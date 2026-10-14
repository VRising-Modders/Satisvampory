// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Materials;
using ProjectM.Network;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.World;
using Satisvampory.World;
using Unity.Entities;
using PgId = Progenitor.Inventory.ContainerId;

namespace Satisvampory.Server
{
    /// <summary>
    /// Craft, build, repair and heart upgrades from storage (spec 06, 11 P3) over Progenitor's ctx.MaterialSource.
    /// Progenitor counts, pays and keeps check and payment together; this only answers which
    /// chests, in what order and with what reserve, from <see cref="MaterialPolicy"/>. An empty
    /// answer means "not governed here" and the game's own rules apply.
    /// </summary>
    internal sealed class MaterialService : IMaterialSourceProvider
    {
        /// <summary>
        /// How long one resolved island and its per-item answers serve. Progenitor asks every online
        /// player's totals every second, for each operation in view, and asks once per item; the answer
        /// for a given player and plot does not depend on the operation, so one cached island serves
        /// all of them. Payments re-check counts themselves, so a few seconds of staleness only affects
        /// the order chests are drained in.
        /// </summary>
        const long CacheMs = 30000;

        // Time spent inside this provider; warns when a 30 s window costs more than a second.
        long _spentTicks, _lastReport = Environment.TickCount64;
        int _calls;

        readonly SatisContext _ctx;
        readonly ServerHalf _half;
        readonly Dictionary<(CastleId User, int Territory), (long Until, long Revision, Resolved? Island)> _cache = new();

        sealed class Resolved
        {
            public DepositContext Deposit = null!;
            public CastleRules Rules = null!;
            public List<PgId> Candidates = new();
            /// <summary>Per item: the spend order and reserve, computed once per cache life.</summary>
            public Dictionary<int, MaterialAnswer?> Answers = new();
        }

        internal MaterialService(SatisContext ctx, ServerHalf half)
        {
            _ctx = ctx;
            _half = half;
        }

        internal void Attach()
        {
            _ctx.Track(_ctx.MaterialSource.Register(this));
            _half.MaterialPolicy.Changed += () =>
            {
                _cache.Clear();
                _ctx.MaterialSource.InvalidateAll();
            };
        }

        public IReadOnlyCollection<MaterialOperation> Operations { get; } =
            new[] { MaterialOperation.Build, MaterialOperation.Craft, MaterialOperation.Repair, MaterialOperation.Upgrade };

