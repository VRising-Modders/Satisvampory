// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Loops;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Stations;
using Satisvampory.Engine.World;

namespace Satisvampory.Engine.Lists
{
    /// <summary>One move. <paramref name="For"/>: a station feed's product (the recipe it feeds), else none.</summary>
    public sealed record MoveRecord(ContainerId From, ContainerId To, ItemId Item, int Amount, string Why, ItemId For = default);

    /// <summary>State that lives across ticks for one island.</summary>
    public sealed class TickState
    {
        /// <summary>Loop run state by <see cref="Loop.Key"/>.</summary>
        public Dictionary<string, LoopState> Loops { get; } = new();

        /// <summary>When a station's held item was first seen as a leftover, for the grace period.</summary>
        public Dictionary<(ContainerId Station, ItemId Item), double> LeftoverSince { get; } = new();

        /// <summary>Where a station's inputs last came from, so leftovers can go back.</summary>
        public Dictionary<(ContainerId Station, ItemId Item), ContainerId> FedFrom { get; } = new();

        /// <summary>
        /// Edges and loops depend only on the lists and the scope. They are kept between ticks
        /// under the caller's <see cref="TickOptions.LayoutSignature"/>, so an island whose lists
        /// did not change does not rebuild every supplier-to-receiver pair of its belts.
        /// </summary>
        /// <summary>
        /// Stations that got nothing because what they need is not there (not enough, reserve, no source): skipped
        /// until <see cref="Starved.NextTry"/> (5, 10, 20, 40, then 60 s), or at once when the island's stock of any
        /// input changes, the station's own contents change, or it has leftovers waiting.
        /// </summary>
        public Dictionary<ContainerId, Starved> StarvedStations { get; } = new();

        /// <summary>Items a devourer gave back unsalvaged: never fed to it again (this server run).</summary>
        public HashSet<(ContainerId Devourer, ItemId Item)> DevourerRefuses { get; } = new();

        /// <summary>Where station feeding starts next tick, so a time-limited island feeds every station in turn.</summary>
        public int StationCursor { get; set; }

        public sealed class Starved
        {
            public long Supply;
            public long Held;
            public double NextTry;
            public double Delay;
        }

        internal long CachedLayout;
        internal List<Edge>? CachedEdges;
        internal List<ContainerId>? CachedOutOfScope;
        internal List<Loop>? CachedLoops;
    }

    public sealed class TickReport
    {
        /// <summary>Milliseconds into the run at the end of each phase (edges, loops, stations, chests, mass deposit).</summary>
        public List<(string Phase, double Ms)> Timings { get; } = new();
        public List<MoveRecord> Moves { get; } = new();

        /// <summary>Diagnostic codes per container (spec 02, "Diagnostics").</summary>
        public Dictionary<ContainerId, List<string>> Diagnostics { get; } = new();

        public List<Loop> Loops { get; } = new();

        internal void Diag(ContainerId id, string code)
        {
            if (!Diagnostics.TryGetValue(id, out var l)) Diagnostics[id] = l = new List<string>();
            if (!l.Contains(code)) l.Add(code);
        }
    }

    /// <summary>Settings a tick needs beyond the deposit context.</summary>
    public sealed class TickOptions
    {
        public int MovesPerPlot { get; init; } = 12;
        public int MovesPerTick { get; init; } = 120;
        public int LoopMovesPerTick { get; init; } = 12;
        public bool LoopsAllowed { get; init; } = true;
        public double LeftoverGraceSeconds { get; init; } = 30;
        public int FeedCrafts { get; init; } = StationPlanner.DefaultFeedCrafts;

        /// <summary>
        /// Milliseconds station feeding may take this tick; once spent, the rest of the stations wait for the
        /// next tick (which starts after the last one fed). Infinite by default.
        /// </summary>
        public double StationTimeBudgetMs { get; init; } = double.PositiveInfinity;

        /// <summary>A hash of everything edges and loops depend on (lists, belts, scope); 0 disables the cache.</summary>
        public long LayoutSignature { get; init; }

        /// <summary>Inventories the caller already read this moment, so the tick does not read them again.</summary>
        public IReadOnlyDictionary<(ContainerId, InvPart), ItemStack[]>? Prefetched { get; init; }

        /// <summary>Stations to empty back into storage once; each is removed when nothing is left to move.</summary>
        public ISet<ContainerId>? StationsToClear { get; init; }
    }

    /// <summary>
    /// One conveyor drain over an island (spec 02, 04, 05). Order: stations (fed a craft per
    /// round so sinks share fairly, including from Pull: reverse Mass Deposit), station leftovers,
    /// chest edges and loops, Push: Mass Deposit outputs. Chests never pull from reverse Mass
    /// Deposit: they have no demand. Reserve applies only when items leave storage into a station;
    /// chest-to-chest moves keep items on the island and ignore it.
    /// </summary>
    public sealed class ConveyorTick
    {
        readonly DepositContext _ctx;

        // Per tick: an item's reverse Mass Deposit order and a station's sources for an item. The
        // ranking walks every holder on the island; with a large Default Belt it was asked for per
        // station, item and feeding round. Moves within the tick can make the order slightly stale,
        // which only changes which chest is tried first: Transfer moves what is really there.
        readonly Dictionary<ItemId, (List<ContainerId> Order, Dictionary<ContainerId, int> Rank)> _reverse = new();
        readonly Dictionary<(ContainerId Station, ItemId Item), List<ContainerId>> _sourcesFor = new();
        readonly Dictionary<(ContainerId Station, ItemId Item), List<ContainerId>> _pullFrom = new();
        Dictionary<ItemId, HashSet<ContainerId>>? _holders;

