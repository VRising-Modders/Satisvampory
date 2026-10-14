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
    /// Craft and build from storage (spec 06, 11 P3) over Progenitor's ctx.MaterialSource.
    /// Progenitor counts, pays and keeps check and payment together; this only answers which
    /// chests, in what order and with what reserve, from <see cref="MaterialPolicy"/>. An empty
    /// answer means "not governed here" and the game's own rules apply.
    /// </summary>
    internal sealed class MaterialService : IMaterialSourceProvider
    {
        /// <summary>How long one resolved island serves repeated questions (one check asks once per item).</summary>
        const long CacheMs = 1000;

        readonly SatisContext _ctx;
        readonly ServerHalf _half;
        readonly Dictionary<MaterialContext, (long Until, long Revision, Resolved? Island)> _cache = new();

        sealed class Resolved
        {
            public DepositContext Deposit = null!;
            public CastleRules Rules = null!;
            public List<PgId> Candidates = new();
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
            new[] { MaterialOperation.Build, MaterialOperation.Craft };

        public long Revision(in MaterialContext context)
        {
            // Settings and list edits bump the policy; raid state and chest contents are picked up
            // by a slow time bucket. Payments re-resolve regardless.
            var allowed = Allowed(context, out _) ? 1L : 0L;
            return unchecked(_half.MaterialPolicy.Revision * 1_000_003L + allowed * 7919L + Environment.TickCount64 / 30_000);
        }

        public IReadOnlyList<PgId> Candidates(in MaterialContext context) =>
            Resolve(context)?.Candidates ?? (IReadOnlyList<PgId>)Array.Empty<PgId>();

        public ItemSources? Sources(in MaterialContext context, int item)
        {
            var island = Resolve(context);
            if (island == null) return null;
            var use = context.Operation == MaterialOperation.Build ? MaterialUse.Build : MaterialUse.Craft;
            var answer = _half.MaterialPolicy.Sources(use, serverAllows: true, playerAllows: true, island.Deposit, island.Rules, new ItemId(item));
            if (answer is not { } a) return null;
            var station = context.Station;
            var order = a.SpendOrder.Select(id => new PgId(id.Value)).Where(id => !id.Equals(station)).ToList();
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
            if (_cache.TryGetValue(context, out var hit) && hit.Until > now && hit.Revision == revision) return hit.Island;
            if (_cache.Count > 256) _cache.Clear();

            Resolved? result = null;
            try
            {
                result = Build(context);
            }
            catch (Exception e)
            {
                _ctx.Log.LogError("Satisvampory: material sources failed: " + e.Message);
            }
            _cache[context] = (now + CacheMs, revision, result);
            return result;
        }

        Resolved? Build(in MaterialContext context)
        {
            if (!Allowed(context, out var user)) return null;
            var island = ServerActions.Resolve(_ctx, _half, context.User.ToEntity(), user);
            // Only the castle's own people craft from its chests; allies and visitors stay vanilla.
            if (island == null || island.Relation is not (CastleRelation.Owner or CastleRelation.ClanMember)) return null;
            if (context.StandingTerritory >= 0 && island.Standing.Territory.Index != context.StandingTerritory) return null;

            var world = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, island.Castles,
                id => island.State.Deposit.TryGetValue(id, out var d) ? d : null);
            var deposit = new DepositContext(_half.Catalog.Current.WithEdits(island.State.Categories), world, island.Scope, ProgenitorWorld.PlotOf(island.Standing))
            {
                Settings = island.State.MassDeposit,
                Belts = island.State.Graph.BeltMembers(),
            };
            var station = context.Station;
            var candidates = Scope.Chests(world, island.Scope)
                .Where(c => c.Deposit.Special is SpecialKind.None or SpecialKind.Overflow or SpecialKind.Spoils)
                .Select(c => new PgId(c.Id.Value))
                .Where(id => !id.Equals(station))
                .ToList();
            return new Resolved { Deposit = deposit, Rules = island.State.Rules, Candidates = candidates };
        }
    }
}
