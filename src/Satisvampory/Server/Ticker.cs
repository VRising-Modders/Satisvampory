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

        /// <summary>Blood Essence's prefab guid (a game fact). Replaced by a catalog lookup once ctx.Items.Catalog lands.</summary>
        const int BloodEssence = 862477668;

        readonly SatisContext _ctx;
        readonly ServerHalf _half;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly Dictionary<string, TickState> _tickStates = new();
        readonly Dictionary<PlotId, double> _heartFed = new();
        readonly Dictionary<string, TickReport> _lastReports = new();

        /// <summary>The last conveyor report for an island, for the panels' row status lines.</summary>
        internal TickReport? LastReport(string islandKey) => _lastReports.TryGetValue(islandKey, out var r) ? r : null;
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
            var done = new HashSet<PlotId>();
            var budget = server.MovesPerTick;

            foreach (var castle in castles)
            {
                if (budget <= 0) break;
                var plot = ProgenitorWorld.PlotOf(castle);
                if (done.Contains(plot)) continue;

                var (key, members) = IslandOf(castle, castles, sharing);
                foreach (var m in members) done.Add(ProgenitorWorld.PlotOf(m));

                var save = Stores.Island(_ctx.Save, key);
                if (save == null || save.Containers.Count == 0) continue;
                var state = SaveCodec.Restore(save, out _);

                var world = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, members, id => state.Deposit.TryGetValue(id, out var d) ? d : null)
                {
                    TableRecipesOn = id => state.TableRecipesOn.TryGetValue(id, out var on) ? on : null,
                };
                var scope = members.Select(ProgenitorWorld.PlotOf).ToList();
                var deposit = new DepositContext(_half.Catalog.Current.WithEdits(state.Categories), world, scope, plot)
                {
                    Settings = state.MassDeposit,
                    Belts = state.Graph.BeltMembers(),
                };
                RunSpecials(world, members, server);
                if (state.Graph.Lists.Count == 0) continue;
                if (!_tickStates.TryGetValue(key, out var tickState)) _tickStates[key] = tickState = new TickState();
                foreach (var (loopKey, mode) in state.LoopModes)
                {
                    if (!tickState.Loops.TryGetValue(loopKey, out var ls)) tickState.Loops[loopKey] = ls = new Engine.Loops.LoopState();
                    ls.Mode = mode;
                }

                var report = new ConveyorTick(world, deposit, state.Graph, state.Rules, tickState, new TickOptions
                {
                    MovesPerPlot = server.MovesPerPlot,
                    MovesPerTick = budget,
                    LoopMovesPerTick = server.LoopMovesPerTick,
                    LoopsAllowed = server.AllowLoops,
                }).Run(Now);
                budget -= report.Moves.Count;
                _lastReports[key] = report;
            }
        }

        /// <summary>
        /// The island a castle belongs to for background work, from its owner's sharing choices:
        /// the clan's taking-part castles under clan share, the owner's castles under castle share,
        /// else the castle alone. Raided castles never join.
        /// </summary>
        (string Key, List<ICastleHandle> Members) IslandOf(ICastleHandle castle, List<ICastleHandle> all, SharingState sharing)
        {
            var owner = _half.PlayerFor(castle.Owner.PlatformId);
            bool Excluded(ICastleHandle c) => Stores.Castle(_ctx.Save, c.Territory.Index).ExcludeFromSharing;
            if (Excluded(castle)) return ("castle/" + castle.Territory.Index, new List<ICastleHandle> { castle });
            if (castle.Owner.HasClan && sharing.ClanShare(owner.TakePartInClanShare))
            {
                var clan = _ctx.Clans.Of(castle.Owner.User.ToEntity());
                if (!clan.IsNone)
                {
                    var members = _ctx.Clans.Castles(clan)
                        .Where(c => c.Enabled && !c.Raided && !Excluded(c) && sharing.ClanShare(_half.PlayerFor(c.Owner.PlatformId).TakePartInClanShare))
                        .ToList();
                    if (!members.Any(c => ProgenitorWorld.PlotOf(c) == ProgenitorWorld.PlotOf(castle))) members.Add(castle);
                    return ("clan/" + clan.ClanGuid.ToString("N"), members);
                }
            }
            if (sharing.CastleShare(owner.ShareBetweenMyCastles))
            {
                var mine = all.Where(c => c.Owner.PlatformId == castle.Owner.PlatformId && !c.Raided && !Excluded(c)).ToList();
                if (mine.Count > 1) return ("owner/" + castle.Owner.PlatformId, mine);
            }
            return ("castle/" + castle.Territory.Index, new List<ICastleHandle> { castle });
        }

        /// <summary>Salvage, spawner and brazier chests work on their own plot (spec 09).</summary>
        void RunSpecials(ProgenitorWorld world, List<ICastleHandle> members, ServerSettings server)
        {
            foreach (var castle in members)
            {
                var plot = ProgenitorWorld.PlotOf(castle);
                var castleSettings = Stores.Castle(_ctx.Save, castle.Territory.Index);
                var sw = new SpecialSwitches
                {
                    AllowSalvage = server.AllowSalvage,
                    CastleFeedsSalvage = castleSettings.FeedSalvage,
                    AllowSpawner = server.AllowSpawnerChests,
                    AllowBrazier = server.AllowBrazierChests,
                    // Lighting braziers needs a Progenitor brazier API; fuel still flows.
                    AllowNightAndProximity = false,
                    AllowTrash = server.AllowTrash,
                };
                var report = new JobReport();
                SpecialJobs.Salvage(world, plot, sw, report);
                SpecialJobs.Spawners(world, plot, sw, report);
                SpecialJobs.Braziers(world, plot, sw, isNight: false, players: Array.Empty<(float, float)>(), report);
            }
        }

        static SharingState Sharing(ServerSettings s) => new()
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
                    TopUp(castle, all, server);
                    _heartFed[plot] = Now;
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
            var save = Stores.Island(_ctx.Save, key);
            var state = save == null ? new IslandState() : SaveCodec.Restore(save, out _);
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

            var blood = new ItemId(BloodEssence);
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
