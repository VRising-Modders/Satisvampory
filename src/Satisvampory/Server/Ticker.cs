// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Dev;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Persistence;
using Satisvampory.Engine.Stations;
using Satisvampory.Engine.World;
using Satisvampory.Net;
using Satisvampory.World;
using Unity.Entities;

namespace Satisvampory.Server
{
    /// <summary>
    /// Server background work: the conveyor tick for every island that has lists (spec 02), and
    /// heart blood feed on plot entry and login (spec 09).
    /// </summary>
    internal sealed class Ticker
    {
        public const double TickSeconds = 5;

        /// <summary>Blood Essence's prefab guid as a fallback (a game fact); the catalog lookup wins when it resolves.</summary>
        const int BloodEssenceFallback = 862477668;

        ItemId BloodEssence => _half.Catalog.Current.TryResolveItem("Blood Essence", out var id) ? id : new ItemId(BloodEssenceFallback);

        readonly SatisContext _ctx;
        readonly ServerHalf _half;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly Dictionary<string, TickState> _tickStates = new();
        readonly Dictionary<PlotId, double> _heartFed = new();
        readonly Dictionary<string, TickReport> _lastReports = new();
        int _rotate;

        /// <summary>The last conveyor report for an island, for the panels' row status lines.</summary>
        // ---------------- destroyed containers (spec 12) ----------------

        const double SweepSeconds = 60, MissingGraceSeconds = 120;
        double _lastSweep;
        readonly Dictionary<(string Key, Engine.Model.ContainerId Id), double> _missingSince = new();
        readonly Dictionary<Engine.Model.ContainerId, int> _removedNotes = new();

        /// <summary>A panel shows this once: how many entries its lists lost because a chest was destroyed.</summary>
        internal string TakeRemovedNote(Engine.Model.ContainerId id)
        {
            if (!_removedNotes.Remove(id, out var n)) return "";
            return n == 1 ? "1 entry removed: chest destroyed" : $"{n} entries removed: chest destroyed";
        }

        /// <summary>
        /// Lists and belts that name a container which is on none of the settings owner's castles
        /// for two minutes lose those entries; the container's own record goes too. The grace
        /// covers castles that load late.
        /// </summary>
        void SweepDestroyed(List<ICastleHandle> castles)
        {
            if (Now - _lastSweep < SweepSeconds) return;
            _lastSweep = Now;
            var like = new PluginContextLike(_ctx);
            foreach (var group in castles.GroupBy(c => Stores.SettingsKey(like, c)))
            {
                try
                {
                    var ids = new HashSet<Engine.Model.ContainerId>(ProgenitorWorld.ContainerIds(group));
                    var state = Stores.Load(_ctx.Save, group.Key, ids);
                    var gone = state.Graph.Referenced().Where(id => !ids.Contains(id)).ToList();
                    foreach (var k in _missingSince.Keys.Where(k => k.Key == group.Key && !gone.Contains(k.Id)).ToList()) _missingSince.Remove(k);
                    var forget = new List<Engine.Model.ContainerId>();
                    foreach (var id in gone)
                    {
                        if (!_missingSince.TryGetValue((group.Key, id), out var since)) _missingSince[(group.Key, id)] = Now;
                        else if (Now - since >= MissingGraceSeconds) forget.Add(id);
                    }
                    if (forget.Count == 0) continue;
                    var removed = 0;
                    foreach (var id in forget)
                    {
                        removed += state.Graph.Forget(id, _removedNotes);
                        _missingSince.Remove((group.Key, id));
                        _ctx.Save.Remove("container/" + id.Value.ToString("N"));
                    }
                    Stores.Save(_ctx.Save, group.Key, state);
                    _half.MaterialPolicy.Invalidate();
                    _ctx.Log.LogInfo($"Satisvampory: {forget.Count} destroyed container(s) left the lists of {group.Key} ({removed} entries).");
                }
                catch (Exception e)
                {
                    _ctx.Log.LogError($"Satisvampory: destroyed-container sweep for {group.Key} failed: {e.Message}");
                }
            }
        }

        internal TickReport? LastReport(string islandKey) => _lastReports.TryGetValue(islandKey, out var r) ? r : null;

        /// <summary>Moves on an island in the last minute (spec 02 status line), newest last.</summary>
        readonly Dictionary<string, List<(double At, MoveRecord Move)>> _recent = new();

        internal IReadOnlyList<MoveRecord> RecentMoves(string islandKey)
        {
            if (!_recent.TryGetValue(islandKey, out var list)) return Array.Empty<MoveRecord>();
            var cutoff = Now - 60;
            list.RemoveAll(x => x.At < cutoff);
            return list.Select(x => x.Move).ToList();
        }
        bool _stopped;