        /// <summary>
        /// Which in-scope containers hold an item, read once per tick. Pulls only need these, so a
        /// station's per-item checks run over a handful of chests, not every chest on its belts.
        /// Items moved into a chest later in the tick are found on the next tick.
        /// </summary>
        HashSet<ContainerId> Holders(ItemId item)
        {
            if (_holders == null)
            {
                _holders = new Dictionary<ItemId, HashSet<ContainerId>>();
                foreach (var id in _inScope)
                {
                    var c = _world.Find(id);
                    if (c == null || c.Kind == NodeKind.Heart) continue;
                    foreach (var part in c.Kind == NodeKind.Station ? new[] { InvPart.Main, InvPart.Output } : new[] { PartOf(id) })
                        foreach (var stack in _world.Contents(id, part))
                        {
                            if (!_holders.TryGetValue(stack.Item, out var set)) _holders[stack.Item] = set = new HashSet<ContainerId>();
                            set.Add(id);
                        }
                }
            }
            return _holders.TryGetValue(item, out var found) ? found : Empty;
        }

        static readonly HashSet<ContainerId> Empty = new();
        bool _ran;

        // Where station feeding spends its time, reported in TickReport.Timings.
        readonly System.Diagnostics.Stopwatch _tPlan = new(), _tPull = new(), _tLeftover = new(), _tSources = new();

        (List<ContainerId> Order, Dictionary<ContainerId, int> Rank) Reverse(ItemId item)
        {
            if (_reverse.TryGetValue(item, out var hit)) return hit;
            var order = DepositPlanner.ReverseOrder(_ctx, item);
            var rank = new Dictionary<ContainerId, int>(order.Count);
            for (int i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
            return _reverse[item] = (order, rank);
        }
        readonly LogisticsGraph _graph;
        readonly CastleRules _rules;
        readonly TickState _state;
        readonly TickOptions _opt;
        readonly TickWorld _world;
        readonly TickReport _report = new();
        readonly Dictionary<PlotId, int> _plotMoves = new();
        readonly Dictionary<ItemId, long> _islandTotals = new();
        bool _totalsBuilt;
        readonly HashSet<ContainerId> _pulledInto = new();
        readonly HashSet<ContainerId> _starvedNow = new();
        readonly HashSet<ContainerId> _inScope;
        int _moves;

        public ConveyorTick(IWorld world, DepositContext ctx, LogisticsGraph graph, CastleRules rules, TickState state, TickOptions? options = null)
        {
            _world = new TickWorld(world, options?.Prefetched);
            _ctx = new DepositContext(ctx.Catalog, _world, ctx.Scope, ctx.Standing)
            {
                Settings = ctx.Settings, Script = ctx.Script, Belts = ctx.Belts,
                PlotLabel = ctx.PlotLabel, Player = ctx.Player,
            };
            _graph = graph;
            _rules = rules;
            _state = state;
            _opt = options ?? new TickOptions();
            // A private chest is out of the island: no list runs to, from or on it.
            _inScope = new HashSet<ContainerId>(ctx.Scope.SelectMany(world.ContainersOn).Where(c => !c.Deposit.Private).Select(c => c.Id));
        }

        ItemCatalog Catalog => _ctx.Catalog;

        public TickReport Run(double now)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // A fresh tick keeps the caller's prefetched reads; a reused one starts clean.
            if (_ran) _world.Clear();
            _ran = true;
            if (!_rules.LogisticsOn)
            {
                foreach (var id in _graph.Lists.Keys) _report.Diag(id, "disabled");
                return _report;
            }

            List<Edge> edges;
            List<Loop> loops;
            var cachedLayout = _opt.LayoutSignature != 0 && _state.CachedLayout == _opt.LayoutSignature && _state.CachedEdges != null;
            if (cachedLayout)
            {
                edges = _state.CachedEdges;
                loops = _state.CachedLoops!;
            }
            else
            {
                edges = new List<Edge>();
                var outOfScope = new List<ContainerId>();
                foreach (var e in _graph.Edges())
                {
                    // Hearts give items up only through an output entry set on the heart itself.
                    if (_world.Find(e.From)?.Kind == NodeKind.Heart && !(e.DefinedAsOutput && e.DefinedOn == e.From)) continue;
                    if (_inScope.Contains(e.From) && _inScope.Contains(e.To)) edges.Add(e);
                    else outOfScope.Add(e.DefinedOn);
                }
                loops = LoopFinder.Find(edges);
                _state.CachedLayout = _opt.LayoutSignature;
                _state.CachedEdges = edges;
                _state.CachedOutOfScope = outOfScope;
                _state.CachedLoops = loops;
            }
            foreach (var id in _state.CachedOutOfScope!) _report.Diag(id, "out-of-scope");

            _report.Timings.Add((cachedLayout ? "edges(cached)+loops" : "edges+loops", watch.Elapsed.TotalMilliseconds));
            _report.Loops.AddRange(loops);
            var skip = new HashSet<Edge>();
            var balance = new List<Loop>();
            foreach (var loop in loops)
            {
                if (!_state.Loops.TryGetValue(loop.Key, out var ls)) _state.Loops[loop.Key] = ls = new LoopState();
                switch (LoopController.Decide(loop, ls, _world, now, _opt.LoopsAllowed, Catalog.MaxStack))
                {
                    case LoopDecision.OpenAtClosingEdge:
                        skip.Add(loop.Closing);
                        break;
                    case LoopDecision.Sleep:
                        foreach (var e in loop.Edges) skip.Add(e);
                        foreach (var m in loop.Members) _report.Diag(m, "loop-waiting");
                        break;
                    case LoopDecision.Balance:
                        foreach (var e in loop.Edges) skip.Add(e);
                        balance.Add(loop);
                        break;
                }
            }

            var live = edges.Where(e => !skip.Contains(e)).ToList();
            ClearStations();
            FeedStations(live, now);
            _report.Timings.Add(("stations", watch.Elapsed.TotalMilliseconds));
            _report.Timings.Add(("  plan", _tPlan.Elapsed.TotalMilliseconds));
            _report.Timings.Add(("  pull", _tPull.Elapsed.TotalMilliseconds));
            _report.Timings.Add(("  leftovers", _tLeftover.Elapsed.TotalMilliseconds));
            _report.Timings.Add(("  all-sources", _tSources.Elapsed.TotalMilliseconds));
            RunChestEdges(live);
            _report.Timings.Add(("chests", watch.Elapsed.TotalMilliseconds));
            foreach (var loop in balance) Balance(loop);
            MassDepositOutputs();
            _report.Timings.Add(("massDeposit", watch.Elapsed.TotalMilliseconds));
            return _report;
        }

