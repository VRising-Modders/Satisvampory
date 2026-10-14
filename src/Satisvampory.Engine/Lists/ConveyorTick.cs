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
    public sealed record MoveRecord(ContainerId From, ContainerId To, ItemId Item, int Amount, string Why);

    /// <summary>State that lives across ticks for one island.</summary>
    public sealed class TickState
    {
        /// <summary>Loop run state by <see cref="Loop.Key"/>.</summary>
        public Dictionary<string, LoopState> Loops { get; } = new();

        /// <summary>When a station's held item was first seen as a leftover, for the grace period.</summary>
        public Dictionary<(ContainerId Station, ItemId Item), double> LeftoverSince { get; } = new();

        /// <summary>Where a station's inputs last came from, so leftovers can go back.</summary>
        public Dictionary<(ContainerId Station, ItemId Item), ContainerId> FedFrom { get; } = new();
    }

    public sealed class TickReport
    {
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
        readonly LogisticsGraph _graph;
        readonly CastleRules _rules;
        readonly TickState _state;
        readonly TickOptions _opt;
        readonly IWorld _world;
        readonly TickReport _report = new();
        readonly Dictionary<PlotId, int> _plotMoves = new();
        readonly Dictionary<ItemId, long> _islandTotals = new();
        readonly HashSet<ContainerId> _inScope;
        int _moves;

        public ConveyorTick(IWorld world, DepositContext ctx, LogisticsGraph graph, CastleRules rules, TickState state, TickOptions? options = null)
        {
            _world = world;
            _ctx = ctx;
            _graph = graph;
            _rules = rules;
            _state = state;
            _opt = options ?? new TickOptions();
            _inScope = new HashSet<ContainerId>(ctx.Scope.SelectMany(world.ContainersOn).Select(c => c.Id));
        }

        ItemCatalog Catalog => _ctx.Catalog;

        public TickReport Run(double now)
        {
            if (!_rules.LogisticsOn)
            {
                foreach (var id in _graph.Lists.Keys) _report.Diag(id, "disabled");
                return _report;
            }

            var edges = new List<Edge>();
            foreach (var e in _graph.Edges())
            {
                // Hearts give items up only through an output entry set on the heart itself.
                if (_world.Find(e.From)?.Kind == NodeKind.Heart && !(e.DefinedAsOutput && e.DefinedOn == e.From)) continue;
                if (_inScope.Contains(e.From) && _inScope.Contains(e.To)) edges.Add(e);
                else _report.Diag(e.DefinedOn, "out-of-scope");
            }

            var loops = LoopFinder.Find(edges);
            _report.Loops.AddRange(loops);
            var skip = new HashSet<Edge>();
            var balance = new List<Loop>();
            foreach (var loop in loops)
            {
                if (!_state.Loops.TryGetValue(loop.Key, out var ls)) _state.Loops[loop.Key] = ls = new LoopState();
                switch (LoopController.Decide(loop, ls, _world, now, _opt.LoopsAllowed))
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
            FeedStations(live, now);
            RunChestEdges(live);
            foreach (var loop in balance) Balance(loop);
            MassDepositOutputs();
            return _report;
        }

        // ---------------- stations ----------------

        void FeedStations(List<Edge> edges, double now)
        {
            var stations = _graph.Lists.Keys
                .Select(id => _world.Find(id))
                .Where(c => c != null && c.Kind == NodeKind.Station && _inScope.Contains(c.Id))
                .Select(c => c!)
                .ToList();

            // Fair share in whole crafts: round k feeds every station up to k crafts.
            for (var round = 1; round <= _opt.FeedCrafts && Budget(); round++)
                foreach (var st in stations)
                    FeedStation(st, Sources(st, edges), round, now, final: round == _opt.FeedCrafts);
        }

        /// <summary>Source chests for a station's inputs, in pull order.</summary>
        List<ContainerId> Sources(ContainerInfo station, List<Edge> edges)
        {
            var lists = _graph.For(station.Id);
            var result = new List<ContainerId>();
            foreach (var e in lists.Input)
            {
                if (e.Kind == EntryKind.MassDeposit) continue; // resolved per item below
                foreach (var id in _graph.Expand(e))
                    if (edges.Any(x => x.From == id && x.To == station.Id) && !result.Contains(id)) result.Add(id);
            }
            foreach (var e in edges.Where(x => x.To == station.Id && x.DefinedAsOutput))
                if (!result.Contains(e.From)) result.Add(e.From);
            return result;
        }

        void FeedStation(ContainerInfo station, List<ContainerId> sources, int crafts, double now, bool final)
        {
            var lists = _graph.For(station.Id);
            var massInput = lists.Input.Any(e => e.Kind == EntryKind.MassDeposit);
            var recipes = _world.Recipes(station.Id)
                .Select(r => r.Enabled && CapAllows(r) ? r : r with { Enabled = false })
                .ToList();

            var held = _world.Contents(station.Id);
            List<ContainerId> SourcesFor(ItemId item)
            {
                var list = new List<ContainerId>(sources);
                if (massInput)
                    foreach (var id in DepositPlanner.ReverseOrder(_ctx, item))
                        if (!list.Contains(id)) list.Add(id);
                if (lists.PullOrder == PullOrder.ReverseMassDeposit)
                {
                    var reverse = DepositPlanner.ReverseOrder(_ctx, item);
                    list = list.OrderBy(id => { var i = reverse.IndexOf(id); return i < 0 ? int.MaxValue : i; }).ToList();
                }
                return list.Where(id => _world.Find(id)?.Kind == NodeKind.Chest || _world.Find(id)?.Kind == NodeKind.Station).ToList();
            }

            int Available(ItemId item)
            {
                var inSources = SourcesFor(item).Sum(id => SourceCount(id, item));
                var aboveReserve = IslandTotal(item) - _rules.Reserve(item, Catalog);
                return (int)Math.Max(0, Math.Min(inSources, aboveReserve));
            }

            var plan = StationPlanner.Plan(recipes, held, Available, crafts);

            foreach (var (item, amount) in plan.Pull)
            {
                var left = amount;
                foreach (var src in SourcesFor(item))
                {
                    if (left <= 0 || !Budget(src)) break;
                    var moved = Move(src, PartOf(src), station.Id, item, left, "station feed");
                    if (moved > 0)
                    {
                        _state.FedFrom[(station.Id, item)] = src;
                        _islandTotals[item] = IslandTotal(item) - moved;
                    }
                    left -= moved;
                }
            }

            if (!final) return;

            foreach (var r in recipes.Where(r => r.Enabled && plan.Crafts.TryGetValue(r.Id, out var c) && c == 0))
                _report.Diag(station.Id, plan.Missing.ContainsKey(r.Id) ? "not-enough" : "no-source");

            // Leftovers: returned after the grace period, to where they came from, else their home.
            foreach (var key in _state.LeftoverSince.Keys.Where(k => k.Station == station.Id && !plan.Leftovers.ContainsKey(k.Item)).ToList())
                _state.LeftoverSince.Remove(key);
            foreach (var (item, amount) in plan.Leftovers)
            {
                var key = (station.Id, item);
                if (!_state.LeftoverSince.TryGetValue(key, out var since)) { _state.LeftoverSince[key] = now; continue; }
                if (now - since < _opt.LeftoverGraceSeconds) continue;

                var left = amount;
                if (_state.FedFrom.TryGetValue(key, out var origin) && _inScope.Contains(origin))
                    left -= Move(station.Id, InvPart.Main, origin, item, left, "leftover return");
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
                if (from == null || from.Deposit.Special == SpecialKind.Skip) continue;
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
            foreach (var e in loop.Edges)
                foreach (var stack in _world.Contents(e.From))
                {
                    if (budget <= 0 || !Budget(e.From)) return;
                    if (!e.Filter.Allows(stack.Item, Catalog)) continue;
                    var n = LoopController.BalanceAmount(stack.Amount, _world.Count(e.To, stack.Item), Catalog.MaxStack(stack.Item));
                    if (n <= 0) continue;
                    if (Move(e.From, InvPart.Main, e.To, stack.Item, n, "loop balance") > 0) budget--;
                }
        }

        // ---------------- Mass Deposit entries ----------------

        void MassDepositOutputs()
        {
            foreach (var (id, lists) in _graph.Lists)
            {
                var entry = lists.Output.FirstOrDefault(e => e.Kind == EntryKind.MassDeposit);
                if (entry == null || !_inScope.Contains(id)) continue;
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
        /// A stable's Output gives up only what the mount generates, never feed (spec 01). The
        /// adapter reports a stable's feed items as its recipe inputs.
        /// </summary>
        bool IsStableFeed(ContainerId id, ItemId item) =>
            _world.Find(id)?.Role == StationRole.Stable && _world.Recipes(id).Any(r => r.Inputs.Any(i => i.Item == item));

        InvPart PartOf(ContainerId id) => _world.Find(id)?.Kind == NodeKind.Station ? InvPart.Output : InvPart.Main;

        int SourceCount(ContainerId id, ItemId item) => _world.Count(id, item, PartOf(id));

        long IslandTotal(ItemId item)
        {
            if (_islandTotals.TryGetValue(item, out var t)) return t;
            t = Scope.Chests(_world, _ctx.Scope).Sum(c => (long)_world.Count(c.Id, item));
            _islandTotals[item] = t;
            return t;
        }

        bool Budget() => _moves < _opt.MovesPerTick;

        bool Budget(ContainerId source)
        {
            if (_moves >= _opt.MovesPerTick) return false;
            var plot = _world.Find(source)?.Plot ?? default;
            return !_plotMoves.TryGetValue(plot, out var n) || n < _opt.MovesPerPlot;
        }

        int Move(ContainerId from, InvPart part, ContainerId to, ItemId item, int amount, string why)
        {
            if (amount <= 0 || from == to) return 0;
            var moved = _world.Transfer(from, part, to, item, amount);
            if (moved <= 0) return 0;
            _moves++;
            var plot = _world.Find(from)?.Plot ?? default;
            _plotMoves[plot] = (_plotMoves.TryGetValue(plot, out var n) ? n : 0) + 1;
            _report.Moves.Add(new MoveRecord(from, to, item, moved, why));
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
                var ids = e.Kind == EntryKind.MassDeposit ? DepositPlanner.ReverseOrder(ctx, blood) : graph.Expand(e);
                foreach (var id in ids)
                    if (world.Find(id) is { Kind: not NodeKind.Heart } && !result.Contains(id)) result.Add(id);
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