        internal Ticker(SatisContext ctx, ServerHalf half)
        {
            _ctx = ctx;
            _half = half;
        }

        double Now => _clock.Elapsed.TotalSeconds;

        internal void Attach()
        {
            _ctx.Track(new OnDispose(() => _stopped = true));
            _ctx.Events.On<PlayerTerritoryChanged>(OnTerritory);
            _ctx.Events.On<PlayerReady>(OnReady);
            MainThread.PostDelayed(TickSeconds, Tick, _ctx.Owner);
        }

        // ---------------- conveyor ----------------

        void Tick()
        {
            if (_stopped) return;
            try
            {
                _half.Catalog.Refresh(_ctx);
                var server = _half.Config.Snapshot();
                if (server.AllowLogistics) RunIslands(server);
            }
            catch (Exception e)
            {
                _ctx.Log.LogError("Satisvampory: conveyor tick failed: " + e);
            }
            if (!_stopped) MainThread.PostDelayed(TickSeconds, Tick, _ctx.Owner);
        }

        void RunIslands(ServerSettings server)
        {
            var sharing = Sharing(server);
            var castles = _ctx.Castles.All().Where(c => c.Enabled).ToList();
            SweepDestroyed(castles);
            var done = new HashSet<PlotId>();
            var budget = server.MovesPerTick;
            if (castles.Count == 0) return;
            // Start somewhere new each tick so islands late in the list are not starved of budget.
            var start = _rotate++ % castles.Count;

            for (var n = 0; n < castles.Count && budget > 0; n++)
            {
                var castle = castles[(start + n) % castles.Count];
                var plot = ProgenitorWorld.PlotOf(castle);
                if (done.Contains(plot)) continue;

                var (key, members) = IslandOf(castle, castles, sharing);
                foreach (var m in members) done.Add(ProgenitorWorld.PlotOf(m));
                try
                {
                    budget -= RunIsland(server, key, members, plot, budget);
                }
                catch (Exception e)
                {
                    _ctx.Log.LogError($"Satisvampory: island {key} failed this tick: {e.Message}");
                }
            }
        }

        /// <summary>One island's specials and conveyor tick; returns the moves it made.</summary>
        int RunIsland(ServerSettings server, string key, List<ICastleHandle> members, PlotId plot, int budget)
        {
            {
                var state = Stores.Load(_ctx.Save, key, ProgenitorWorld.ContainerIds(members));
                if (state.Graph.Lists.Count == 0 && state.Deposit.Values.All(d => d.Special == SpecialKind.None)) return 0;

                var world = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, members, id => state.Deposit.TryGetValue(id, out var d) ? d : null)
                {
                    TableRecipesOn = id => state.TableRecipesOn.TryGetValue(id, out var on) ? on : null,
                };
                // A castle with Logistics off takes no part in conveyors (spec 10).
                var scope = members.Where(m => Stores.Castle(_ctx.Save, m.Territory.Index).LogisticsOn).Select(ProgenitorWorld.PlotOf).ToList();
                var deposit = new DepositContext(_half.Catalog.Current.WithEdits(state.Categories), world, scope, plot)
                {
                    Settings = state.MassDeposit,
                    Belts = state.Graph.BeltMembers(),
                };
                var used = RunSpecials(world, members, server);
                if (state.Graph.Lists.Count == 0 || scope.Count == 0) return used;
                if (!_tickStates.TryGetValue(key, out var tickState)) _tickStates[key] = tickState = new TickState();
                foreach (var (loopKey, mode) in state.LoopModes)
                {
                    if (!tickState.Loops.TryGetValue(loopKey, out var ls)) tickState.Loops[loopKey] = ls = new Engine.Loops.LoopState();
                    ls.Mode = mode;
                }

                var report = new ConveyorTick(world, deposit, state.Graph, state.Rules, tickState, new TickOptions
                {
                    MovesPerPlot = server.MovesPerPlot,
                    MovesPerTick = Math.Max(0, budget - used),
                    LoopMovesPerTick = server.LoopMovesPerTick,
                    LoopsAllowed = server.AllowLoops,
                }).Run(Now);
                _lastReports[key] = report;
                if (!_recent.TryGetValue(key, out var recent)) _recent[key] = recent = new List<(double, MoveRecord)>();
                recent.RemoveAll(x => x.At < Now - 60);
                if (recent.Count < 5000) recent.AddRange(report.Moves.Select(m => (Now, m)));
                return used + report.Moves.Count;
            }
        }