        // ---------------- stations ----------------

        void FeedStations(List<Edge> edges, double now)
        {
            var stations = _graph.Lists.Keys
                .Select(id => _world.Find(id))
                .Where(c => c != null && c.Kind == NodeKind.Station && c.Role != StationRole.Coffin && _inScope.Contains(c.Id))
                .Select(c => c!)
                .ToList();

            foreach (var station in stations.Where(s => s.AutomationSuspended))
            {
                _report.Diag(station.Id, station.NativeRoutingKnown ? "native-routing" : "native-routing-unknown");
                foreach (var key in _state.LeftoverSince.Keys.Where(k => k.Station == station.Id).ToList())
                    _state.LeftoverSince.Remove(key);
            }
            stations.RemoveAll(s => s.AutomationSuspended);

            // Start where the last time-limited tick stopped, so every station gets its turn.
            if (stations.Count > 1)
            {
                var k = _state.StationCursor % stations.Count;
                stations = stations.Skip(k).Concat(stations.Take(k)).ToList();
            }
            var all = stations.Count;
            // Starved stations wait out their backoff unless something they depend on changed.
            stations.RemoveAll(s => Waiting(s, now));
            foreach (var gone in _state.StarvedStations.Keys.Where(id => _world.Find(id) == null).ToList()) _state.StarvedStations.Remove(gone);

            // Topology is unchanged during this synchronous pass. Build source lists once.
            var incoming = edges.ToLookup(e => e.To);
            var sourceLists = stations.ToDictionary(s => s.Id, s => Sources(s, incoming[s.Id].ToList()));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var reached = stations.Count; // stations that got every round this tick
            // Fair share in whole crafts: round k feeds every station up to k crafts.
            for (var round = 1; round <= _opt.FeedCrafts && Budget(); round++)
            {
                for (var i = 0; i < stations.Count; i++)
                {
                    if (i >= reached) break;
                    if (clock.Elapsed.TotalMilliseconds > _opt.StationTimeBudgetMs) { reached = i; _report.Diag(stations[i].Id, "time-budget"); break; }
                    var st = stations[i];
                    var depth = st.Role == StationRole.UnitSpawner ? Math.Min(_opt.FeedCrafts, SpecialJobs.SpawnerFeedCrafts) : _opt.FeedCrafts;
                    if (round > depth || (st.Role == StationRole.Workstation && round != depth)) continue;
                    FeedStation(st, sourceLists[st.Id], round, now, final: round == depth);
                }
            }
            _state.StationCursor = reached >= stations.Count ? 0 : _state.StationCursor + reached;
            foreach (var st in stations.Take(reached)) Backoff(st, now);
        }

        /// <summary>Still inside its backoff, with nothing it depends on changed.</summary>
        bool Waiting(ContainerInfo station, double now)
        {
            if (!_state.StarvedStations.TryGetValue(station.Id, out var s)) return false;
            if (now >= s.NextTry || _state.LeftoverSince.Keys.Any(k => k.Station == station.Id)
                || s.Supply != Supply(station) || s.Held != Held(station))
                return false;
            _report.Diag(station.Id, "waiting-supply");
            return true;
        }

        /// <summary>After a station's full feed: starved with nothing pulled backs off (doubling to 60 s); fed clears it.</summary>
        void Backoff(ContainerInfo station, double now)
        {
            if (!_starvedNow.Contains(station.Id) || _pulledInto.Contains(station.Id))
            {
                _state.StarvedStations.Remove(station.Id);
                return;
            }
            var delay = _state.StarvedStations.TryGetValue(station.Id, out var was) ? Math.Min(60, was.Delay * 2) : 5;
            _state.StarvedStations[station.Id] = new TickState.Starved
            {
                Supply = Supply(station), Held = Held(station), NextTry = now + delay, Delay = delay,
            };
        }