        public long Revision(in MaterialContext context)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return RevisionCore(context); }
            finally { Spent(t0); }
        }

        long RevisionCore(in MaterialContext context)
        {
            // Settings and list edits bump the policy; raid state and chest contents are picked up
            // by a slow time bucket. Payments re-resolve regardless.
            var allowed = Allowed(context, out _) ? 1L : 0L;
            return unchecked(_half.MaterialPolicy.Revision * 1_000_003L + allowed * 7919L + Environment.TickCount64 / 30_000);
        }

        public IReadOnlyList<PgId> Candidates(in MaterialContext context)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return CandidatesCore(context); }
            finally { Spent(t0); }
        }

        IReadOnlyList<PgId> CandidatesCore(in MaterialContext context)
        {
            var island = Resolve(context);
            if (island == null) return Array.Empty<PgId>();
            var station = context.Station;
            return station.IsNone ? island.Candidates : island.Candidates.Where(id => !id.Equals(station)).ToList();
        }

        public ItemSources? Sources(in MaterialContext context, int item)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return SourcesCore(context, item); }
            finally { Spent(t0); }
        }

        void Spent(long t0)
        {
            _spentTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            _calls++;
            var now = Environment.TickCount64;
            if (now - _lastReport < 30_000) return;
            var ms = _spentTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms > 1000) _ctx.Log.LogWarning($"Satisvampory: material sources spent {ms:0} ms in {_calls} calls over the last {(now - _lastReport) / 1000} s");
            _spentTicks = 0;
            _calls = 0;
            _lastReport = now;
        }

        ItemSources? SourcesCore(in MaterialContext context, int item)
        {
            var island = Resolve(context);
            if (island == null) return null;
            var use = context.Operation switch
            {
                MaterialOperation.Build => MaterialUse.Build,
                MaterialOperation.Repair => MaterialUse.Repair,
                MaterialOperation.Upgrade => MaterialUse.Upgrade,
                _ => MaterialUse.Craft,
            };
            if (!island.Answers.TryGetValue(item, out var answer))
                island.Answers[item] = answer = _half.MaterialPolicy.Sources(use, serverAllows: true, playerAllows: true, island.Deposit, island.Rules, new ItemId(item));
            if (answer is not { } a) return null;
            var station = context.Station;
            var order = a.SpendOrder.Select(id => new PgId(id.Value)).Where(id => !id.Equals(station)).ToList();
            // A governed item with no sources counts as zero in the game's check and would refuse the
            // whole operation; when the island holds none, leave that item to the game's own rules.
            if (order.Count == 0) return null;
            return new ItemSources(order, a.PoolReserve);
        }

        bool Allowed(in MaterialContext context, out User user)
        {
            user = default;
            var em = _ctx.World!.EntityManager;
            var userEntity = context.User.ToEntity();
            if (!em.Exists(userEntity) || !em.HasComponent<User>(userEntity)) return false;
            user = em.GetComponentData<User>(userEntity);
            return _half.Config.Snapshot().AllowCraftFromStorage && _half.PlayerFor(user.PlatformId).CraftFromStorage;
        }

        /// <summary>The actor's island for this context, or null when it is not governed.</summary>
        Resolved? Resolve(in MaterialContext context)
        {
            var now = Environment.TickCount64;
            var revision = _half.MaterialPolicy.Revision;
            var key = (context.User, context.StandingTerritory);
            if (_cache.TryGetValue(key, out var hit) && hit.Until > now && hit.Revision == revision) return hit.Island;
            if (_cache.Count > 256) _cache.Clear();

            Resolved? result = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                result = Build(context);
                if (watch.ElapsedMilliseconds > 250)
                    _ctx.Log.LogWarning($"Satisvampory: material sources island took {watch.ElapsedMilliseconds} ms ({result?.Candidates.Count ?? 0} chests)");
            }
            catch (Exception e)
            {
                _ctx.Log.LogError("Satisvampory: material sources failed: " + e.Message);
            }
            _cache[key] = (now + CacheMs, revision, result);
            return result;
        }

        Resolved? Build(in MaterialContext context)
        {
            if (!Allowed(context, out var user)) return null;
            var island = ServerActions.Resolve(_ctx, _half, context.User.ToEntity(), user);
            // Only the castle's own people craft from its chests; allies and visitors stay vanilla.
            if (island == null || island.Relation is not (CastleRelation.Owner or CastleRelation.ClanMember)) return null;
            if (context.StandingTerritory >= 0 && island.Standing.Territory.Index != context.StandingTerritory) return null;

            // One read per chest for the cache's life; every per-item ranking then runs from memory.
            var world = new SnapshotView(new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, island.Castles,
                id => island.State.Deposit.TryGetValue(id, out var d) ? d : null));
            var deposit = new DepositContext(_half.Catalog.Current.WithEdits(island.State.Categories), world, island.Scope, ProgenitorWorld.PlotOf(island.Standing))
            {
                Settings = island.State.MassDeposit,
                Belts = island.State.Graph.BeltMembers(),
            };
            var candidates = Scope.Chests(world, island.Scope)
                .Where(c => c.Deposit.Special is SpecialKind.None or SpecialKind.Overflow or SpecialKind.Spoils)
                .Select(c => new PgId(c.Id.Value))
                .ToList();
            return new Resolved { Deposit = deposit, Rules = island.State.Rules, Candidates = candidates };
        }
    }
}