        /// <summary>
        /// The island a castle belongs to for background work, from its owner's sharing choices:
        /// the clan's taking-part castles under clan share, the owner's castles under castle share,
        /// else the castle alone. Raided castles never join.
        /// </summary>
        internal (string Key, List<ICastleHandle> Members) IslandOf(ICastleHandle castle, List<ICastleHandle> all, SharingState sharing)
        {
            var owner = _half.PlayerFor(castle.Owner.PlatformId);
            bool Excluded(ICastleHandle c) => Stores.Castle(_ctx.Save, c.Territory.Index).ExcludeFromSharing;
            var key = Stores.SettingsKey(new PluginContextLike(_ctx), castle);
            if (Excluded(castle)) return (key, new List<ICastleHandle> { castle });
            if (castle.Owner.HasClan && sharing.ClanShare(owner.TakePartInClanShare))
            {
                var clan = _ctx.Clans.Of(castle.Owner.User.ToEntity());
                if (!clan.IsNone)
                {
                    var members = _ctx.Clans.Castles(clan)
                        .Where(c => c.Enabled && !c.Raided && !Excluded(c) && sharing.ClanShare(_half.PlayerFor(c.Owner.PlatformId).TakePartInClanShare))
                        .ToList();
                    if (!members.Any(c => ProgenitorWorld.PlotOf(c) == ProgenitorWorld.PlotOf(castle))) members.Add(castle);
                    return (key, members);
                }
            }
            if (sharing.CastleShare(owner.ShareBetweenMyCastles))
            {
                var mine = all.Where(c => c.Owner.PlatformId == castle.Owner.PlatformId && !c.Raided && !Excluded(c)).ToList();
                if (mine.Count > 1) return (key, mine);
            }
            return (key, new List<ICastleHandle> { castle });
        }

        /// <summary>Salvage, spawner and brazier chests work on their own plot (spec 09).</summary>
        int RunSpecials(ProgenitorWorld world, List<ICastleHandle> members, ServerSettings server)
        {
            var moves = 0;
            foreach (var castle in members)
            {
                var plot = ProgenitorWorld.PlotOf(castle);
                var castleSettings = Stores.Castle(_ctx.Save, castle.Territory.Index);
                var sw = new SpecialSwitches
                {
                    AllowSalvage = server.AllowSalvage,
                    CastleFeedsSalvage = castleSettings.FeedSalvage,
                    AllowSpawner = server.AllowSpawnerChests,
                    AllowTrash = server.AllowTrash,
                };
                var report = new JobReport();
                SpecialJobs.Salvage(world, plot, sw, report);
                SpecialJobs.Spawners(world, plot, sw, report);
                moves += report.Moves.Count;
            }
            return moves;
        }

        internal static SharingState Sharing(ServerSettings s) => new()
        {
            CastleShareAllowed = s.CastleShare,
            PlayersMayLeaveCastleShare = s.PlayersMayLeaveCastleShare,
            ClanShareAllowed = s.ClanShare,
            PlayersMayLeaveClanShare = s.PlayersMayLeaveClanShare,
        };

        // ---------------- heart blood feed ----------------

        void OnTerritory(PlayerTerritoryChanged e)
        {
            if (e.Castle == null || !e.Castle.Enabled) return;
            Feed(e.User.ToEntity(), HeartTrigger.Visit, e.Castle);
            TellIfShared(e.User.ToEntity());
        }

        /// <summary>Spec 16: a one-time tip the first time a player stands on a shared island. The client keeps the seen state.</summary>
        void TellIfShared(Entity userEntity)
        {
            try
            {
                var user = _ctx.World!.EntityManager.GetComponentData<ProjectM.Network.User>(userEntity);
                var island = ServerActions.Resolve(_ctx, _half, userEntity, user);
                if (island == null || island.Scope.Count <= 1) return;
                if (island.Relation is not (Progenitor.Castles.CastleRelation.Owner or Progenitor.Castles.CastleRelation.ClanMember)) return;
                var n = new NoticePacket { Magic = EventIds.Magic, Code = (int)NoticeCode.SharedIsland, A = island.Scope.Count };
                _ctx.Net.SendToUser(userEntity, EventIds.Notice, n);
            }
            catch (Exception ex)
            {
                _ctx.Log.LogError("Satisvampory: shared island notice failed: " + ex.Message);
            }
        }