        /// <summary>
        /// What a starved station depends on: its recipes and their switches, the island's stock, reserve and cap of
        /// every input and product. A change to any of them ends the backoff.
        /// </summary>
        long Supply(ContainerInfo station)
        {
            unchecked
            {
                long h = 17;
                foreach (var r in _world.Recipes(station.Id))
                {
                    h = h * 31 + r.Id * 2 + (r.Enabled ? 1 : 0);
                    if (!r.Enabled) continue;
                    foreach (var item in r.Inputs.Concat(r.Outputs).Select(i => i.Item))
                        h = h * 31 + item.Guid * 7919L + IslandTotal(item) * 3 + _rules.Reserve(item, Catalog) * 5 + (_rules.Cap(item, Catalog) ?? -1) * 7;
                }
                return h;
            }
        }

        long Held(ContainerInfo station)
        {
            unchecked
            {
                long h = 23;
                foreach (var s in _world.Contents(station.Id)) h = h * 31 + s.Item.Guid * 7919L + s.Amount;
                return h;
            }
        }

        /// <summary>
        /// A devourer takes everything from the chests a player linked to it (its own Input entries, or another chest's
        /// Output naming it), never through the automatic Default Belt or a reverse Mass Deposit: those would salvage the
        /// whole island. What the game will not salvage stays where it is.
        /// </summary>
        void FeedDevourer(ContainerInfo station, List<ContainerId> sources)
        {
            var lists = _graph.For(station.Id);
            var chosen = new HashSet<ContainerId>();
            foreach (var e in lists.Input.Where(e => !e.Auto && e.Kind != EntryKind.MassDeposit))
                foreach (var id in _graph.Expand(e, output: false, owner: station.Id)) chosen.Add(id);
            foreach (var (owner, other) in _graph.Lists)
                if (owner != station.Id && other.Output.Any(e => !e.Auto && e.Kind == EntryKind.Container && e.Container == station.Id))
                    chosen.Add(owner);
            // What came out unchanged (the game would not salvage it) goes back where it came from, and is not fed
            // again. If that chest will not take it, the devourer's own Output (Mass Deposit) has it.
            foreach (var stack in _world.Contents(station.Id, InvPart.Output).ToList())
            {
                if (!_state.FedFrom.TryGetValue((station.Id, stack.Item), out var home)) continue;
                _state.DevourerRefuses.Add((station.Id, stack.Item));
                if (Budget(station.Id)) Move(station.Id, InvPart.Output, home, stack.Item, stack.Amount, "devourer returns");
            }
            foreach (var src in sources.Where(chosen.Contains))
                foreach (var stack in _world.Contents(src, PartOf(src)).ToList())
                {
                    if (_state.DevourerRefuses.Contains((station.Id, stack.Item))) continue;
                    if (!Budget(src)) return;
                    if (Move(src, PartOf(src), station.Id, stack.Item, stack.Amount, "salvage feed") > 0)
                    {
                        _pulledInto.Add(station.Id);
                        _state.FedFrom[(station.Id, stack.Item)] = src;
                    }
                }
        }

        /// <summary>Source chests for a station's inputs, in pull order.</summary>
        List<ContainerId> Sources(ContainerInfo station, List<Edge> edges)
        {
            var lists = _graph.For(station.Id);
            var result = new List<ContainerId>();
            foreach (var e in lists.Input)
            {
                if (e.Kind == EntryKind.MassDeposit) continue; // resolved per item below
                foreach (var id in _graph.Expand(e, output: false, owner: station.Id))
                    if (edges.Any(x => x.From == id && x.To == station.Id) && !result.Contains(id)) result.Add(id);
            }
            foreach (var e in edges.Where(x => x.To == station.Id && x.DefinedAsOutput))
                if (!result.Contains(e.From)) result.Add(e.From);
            return result;
        }

