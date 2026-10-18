// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Dev;
using Satisvampory.Engine.Logging;
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
        // ---------------- material facts ----------------

        const double MaterialFactsSeconds = 5;
        double _lastMaterialFacts;
        long _materialFacts;

        /// <summary>
        /// Craft-from-storage answers depend on which plots are castles, which are raided, and which
        /// containers stand on them. Progenitor reuses a player's view until the provider's revision
        /// moves, so move it when one of these changes, and only then.
        /// </summary>
        void WatchMaterialFacts(List<ICastleHandle> castles)
        {
            if (Now - _lastMaterialFacts < MaterialFactsSeconds) return;
            _lastMaterialFacts = Now;
            long sig = castles.Count;
            foreach (var c in castles.OrderBy(c => c.Territory.Index))
            {
                sig = unchecked(sig * 31 + c.Territory.Index * 2 + (c.Raided ? 1 : 0));
                sig = unchecked(sig * 31 + c.StructureRevision);
            }
            if (sig == _materialFacts) return;
            var first = _materialFacts == 0;
            _materialFacts = sig;
            if (!first) _half.MaterialPolicy.Invalidate();
        }

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
                    var ids = new HashSet<Engine.Model.ContainerId>(ProgenitorWorld.ContainerIds(group, cachedRows: true));
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

        readonly HashSet<(string Island, string Loop)> _stalled = new();
        double _lastTickMs;
        long _movedSinceLog;
        int _listIslandTicks;
        readonly Dictionary<string, int> _diagSinceLog = new();
        int _moveRecordsSinceLog;
        double _lastMoveLog;
        int _lastIslands, _lastMoves;

        internal string Diagnostics(ServerHalf half) =>
            $"Last tick {_lastTickMs:0.0} ms over {_lastSlices} frames · {_lastIslands} islands · {_lastMoves} moves · {_recent.Values.Sum(l => l.Count)} moves kept for status · "
            + $"{_missingSince.Count} chests waiting to be forgotten · catalog {half.Catalog.ItemCount} items";

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
            StressOwners.Bind(ctx);
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

        /// <summary>
        /// The most main-thread time one frame gives the conveyor. A tick's islands are worked through in
        /// slices of about this long, one per frame, so many castles never stall the server in one frame.
        /// </summary>
        internal const double SliceMs = 25;

        /// <summary>One tick's pass over every island, possibly spread over several frames.</summary>
        sealed class Cycle
        {
            public ServerSettings Server = null!;
            public SharingState Sharing = null!;
            public List<ICastleHandle> Castles = null!;
            public HashSet<PlotId> Done = new();
            public int Start, Next, Budget, Slices;
            public double SpentMs;
        }

        Cycle? _cycle;

        void Tick()
        {
            if (_stopped) return;
            try
            {
                _half.Catalog.Refresh(_ctx);
                var server = _half.Config.Snapshot();
                if (server.AllowLogistics && BeginCycle(server)) { Slice(); return; }
            }
            catch (Exception e)
            {
                _ctx.Log.LogError("Satisvampory: conveyor tick failed: " + e);
            }
            if (!_stopped) MainThread.PostDelayed(TickSeconds, Tick, _ctx.Owner);
        }

        bool BeginCycle(ServerSettings server)
        {
            // An unowned castle (no player, e.g. freshly generated or abandoned) has no island to run:
            // grouped by owner they would all join one huge "owner 0" island and stall the tick.
            var castles = _ctx.Castles.All().Where(c => c.Enabled && c.Owner.PlatformId != 0 && !StressOwners.Is(c.Owner.PlatformId)).ToList();
            SweepDestroyed(castles);
            WatchMaterialFacts(castles);
            if (castles.Count == 0) return false;
            _lastIslands = 0;
            _cycle = new Cycle
            {
                Server = server, Sharing = Sharing(server), Castles = castles, Budget = server.MovesPerTick,
                // Start somewhere new each tick so islands late in the list are not starved of budget.
                Start = _rotate++ % castles.Count,
            };
            return true;
        }

        /// <summary>Islands until this frame's slice is spent; the rest next frame; the next tick once all are done.</summary>
        void Slice()
        {
            if (_stopped) return;
            var c = _cycle;
            if (c == null) { MainThread.PostDelayed(TickSeconds, Tick, _ctx.Owner); return; }
            var watch = Stopwatch.StartNew();
            try
            {
                while (c.Next < c.Castles.Count && c.Budget > 0)
                {
                    var castle = c.Castles[(c.Start + c.Next++) % c.Castles.Count];
                    var plot = ProgenitorWorld.PlotOf(castle);
                    if (c.Done.Contains(plot) || !castle.Enabled) continue;
                    var (key, members) = IslandOf(castle, c.Castles, c.Sharing);
                    foreach (var m in members) c.Done.Add(ProgenitorWorld.PlotOf(m));
                    try
                    {
                        c.Budget -= RunIsland(c.Server, key, members, plot, c.Budget, SliceMs - watch.Elapsed.TotalMilliseconds);
                        _lastIslands++;
                        _lastMoves = c.Server.MovesPerTick - c.Budget;
                    }
                    catch (Exception e)
                    {
                        _ctx.Log.LogError($"Satisvampory: island {key} failed this tick: {e.Message}");
                    }
                    if (watch.Elapsed.TotalMilliseconds >= SliceMs) break;
                }
            }
            catch (Exception e)
            {
                _ctx.Log.LogError("Satisvampory: conveyor slice failed: " + e);
                c.Next = c.Castles.Count;
            }
            var ms = watch.Elapsed.TotalMilliseconds;
            c.SpentMs += ms;
            c.Slices++;
            _maxTickMsSinceLog = Math.Max(_maxTickMsSinceLog, ms);
            if (c.Next < c.Castles.Count && c.Budget > 0)
            {
                MainThread.PostDelayed(0.02, Slice, _ctx.Owner); // the next frame or so
                return;
            }
            _lastTickMs = c.SpentMs;
            _lastSlices = c.Slices;
            _cycle = null;
            MainThread.PostDelayed(TickSeconds, Tick, _ctx.Owner);
        }

        int _lastSlices;

        // ---------------- idle skip ----------------

        /// <summary>The longest an island goes without a full tick, for time-based work (leftover grace, loop timers).</summary>
        const double IdleMaxSeconds = 60;

        sealed class IdleState
        {
            public long Layout;
            public long Inventory;
            public double LastFull;
            public bool Settled;
        }

        readonly Dictionary<string, IdleState> _idle = new();
        int _skippedSinceLog;
        double _maxTickMsSinceLog;
        double _skippedMsSinceLog;

        static string IslandId(string key, List<ICastleHandle> members) =>
            key + "|" + string.Join(",", members.Select(m => m.Territory.Index).OrderBy(i => i));

        /// <summary>Everything a tick's work depends on besides inventories: settings writes, built or destroyed containers, scope and budgets.</summary>
        long LayoutSignature(string key, List<ICastleHandle> members, ServerSettings server)
        {
            var h = new HashCode();
            h.Add(key);
            h.Add(Stores.Revision);
            h.Add(_half.Catalog.ItemCount);
            h.Add(server.MovesPerTick); h.Add(server.MovesPerPlot); h.Add(server.LoopMovesPerTick); h.Add(server.AllowLoops);
            h.Add(server.AllowSalvage); h.Add(server.AllowSpawnerChests); h.Add(server.AllowTrash);
            foreach (var m in members.OrderBy(m => m.Territory.Index))
            {
                h.Add(m.Territory.Index);
                h.Add(m.StructureRevision);
                h.Add(m.Raided);
            }
            return (uint)h.ToHashCode() | ((long)members.Count << 32);
        }

        /// <summary>
        /// A hash of every inventory on the island (main and output parts), read once each. The reads
        /// are kept in <paramref name="reads"/> so a tick that runs does not read them again.
        /// </summary>
        static long InventoryFingerprint(ProgenitorWorld world, IEnumerable<PlotId> plots,
            Dictionary<(Engine.Model.ContainerId, InvPart), Engine.Model.ItemStack[]>? reads = null)
        {
            unchecked
            {
                long h = 17;
                foreach (var plot in plots)
                    foreach (var c in world.ContainersOn(plot))
                    {
                        h = h * 31 + c.Id.GetHashCode();
                        foreach (var part in c.Kind == NodeKind.Station ? new[] { InvPart.Main, InvPart.Output } : new[] { InvPart.Main })
                        {
                            var stacks = world.Contents(c.Id, part);
                            if (reads != null) reads[(c.Id, part)] = stacks as Engine.Model.ItemStack[] ?? stacks.ToArray();
                            foreach (var stack in stacks)
                                h = h * 1_000_003 + stack.Item.Guid * 7919L + stack.Amount + (int)part;
                        }
                    }
                return h;
            }
        }

        /// <summary>One island's specials and conveyor tick; returns the moves it made.</summary>
        int RunIsland(ServerSettings server, string key, List<ICastleHandle> members, PlotId plot, int budget, double timeLeftMs = SliceMs)
        {
            // Skip the whole tick when nothing it reads changed since a tick that did nothing: no settings
            // or structure change, the same inventories, and no timer waiting (leftovers, loops).
            var check = Stopwatch.StartNew();
            var layout = LayoutSignature(key, members, server);
            var plots = members.Select(ProgenitorWorld.PlotOf).ToList();
            // Several islands can share one settings key (a clan's castles split by sharing rules), so
            // per-island caches are keyed by the island's own plots as well.
            var island = IslandId(key, members);
            var peek = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, members, _ => null, cachedRows: true);
            var reads = new Dictionary<(Engine.Model.ContainerId, InvPart), Engine.Model.ItemStack[]>();
            var inventory = InventoryFingerprint(peek, plots, reads);
            if (!_idle.TryGetValue(island, out var idle)) _idle[island] = idle = new IdleState();
            if (idle.Settled && idle.Layout == layout && idle.Inventory == inventory && Now - idle.LastFull < IdleMaxSeconds)
            {
                _skippedSinceLog++;
                _skippedMsSinceLog += check.Elapsed.TotalMilliseconds;
                return 0;
            }
            var moved = RunIslandFull(server, key, members, plot, budget, reads, timeLeftMs, out var pending);
            idle.Layout = layout;
            // Moves change inventories; read them again so the next tick compares against the result.
            idle.Inventory = moved > 0 ? InventoryFingerprint(new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, members, _ => null, cachedRows: true), plots) : inventory;
            idle.Settled = moved == 0 && !pending;
            idle.LastFull = Now;
            return moved;
        }

        int RunIslandFull(ServerSettings server, string key, List<ICastleHandle> members, PlotId plot, int budget,
            IReadOnlyDictionary<(Engine.Model.ContainerId, InvPart), Engine.Model.ItemStack[]> reads, double timeLeftMs, out bool pending)
        {
            pending = false;
            var phases = Stopwatch.StartNew();
            var marks = new List<string>();
            void Mark(string name) { marks.Add($"{name} {phases.Elapsed.TotalMilliseconds:0}"); }
            try { return RunIslandFullCore(server, key, members, plot, budget, reads, timeLeftMs, out pending, Mark); }
            finally
            {
                if (phases.Elapsed.TotalMilliseconds > 100)
                    _ctx.Log.LogInfo($"Satisvampory: slow island tick {key}: {string.Join(", ", marks)} ms");
            }
        }

        int RunIslandFullCore(ServerSettings server, string key, List<ICastleHandle> members, PlotId plot, int budget,
            IReadOnlyDictionary<(Engine.Model.ContainerId, InvPart), Engine.Model.ItemStack[]> reads, double timeLeftMs, out bool pending, Action<string> Mark)
        {
            pending = false;
            {
                var state = Stores.Load(_ctx.Save, key, ProgenitorWorld.ContainerIds(members, cachedRows: true));
                Mark("load");
                var configured = state.Graph.Lists.Count;
                var world = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, members, id => state.Deposit.TryGetValue(id, out var d) ? d : null, cachedRows: true, logistics: state)
                {
                    TableRecipesOn = id => state.TableRecipesOn.TryGetValue(id, out var on) ? on : null,
                };
                Mark("world");
                if (state.Graph.Lists.Count != configured || state.DefaultBeltMigrated) Stores.Save(_ctx.Save, key, state);
                // A castle with Logistics off takes no part in conveyors (spec 10).
                var scope = members.Where(m => Stores.Castle(_ctx.Save, m.Territory.Index).LogisticsOn).Select(ProgenitorWorld.PlotOf).ToList();
                var deposit = new DepositContext(_half.Catalog.Current.WithEdits(state.Categories), world, scope, plot)
                {
                    Settings = state.MassDeposit,
                    Belts = state.Graph.BeltMembers(),
                };
                Mark("context");
                var used = RunSpecials(world, members, server);
                Mark("specials");
                if (state.Graph.Lists.Count == 0 || scope.Count == 0)
                    return used;
                var island = IslandId(key, members);
                if (!_tickStates.TryGetValue(island, out var tickState)) _tickStates[island] = tickState = new TickState();
                foreach (var (loopKey, mode) in state.LoopModes)
                {
                    if (!tickState.Loops.TryGetValue(loopKey, out var ls)) tickState.Loops[loopKey] = ls = new Engine.Loops.LoopState();
                    ls.Mode = mode;
                }

                var toClear = state.StationsToClear.Count;
                var clearHere = state.StationsToClear.Any(id => world.Find(id) != null);
                var report = new ConveyorTick(world, deposit, state.Graph, state.Rules, tickState, new TickOptions
                {
                    LayoutSignature = LayoutSignature(key, members, server),
                    // Specials may have moved items since these reads; then the tick reads fresh.
                    Prefetched = used == 0 ? reads : null,
                    MovesPerPlot = server.MovesPerPlot,
                    MovesPerTick = Math.Max(0, budget - used),
                    LoopMovesPerTick = server.LoopMovesPerTick,
                    LoopsAllowed = server.AllowLoops,
                    StationsToClear = state.StationsToClear,
                    // What is left of this frame's slice (at least a little, so a big island always makes progress).
                    StationTimeBudgetMs = Math.Max(8, timeLeftMs),
                }).Run(Now);
                Mark("conveyor");
                foreach (var (phase, ms) in report.Timings) Mark("  " + phase + "@" + ms.ToString("0"));
                _lastReports[key] = report;
                // A sleeping loop wakes on an outside change, which the inventory fingerprint sees;
                // only a leftover's grace timer needs ticks to pass.
                pending = tickState.LeftoverSince.Count > 0 || (clearHere && state.StationsToClear.Any(id => world.Find(id) != null));
                if (state.StationsToClear.Count != toClear)
                {
                    Stores.Save(_ctx.Save, key, state);
                    _ctx.Log.LogInfo($"Satisvampory: station cleanup on {key}: {toClear - state.StationsToClear.Count} station(s) emptied back to storage, {state.StationsToClear.Count} to go");
                    var catalog = _half.Catalog.Current;
                    foreach (var g in report.Moves.Where(m => m.Why == "station cleanup").GroupBy(m => m.From))
                    {
                        var c = world.Find(g.Key);
                        _ctx.Log.LogInfo($"Satisvampory: station cleanup: {c?.DisplayName ?? g.Key.ToString()} [{g.Key}] on plot {c?.Plot}: "
                            + string.Join(", ", g.GroupBy(m => m.Item).Select(x => $"{x.Sum(m => m.Amount)} x {catalog.NameOf(x.Key)}")));
                    }
                    foreach (var id in state.StationsToClear.Where(id => world.Find(id) != null))
                        _ctx.Log.LogInfo($"Satisvampory: station cleanup waiting on {world.Find(id)?.DisplayName ?? id.ToString()} [{id}]: "
                            + (report.Diagnostics.TryGetValue(id, out var why) ? string.Join(", ", why.Where(w => w.StartsWith("cleanup-")).Distinct().Take(6)) : "not reached"));
                }
                _listIslandTicks++;
                foreach (var codes in report.Diagnostics.Values)
                    foreach (var code in codes) _diagSinceLog[code] = (_diagSinceLog.TryGetValue(code, out var dn) ? dn : 0) + 1;
                _movedSinceLog += report.Moves.Sum(m => m.Amount);
                _moveRecordsSinceLog += report.Moves.Count;
                if (Now - _lastMoveLog >= 60 && _listIslandTicks > 0)
                {
                    _ctx.Log.LogInfo($"Satisvampory: lists moved {_movedSinceLog:N0} items in {_moveRecordsSinceLog} moves in the last {Now - _lastMoveLog:0} s ({_listIslandTicks} island ticks with lists, {_skippedSinceLog} idle ticks skipped at {(_skippedSinceLog == 0 ? 0 : _skippedMsSinceLog / _skippedSinceLog):0.0} ms each, slowest frame slice {_maxTickMsSinceLog:0} ms)"
                        + (_diagSinceLog.Count == 0 ? "" : "; waiting: " + string.Join(", ", _diagSinceLog.Select(kv => $"{kv.Key} x{kv.Value}"))));
                    _diagSinceLog.Clear();
                    _listIslandTicks = 0;
                    _skippedSinceLog = 0;
                    _skippedMsSinceLog = 0;
                    _maxTickMsSinceLog = 0;
                    _movedSinceLog = 0;
                    _moveRecordsSinceLog = 0;
                    _lastMoveLog = Now;
                }
                foreach (var (loopKey, ls) in tickState.Loops)
                {
                    var was = _stalled.Contains((key, loopKey));
                    if (ls.Stalled && !was)
                    {
                        _stalled.Add((key, loopKey));
                        _ctx.Log.LogInfo($"Satisvampory: a loop on {key} kept moving for 60 s with no outside change and was stopped ({loopKey}).");
                    }
                    else if (!ls.Stalled && was) _stalled.Remove((key, loopKey));
                }
                if (report.Moves.Count > 0)
                {
                    var names = ServerActions.PlotNames(_ctx.Save, members);
                    foreach (var g in report.Moves.GroupBy(m => m.Why))
                        ServerActions.LogMoves(_half, world, names, g.Select(m => (m.From, m.To, m.Item, m.Amount, m.For)), MoveLog.Why(g.Key));
                }
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
                var feed = new HeartFeedPrefs
                {
                    OnVisit = server.HeartTopUpOnVisit,
                    OnLoginIsland = server.HeartTopUpIslandOnLogin,
                    OutsideIsland = server.HeartTopUpOutsideIsland,
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
                        AutoFeedOn: true, // heart feed is the server's call (admin settings), not a castle switch
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
            var state = Stores.Load(_ctx.Save, key, ProgenitorWorld.ContainerIds(members, cachedRows: true));
            var world = new ProgenitorWorld(_ctx.Inventory, _ctx.Stations, members, id => state.Deposit.TryGetValue(id, out var d) ? d : null, cachedRows: true, logistics: state)
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
            var total = members.SelectMany(c => world.ContainersOn(ProgenitorWorld.PlotOf(c))).Where(c => c.Shared).Sum(c => (long)world.Count(c.Id, blood));
            foreach (var (from, amount) in HeartFeed.Plan(heart, sources, total, state.Rules.Reserve(blood, deposit.Catalog)))
                world.Transfer(from, InvPart.Main, heartId, blood, amount);
        }
    }
}