        void OnReady(PlayerReady e)
        {
            var standing = _ctx.Castles.At(e.Character.ToEntity());
            if (standing.Enabled) Feed(e.User.ToEntity(), HeartTrigger.Visit, standing);
            Feed(e.User.ToEntity(), HeartTrigger.Login, null);
        }

        void Feed(Entity userEntity, HeartTrigger trigger, ICastleHandle? visited)
        {
            try
            {
                var server = _half.Config.Snapshot();
                if (!server.AllowHeartBloodFeed) return;
                var user = _ctx.World!.EntityManager.GetComponentData<ProjectM.Network.User>(userEntity);
                var prefs = _half.PlayerFor(user.PlatformId);
                var feed = new HeartFeedPrefs
                {
                    OnVisit = prefs.HeartTopUpOnVisit,
                    OnLoginIsland = prefs.HeartTopUpIslandOnLogin,
                    OutsideIsland = prefs.HeartTopUpOutsideIsland,
                };

                var all = _ctx.Castles.All().Where(c => c.Enabled).ToList();
                var anchor = visited ?? _ctx.Castles.ForUser(userEntity);
                if (!anchor.Enabled) return;
                var (key, members) = IslandOf(anchor, all, Sharing(server));
                var island = members.Select(ProgenitorWorld.PlotOf).ToList();

                HeartInfo Info(ICastleHandle c)
                {
                    var fuel = c.HeartFuel();
                    var rel = _ctx.Castles.RelationTo(userEntity, c);
                    return new HeartInfo(ProgenitorWorld.PlotOf(c), fuel.UnlockedSlots, fuel.FuelQuantity,
                        AutoFeedOn: Stores.Castle(_ctx.Save, c.Territory.Index).HeartAutoFeed,
                        OwnedByPlayerOrClan: rel is CastleRelation.Owner or CastleRelation.ClanMember);
                }

                var visitedInfo = visited == null ? null : Info(visited);
                var guest = visited != null && !(visitedInfo!.OwnedByPlayerOrClan);
                var targets = HeartFeed.Which(trigger, feed, true, guest, visitedInfo, island, members.Select(Info));

                foreach (var plot in targets)
                {
                    if (!HeartFeed.CooledDown(_heartFed, plot, Now)) continue;
                    var castle = all.First(c => ProgenitorWorld.PlotOf(c) == plot);
                    _heartFed[plot] = Now;
                    try { TopUp(castle, all, server); }
                    catch (Exception e) { _ctx.Log.LogError($"Satisvampory: heart top-up failed on plot {plot}: {e.Message}"); }
                }
            }
            catch (Exception ex)
            {
                _ctx.Log.LogError("Satisvampory: heart feed failed: " + ex);
            }
        }

        /// <summary>Fill one heart's unlocked pads from its own island, worst-stored first, keeping reserve.</summary>
        void TopUp(ICastleHandle castle, List<ICastleHandle> all, ServerSettings server)
        {
            var (key, members) = IslandOf(castle, all, Sharing(server));
            var state = Stores.Load(_ctx.Save, key, ProgenitorWorld.ContainerIds(members));
            var world = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, members, id => state.Deposit.TryGetValue(id, out var d) ? d : null)
                {
                    TableRecipesOn = id => state.TableRecipesOn.TryGetValue(id, out var on) ? on : null,
                };
            var heartRef = _ctx.Inventory.Of(castle.Heart.ToEntity());
            var heartId = world.AddBag(heartRef);
            var plot = ProgenitorWorld.PlotOf(castle);
            var deposit = new DepositContext(_half.Catalog.Current.WithEdits(state.Categories), world, members.Select(ProgenitorWorld.PlotOf).ToList(), plot)
            {
                Settings = state.MassDeposit,
                Belts = state.Graph.BeltMembers(),
            };

            var blood = BloodEssence;
            var fuel = castle.HeartFuel();
            var heart = new HeartInfo(plot, fuel.UnlockedSlots, fuel.FuelQuantity, true, true);
            var sources = ConveyorTick.HeartSources(world, deposit, state.Graph, new Engine.Model.ContainerId(castle.HeartId.Value), blood)
                .Select(id => (id, world.Count(id, blood))).ToList();
            var total = members.SelectMany(c => world.ContainersOn(ProgenitorWorld.PlotOf(c))).Where(c => c.Kind == NodeKind.Chest).Sum(c => (long)world.Count(c.Id, blood));
            foreach (var (from, amount) in HeartFeed.Plan(heart, sources, total, state.Rules.Reserve(blood, deposit.Catalog)))
                world.Transfer(from, InvPart.Main, heartId, blood, amount);
        }
    }
}