        void FeedStation(ContainerInfo station, List<ContainerId> sources, int crafts, double now, bool final)
        {
            if (station.Role == StationRole.Brazier)
            {
                if (final) FeedBrazier(station, sources);
                return;
            }
            // A devourer has no recipe list: it salvages whatever it is given.
            if (station.Role == StationRole.Devourer)
            {
                if (final) FeedDevourer(station, sources);
                return;
            }
            var lists = _graph.For(station.Id);
            var original = _world.Recipes(station.Id);
            var capped = original.Where(r => r.Enabled && !CapAllows(r)).Select(r => r.Id).ToHashSet();
            var recipes = original
                .Select(r => r.Enabled && !capped.Contains(r.Id) ? r : r with { Enabled = false })
                .ToList();

            var held = _world.Contents(station.Id);
            // Every allowed source (homes for leftovers) and the ones that hold the item (pulls).
            List<ContainerId> SourcesFor(ItemId item)
            {
                if (_sourcesFor.TryGetValue((station.Id, item), out var cached)) return cached;
                var was = _tSources.IsRunning; _tSources.Start();
                try { return _sourcesFor[(station.Id, item)] = ComputeSourcesFor(item, null); }
                finally { if (!was) _tSources.Stop(); }
            }

            List<ContainerId> PullFrom(ItemId item)
            {
                if (_pullFrom.TryGetValue((station.Id, item), out var cached)) return cached;
                if (_sourcesFor.TryGetValue((station.Id, item), out var all))
                {
                    var holders = Holders(item);
                    return _pullFrom[(station.Id, item)] = all.Where(holders.Contains).ToList();
                }
                return _pullFrom[(station.Id, item)] = ComputeSourcesFor(item, Holders(item));
            }

            List<ContainerId> ComputeSourcesFor(ItemId item, HashSet<ContainerId>? only)
            {
                var list = new List<ContainerId>(sources);
                var seen = new HashSet<ContainerId>(list);
                var massSources = lists.Input.Any(e => e.Kind == EntryKind.MassDeposit && e.Filter.Allows(item, Catalog))
                    ? Reverse(item).Order.ToHashSet() : new HashSet<ContainerId>();
                if (massSources.Count > 0)
                    foreach (var id in Reverse(item).Order)
                        if (seen.Add(id)) list.Add(id);
                if (lists.PullOrder == PullOrder.ReverseMassDeposit)
                {
                    var rank = Reverse(item).Rank;
                    list = list.OrderBy(id => rank.TryGetValue(id, out var i) ? i : int.MaxValue).ToList();
                }
                return list.Where(id => (only == null || only.Contains(id)) && _inScope.Contains(id) && id != station.Id)
                    .Where(id => _world.Find(id) is { } c && c.Kind is NodeKind.Chest or NodeKind.Station
                        && !c.AutomationSuspended && c.Deposit.Special != SpecialKind.Skip && !_world.IsRaided(c.Plot))
                    .Where(id => massSources.Contains(id)
                        || lists.Input.Any(e => _graph.Allows(e, station.Id, false, id, item, Catalog))
                        || _graph.For(id).Output.Any(e => _graph.Allows(e, id, true, station.Id, item, Catalog)))
                    .ToList();
            }

            // Bench crafting is paid through MaterialSource from the player's native queue.
            // Recipe switches never create conveyor demand, even when all recipes are enabled.
            if (station.Role == StationRole.Workstation)
            {
                // No recipe rows means products cannot be classified safely. Keep the inventory intact.
                if (original.Count == 0) return;
                if (final) ReturnLeftovers(station, held.Where(s => !IsBenchProduct(station.Id, s.Item)).GroupBy(s => s.Item)
                    .ToDictionary(g => g.Key, g => g.Sum(s => s.Amount)),
                    item => SourcesFor(item).ToHashSet(), now);
                return;
            }

            // Nothing moves while the planner asks, so each item's availability is summed once per plan.
            var available = new Dictionary<ItemId, int>();
            int Available(ItemId item)
            {
                // A station takes only what its Accepts allow (a furnace listed for iron takes no copper). An empty
                // Accepts list (as imported 1.x stations have) narrows nothing: its recipes decide.
                if (Narrows(station.Deposit) && !DepositScore.Takes(station, item, Catalog)) return 0;
                if (available.TryGetValue(item, out var known)) return known;
                var inSources = PullFrom(item).Sum(id => SourceCount(id, item));
                var aboveReserve = IslandTotal(item) - _rules.Reserve(item, Catalog);
                return available[item] = (int)Math.Max(0, Math.Min(inSources, aboveReserve));
            }

            _tPlan.Start();
            var plan = StationPlanner.Plan(recipes, held, Available, crafts);
            _tPlan.Stop();
            _tPull.Start();

            foreach (var (item, amount) in plan.Pull)
            {
                var left = amount;
                foreach (var src in PullFrom(item))
                {
                    if (left <= 0 || !Budget(src)) break;
                    var moved = Move(src, PartOf(src), station.Id, item, left, "station feed", ProductFor(recipes, plan, item));
                    if (moved > 0)
                    {
                        _pulledInto.Add(station.Id);
                        _state.FedFrom[(station.Id, item)] = src;
                        _islandTotals[item] = IslandTotal(item) - moved;
                    }
                    left -= moved;
                }
            }

            _tPull.Stop();
            if (!final) return;

            if (capped.Count > 0) _report.Diag(station.Id, "cap");
            foreach (var r in recipes.Where(r => r.Enabled && plan.Crafts.TryGetValue(r.Id, out var c) && c == 0))
            {
                _starvedNow.Add(station.Id);
                if (!plan.Missing.TryGetValue(r.Id, out var short1)) { _report.Diag(station.Id, "no-source"); continue; }
                // Stock is there but the reserve holds it back (spec 02 "reserve").
                var inSources = PullFrom(short1).Sum(id => SourceCount(id, short1));
                _report.Diag(station.Id, inSources > Available(short1) ? "reserve" : "not-enough");
            }

            _tLeftover.Start();
            ReturnLeftovers(station, plan.Leftovers, item => SourcesFor(item).ToHashSet(), now);
            _tLeftover.Stop();
        }

        /// <summary>
        /// The one-time cleanup: everything in each listed station's input goes back to storage by
        /// Mass Deposit order, even mid-craft (the list holds only factories). A station whose stock
        /// finds no room stays listed.
        /// </summary>
        void ClearStations()
        {
            var set = _opt.StationsToClear;
            if (set == null || set.Count == 0) return;
            foreach (var id in set.ToList())
            {
                var station = _world.Find(id);
                // Not in this world: another island under the same settings owns it.
                if (station == null) continue;
                // Never a bench: its slots hold the player's queued crafts.
                if (station.Kind != NodeKind.Station
                    || station.Role is not (StationRole.None or StationRole.Refinement or StationRole.Coffin or StationRole.UnitSpawner))
                { set.Remove(id); continue; }
                if (!_inScope.Contains(id) || station.AutomationSuspended) continue;
                // A unit station keeps its spawn ingredients; everything else goes.
                var keep = station.Role == StationRole.UnitSpawner
                    ? _world.Recipes(id).SelectMany(r => r.Inputs).Select(i => i.Item).ToHashSet()
                    : new HashSet<ItemId>();
                var done = true;
                foreach (var stack in _world.Contents(id, InvPart.Main).ToList())
                {
                    if (keep.Contains(stack.Item)) continue;
                    var left = stack.Amount;
                    var plan = DepositPlanner.Plan(_ctx, stack.Item, left, id);
                    foreach (var m in plan)
                        left -= Move(id, InvPart.Main, m.To, stack.Item, m.Amount, "station cleanup");
                    if (left > 0)
                    {
                        done = false;
                        _report.Diag(id, plan.Count == 0 ? $"cleanup-no-home:{stack.Item.Guid}" : $"cleanup-refused:{stack.Item.Guid}");
                    }
                }
                if (done) set.Remove(id);
                else _report.Diag(id, "sink-full");
            }
        }

        static bool Narrows(DepositOptions d) =>
            d.ItemPriority.Count > 0 || d.CategoryPriority.Count > 0 || d.BannedItems.Count > 0 || d.BannedCategories.Count > 0;

        void ReturnLeftovers(ContainerInfo station, IReadOnlyDictionary<ItemId, int> leftovers,
            Func<ItemId, HashSet<ContainerId>> sourcesFor, double now)
        {
            if (ProtectsInputs(station) && !_world.StationInputs(station.Id).CanReclaim)
            {
                // A new queue or an unknown read interrupts the grace period; never age reservations into leftovers.
                foreach (var key in _state.LeftoverSince.Keys.Where(k => k.Station == station.Id).ToList())
                    _state.LeftoverSince.Remove(key);
                return;
            }
            foreach (var key in _state.LeftoverSince.Keys.Where(k => k.Station == station.Id && !leftovers.ContainsKey(k.Item)).ToList())
                _state.LeftoverSince.Remove(key);
            foreach (var (item, amount) in leftovers)
            {
                var key = (station.Id, item);
                if (!_state.LeftoverSince.TryGetValue(key, out var since)) { _state.LeftoverSince[key] = now; continue; }
                if (now - since < _opt.LeftoverGraceSeconds) continue;

                var left = amount;
                var inputHomes = sourcesFor(item);
                foreach (var home in DepositRanker.Rank(_ctx, item, inputHomes).Tiers.SelectMany(t => t.Members))
                {
                    if (left <= 0 || !Budget(station.Id)) break;
                    left -= Move(station.Id, InvPart.Main, home.Chest.Id, item, left, "leftover return");
                }
                foreach (var m in DepositPlanner.Plan(_ctx, item, left, station.Id))
                    left -= Move(station.Id, InvPart.Main, m.To, item, m.Amount, "leftover home");
                if (left > 0) _report.Diag(station.Id, "sink-full");
                else _state.LeftoverSince.Remove(key);
            }
        }

        bool CapAllows(Recipe r)
        {
            foreach (var o in r.Outputs)
                if (Catalog.ItemCapOrNull(_rules, o.Item) is { } cap && IslandTotal(o.Item) >= cap) return false;
            return true;
        }

        // ---------------- chest edges ----------------

        void RunChestEdges(List<Edge> edges)
        {
            foreach (var group in edges.GroupBy(e => e.From))
            {
                var from = _world.Find(group.Key);
                if (from == null || from.AutomationSuspended || from.Deposit.Special == SpecialKind.Skip) continue;
                var targets = group.Where(e => _world.Find(e.To)?.Kind == NodeKind.Chest).ToList();
                if (targets.Count == 0) continue;
                var spread = _graph.For(from.Id).SendOrder == SendOrder.SpreadEvenly;

                foreach (var stack in _world.Contents(from.Id, PartOf(from.Id)))
                {
                    if (IsStableFeed(from.Id, stack.Item)) continue;
                    var wanting = targets.Where(e => e.Filter.Allows(stack.Item, Catalog)).ToList();
                    if (wanting.Count == 0) continue;
                    var left = stack.Amount;
                    if (spread)
                    {
                        var share = Math.Max(1, left / wanting.Count);
                        foreach (var e in wanting)
                        {
                            if (left <= 0 || !Budget(from.Id)) break;
                            left -= Move(from.Id, PartOf(from.Id), e.To, stack.Item, Math.Min(share, left), "edge");
                        }
                    }
                    foreach (var e in wanting)
                    {
                        if (left <= 0 || !Budget(from.Id)) break;
                        var moved = Move(from.Id, PartOf(from.Id), e.To, stack.Item, left, "edge");
                        if (moved == 0 && _world.FreeSpace(e.To, stack.Item) == 0) _report.Diag(e.To, "sink-full");
                        left -= moved;
                    }
                }
            }
        }

        void Balance(Loop loop)
        {
            var budget = _opt.LoopMovesPerTick;
            var outgoing = loop.Edges.ToLookup(e => e.From);
            // Follow connection order rather than random container ids. This lets the same
            // ring settle consistently regardless of which ids a save assigned its chests.
            // Only chests share a loop's stock; a station in the ring keeps what its recipes pulled.
            bool IsChest(ContainerId id) => _world.Find(id)?.Kind == NodeKind.Chest;
            var members = loop.Edges.OrderBy(e => e.Sequence).Select(e => e.From).Distinct().Where(IsChest).ToArray();
            var chests = loop.Members.Where(IsChest).ToList();
            if (chests.Count < 2) return;
            var items = members.SelectMany(m => _world.Contents(m)).Select(s => s.Item).Distinct().OrderBy(i => i.Guid).ToArray();
            foreach (var item in items)
            {
                var counts = chests.ToDictionary(m => m, m => _world.Count(m, item));
                if (counts.Values.Max() - counts.Values.Min() <= Catalog.MaxStack(item)) continue;
                var total = counts.Values.Sum(n => (long)n);
                var targets = chests.Select((m, i) => (m, n: (int)(total / counts.Count + (i < total % counts.Count ? 1 : 0))))
                    .ToDictionary(x => x.m, x => x.n);
                // Incoming items are not forwarded again in this pass. Existing stock may travel
                // one edge toward a downstream deficit, even when the next chest has its share.
                var available = new Dictionary<ContainerId, int>(counts);
                foreach (var from in members)
                {
                    if (budget <= 0 || !Budget()) return;
                    if (!Budget(from) || available[from] <= 0 || counts[from] <= targets[from]) continue;
                    var queue = new Queue<ContainerId>();
                    var firstHop = new Dictionary<ContainerId, ContainerId> { [from] = from };
                    queue.Enqueue(from);
                    while (queue.Count > 0)
                    {
                        var current = queue.Dequeue();
                        foreach (var edge in outgoing[current])
                        {
                            var to = edge.To;
                            if (firstHop.ContainsKey(to) || !edge.Filter.Allows(item, Catalog)) continue;
                            var target = _world.Find(to);
                            // Stations are fed by their recipes, never handed a loop's share.
                            if (target == null || target.AutomationSuspended || target.Deposit.Special == SpecialKind.Skip
                                || target.Kind != NodeKind.Chest || !DepositScore.Takes(target, item, Catalog)
                                || _world.FreeSpace(to, item) <= 0) continue;
                            firstHop[to] = current == from ? to : firstHop[current];
                            if (counts[to] < targets[to])
                            {
                                var hop = firstHop[to];
                                var amount = Math.Min(available[from], Math.Min(counts[from] - targets[from], targets[to] - counts[to]));
                                var moved = Move(from, InvPart.Main, hop, item, amount, "loop balance");
                                if (moved > 0)
                                {
                                    counts[from] -= moved; counts[hop] += moved; available[from] -= moved; budget--;
                                }
                                queue.Clear();
                                break;
                            }
                            queue.Enqueue(to);
                        }
                    }
                }
            }
        }

        // ---------------- Mass Deposit entries ----------------

        void MassDepositOutputs()
        {
            foreach (var (id, lists) in _graph.Lists)
            {
                var entry = lists.Output.FirstOrDefault(e => e.Kind == EntryKind.MassDeposit);
                if (entry == null || !_inScope.Contains(id) || _world.Find(id)?.AutomationSuspended == true) continue;
                foreach (var stack in _world.Contents(id, PartOf(id)))
                {
                    if (!entry.Filter.Allows(stack.Item, Catalog) || IsStableFeed(id, stack.Item)) continue;
                    var left = stack.Amount;
                    foreach (var m in DepositPlanner.Plan(_ctx, stack.Item, left, id))
                    {
                        if (left <= 0 || !Budget(id)) break;
                        left -= Move(id, PartOf(id), m.To, stack.Item, m.Amount, "mass deposit");
                    }
                }
            }
        }

        // ---------------- helpers ----------------

        /// <summary>
        /// A brazier has no recipes: it takes whatever fuel its Input sources hold, as much as it
        /// has room for, in list order. Island reserves still apply. The game decides what is fuel
        /// (a brazier has no room for anything else).
        /// </summary>
        void FeedBrazier(ContainerInfo brazier, List<ContainerId> sources)
        {
            var filters = _graph.For(brazier.Id).Input;
            foreach (var src in sources)
            {
                if (!Budget(src)) break;
                foreach (var stack in _world.Contents(src).ToList())
                {
                    if (!filters.Any(e => _graph.Allows(e, brazier.Id, false, src, stack.Item, Catalog))
                        && !_graph.For(src).Output.Any(e => _graph.Allows(e, src, true, brazier.Id, stack.Item, Catalog)))
                        continue;
                    var room = _world.FreeSpace(brazier.Id, stack.Item);
                    var aboveReserve = IslandTotal(stack.Item) - _rules.Reserve(stack.Item, Catalog);
                    var want = (int)Math.Min(Math.Min(room, SourceCount(src, stack.Item)), Math.Max(0, aboveReserve));
                    if (want <= 0) continue;
                    var moved = Move(src, PartOf(src), brazier.Id, stack.Item, want, "brazier fuel");
                    if (moved > 0) _islandTotals[stack.Item] = IslandTotal(stack.Item) - moved;
                }
            }
        }

        /// <summary>
        /// A stable's Output gives up only what the mount generates, never feed (spec 01). The
        /// adapter reports a stable's feed items as its recipe inputs.
        /// </summary>
        bool IsStableFeed(ContainerId id, ItemId item) =>
            _world.Find(id)?.Role == StationRole.Stable && _world.Recipes(id).Any(r => r.Inputs.Any(i => i.Item == item));

        InvPart PartOf(ContainerId id) => _world.Find(id) is { Kind: NodeKind.Station } station
            && station.Role != StationRole.Workstation ? InvPart.Output : InvPart.Main;

        bool IsBenchProduct(ContainerId id, ItemId item) => _world.Recipes(id)
            .Any(r => r.Outputs.Any(o => o.Item == item && o.Amount > 0));

        int SourceCount(ContainerId id, ItemId item) => _world.Find(id) is { } source
            && (source.AutomationSuspended || (source.Role == StationRole.Workstation
                && (!_world.StationInputs(id).CanReclaim || !IsBenchProduct(id, item))))
            ? 0 : _world.Count(id, item, PartOf(id));

        static bool ProtectsInputs(ContainerInfo container) => container.Kind == NodeKind.Station
            && container.Role is StationRole.Workstation or StationRole.Refinement;

        long IslandTotal(ItemId item)
        {
            if (!_totalsBuilt)
            {
                // One pass over the island's shared chests for every item, not one pass per item asked.
                _totalsBuilt = true;
                foreach (var c in Scope.Chests(_world, _ctx.Scope))
                    foreach (var s in _world.Contents(c.Id))
                        _islandTotals[s.Item] = (_islandTotals.TryGetValue(s.Item, out var n) ? n : 0) + s.Amount;
            }
            return _islandTotals.TryGetValue(item, out var t) ? t : 0;
        }

        bool Budget() => _moves < _opt.MovesPerTick;

        bool Budget(ContainerId source)
        {
            if (_moves >= _opt.MovesPerTick) return false;
            var plot = _world.Find(source)?.Plot ?? default;
            return !_plotMoves.TryGetValue(plot, out var n) || n < _opt.MovesPerPlot;
        }

        /// <summary>What a pulled item is for: the first product of a fed recipe that uses it.</summary>
        static ItemId ProductFor(IEnumerable<Recipe> recipes, StationPlan plan, ItemId item)
        {
            var r = recipes.Where(x => x.Inputs.Any(i => i.Item == item))
                .OrderByDescending(x => plan.Crafts.TryGetValue(x.Id, out var c) ? c : 0)
                .FirstOrDefault();
            return r != null && r.Outputs.Count > 0 ? r.Outputs[0].Item : default;
        }

        int Move(ContainerId from, InvPart part, ContainerId to, ItemId item, int amount, string why, ItemId forProduct = default)
        {
            if (amount <= 0 || from == to) return 0;
            if (!_inScope.Contains(from) || !_inScope.Contains(to)) return 0;
            var source = _world.Find(from);
            var target = _world.Find(to);
            if (source == null || target == null || _world.IsRaided(source.Plot) || _world.IsRaided(target.Plot)
                || source.Deposit.Special == SpecialKind.Skip || target.Deposit.Special == SpecialKind.Skip) return 0;
            if (source.AutomationSuspended || target.AutomationSuspended) return 0;
            // Applies to edges, loops, and mass-deposit outputs too. Workstation inventories are
            // shared input/output: products follow output routes; ingredients use the grace-controlled return path.
            if (target.Role == StationRole.Workstation) return 0;
            // A coffin's slots belong to its servant.
            if (target.Role == StationRole.Coffin || (source.Role == StationRole.Coffin && why != "station cleanup")) return 0;
            if (source.Role == StationRole.Workstation)
            {
                if (!_world.StationInputs(from).CanReclaim || _world.Recipes(from).Count == 0) return 0;
                bool returning = why == "leftover return" || why == "leftover home";
                if (returning == IsBenchProduct(from, item)) return 0;
            }
            if (part == InvPart.Main && ProtectsInputs(source) && !_world.StationInputs(from).CanReclaim
                && !(why == "station cleanup" && source.Role != StationRole.Workstation)) return 0;
            if (target.Kind == NodeKind.Chest && !DepositScore.Takes(target, item, Catalog)) return 0;
            var moved = _world.Transfer(from, part, to, item, amount, takeBusyInputs: why == "station cleanup");
            if (moved <= 0) return 0;
            _moves++;
            var plot = _world.Find(from)?.Plot ?? default;
            _plotMoves[plot] = (_plotMoves.TryGetValue(plot, out var n) ? n : 0) + 1;
            _report.Moves.Add(new MoveRecord(from, to, item, moved, why, forProduct));
            return moved;
        }
        /// <summary>
        /// Sources for a heart's blood feed, in pull order: its input list when it has entries,
        /// else the island in reverse Mass Deposit order. Other hearts are never sources.
        /// </summary>
        public static List<ContainerId> HeartSources(IWorldView world, DepositContext ctx, LogisticsGraph graph, ContainerId heart, ItemId blood)
        {
            var lists = graph.Lists.TryGetValue(heart, out var l) ? l : null;
            if (lists == null || lists.Input.Count == 0) return DepositPlanner.ReverseOrder(ctx, blood);

            var result = new List<ContainerId>();
            foreach (var e in lists.Input)
            {
                var ids = e.Kind == EntryKind.MassDeposit
                    ? (e.Filter.Allows(blood, ctx.Catalog) ? DepositPlanner.ReverseOrder(ctx, blood) : new List<ContainerId>())
                    : graph.Expand(e, output: false, owner: heart)
                        .Where(id => graph.Allows(e, heart, false, id, blood, ctx.Catalog));
                foreach (var id in ids)
                    if (world.Find(id) is { Kind: not NodeKind.Heart, AutomationSuspended: false } && !result.Contains(id)) result.Add(id);
            }
            if (lists.PullOrder == PullOrder.ReverseMassDeposit)
            {
                var reverse = DepositPlanner.ReverseOrder(ctx, blood);
                result = result.OrderBy(id => { var i = reverse.IndexOf(id); return i < 0 ? int.MaxValue : i; }).ToList();
            }
            return result;
        }
    }

    static class CatalogRuleExtensions
    {
        public static int? ItemCapOrNull(this ItemCatalog catalog, CastleRules rules, ItemId item) => rules.Cap(item, catalog);
    }
}
