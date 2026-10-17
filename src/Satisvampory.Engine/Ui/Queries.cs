// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Loops;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.World;

namespace Satisvampory.Engine.Ui
{
    public enum PickerSide
    {
        Input,
        Output,
    }

    public enum PickerSection
    {
        Here,
        OtherPlots,
        Belts,
        SpecialChests,
        MassDeposit,
    }

    public enum PickerRowKind
    {
        Container,
        Belt,
        MassDeposit,
    }

    /// <summary>One row of the Add… picker (spec 02, "Pickers").</summary>
    public sealed class PickerRow
    {
        public PickerRowKind Kind { get; init; }
        public PickerSection Section { get; init; }
        public ContainerId? Container { get; init; }
        public BeltId? Belt { get; init; }
        public string Name { get; init; } = string.Empty;
        public string PlotLabel { get; init; } = string.Empty;
        /// <summary>Metres from the player when on the same plot, else null.</summary>
        public float? Distance { get; init; }
        /// <summary>"holds 320 Plank", "wants Iron Ore", "3 chests".</summary>
        public string Hint { get; init; } = string.Empty;
        /// <summary>Why the row is greyed, or null when it can be picked.</summary>
        public string? Refused { get; init; }
        /// <summary>Picking this closes a loop; allowed, but the UI sends the player to the loop settings.</summary>
        public bool ClosesLoop { get; init; }
    }

    public sealed class PickerQuery
    {
        public ContainerId Self { get; init; }
        public PickerSide Side { get; init; }
        public PlotId Standing { get; init; }
        public (float X, float Z)? Player { get; init; }
        public string Search { get; init; } = string.Empty;
        /// <summary>Clan plots outside the island, shown greyed so players see why they are missing.</summary>
        public IReadOnlyList<PlotId> OutsideIsland { get; init; } = Array.Empty<PlotId>();
    }

    public static class Picker
    {
        public static List<PickerRow> Rows(
            IWorldView world, ItemCatalog catalog, LogisticsGraph graph, IReadOnlyList<PlotId> island,
            Func<PlotId, string> plotLabel, PickerQuery q)
        {
            var rows = new List<PickerRow>();
            var edges = graph.Edges();
            var needle = NameFold.Fold(q.Search);
            var self = world.Find(q.Self);

            bool Matches(string name, string label, string hint) =>
                needle.Length == 0 || NameFold.Fold(name).Contains(needle) || NameFold.Fold(label).Contains(needle) || NameFold.Fold(hint).Contains(needle);

            // A new edge other→self (Input) or self→other (Output) closes a loop exactly when a path already
            // runs the other way. One search from this container answers it for every candidate; running the
            // loop finder per candidate over a Default Belt island took seconds.
            var linked = Reachable(edges, q.Self, forward: q.Side == PickerSide.Input);
            bool WouldLoop(ContainerId other) => other != q.Self && linked.Contains(other);

            void AddContainer(ContainerInfo c, bool inIsland)
            {
                var label = c.Plot == q.Standing ? "here" : plotLabel(c.Plot);
                var hint = Hint(world, catalog, c);
                if (!Matches(c.DisplayName, label, hint)) return;

                string? refused = null;
                if (c.Id == q.Self) refused = "This is the container you are editing";
                else if (world.IsRaided(c.Plot)) refused = "Castle is under attack";
                else if (!inIsland) refused = "Outside your sharing island";
                else if (c.Deposit.Special == SpecialKind.Skip) refused = "Skip chests are never used";
                else if (c.Kind == NodeKind.Heart && q.Side == PickerSide.Input) refused = "Hearts only give items through their own Output";
                else if (self?.Kind == NodeKind.Heart && q.Side == PickerSide.Output && c.Kind == NodeKind.Heart) refused = "A heart cannot feed another heart";

                float? dist = null;
                if (c.Plot == q.Standing && q.Player is { } p)
                    dist = MathF.Sqrt((c.X - p.X) * (c.X - p.X) + (c.Z - p.Z) * (c.Z - p.Z));

                var section = c.Deposit.Special != SpecialKind.None && c.Deposit.Special != SpecialKind.Skip
                    ? PickerSection.SpecialChests
                    : c.Plot == q.Standing ? PickerSection.Here : PickerSection.OtherPlots;

                rows.Add(new PickerRow
                {
                    Kind = PickerRowKind.Container,
                    Section = section,
                    Container = c.Id,
                    Name = c.DisplayName,
                    PlotLabel = label,
                    Distance = dist,
                    Hint = c.Deposit.Special is not (SpecialKind.None or SpecialKind.Skip) ? $"{c.Deposit.Special} · {hint}".TrimEnd(' ', '·') : hint,
                    Refused = refused,
                    ClosesLoop = refused == null && WouldLoop(c.Id),
                });
            }

            foreach (var plot in island)
                foreach (var c in world.ContainersOn(plot))
                    AddContainer(c, inIsland: true);
            foreach (var plot in q.OutsideIsland)
                foreach (var c in world.ContainersOn(plot))
                    AddContainer(c, inIsland: false);

            foreach (var belt in graph.Belts.Values)
            {
                var members = (q.Side == PickerSide.Output ? graph.Receivers(belt.Id) : graph.Suppliers(belt.Id)).ToList();
                var hint = $"{graph.Suppliers(belt.Id).Count()} suppliers · {graph.Receivers(belt.Id).Count()} receivers";
                if (!Matches(belt.Name, string.Empty, hint)) continue;
                rows.Add(new PickerRow
                {
                    Kind = PickerRowKind.Belt,
                    Section = PickerSection.Belts,
                    Belt = belt.Id,
                    Name = belt.Name,
                    Hint = hint,
                    Refused = null,
                    ClosesLoop = members.Any(WouldLoop),
                });
            }

            var demand = self != null && Edits.EditService.HasDemand(self);
            if (Matches("Mass Deposit", "push pull reverse", string.Empty) && (q.Side == PickerSide.Output || demand))
                rows.Add(new PickerRow
                {
                    Kind = PickerRowKind.MassDeposit,
                    Section = PickerSection.MassDeposit,
                    Name = q.Side == PickerSide.Output ? "Push: Mass Deposit" : "Pull: reverse Mass Deposit",
                    Hint = q.Side == PickerSide.Output
                        ? "push each item to the chest that rates it highest"
                        : "pull from wherever items are stored worst first",
                });

            return rows
                .OrderBy(r => r.Section)
                .ThenBy(r => r.Refused == null ? 0 : 1)
                .ThenBy(r => r.Distance ?? float.MaxValue)
                .ThenBy(r => r.PlotLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Containers reachable from <paramref name="start"/> along edges (or against them).</summary>
        static HashSet<ContainerId> Reachable(IReadOnlyList<Edge> edges, ContainerId start, bool forward)
        {
            var next = new Dictionary<ContainerId, List<ContainerId>>();
            foreach (var e in edges)
            {
                var (a, b) = forward ? (e.From, e.To) : (e.To, e.From);
                if (!next.TryGetValue(a, out var l)) next[a] = l = new List<ContainerId>();
                l.Add(b);
            }
            var seen = new HashSet<ContainerId>();
            var queue = new Queue<ContainerId>();
            queue.Enqueue(start);
            while (queue.Count > 0)
                if (next.TryGetValue(queue.Dequeue(), out var outs))
                    foreach (var n in outs)
                        if (seen.Add(n)) queue.Enqueue(n);
            return seen;
        }

        static string Hint(IWorldView world, ItemCatalog catalog, ContainerInfo c)
        {
            if (c.Kind == NodeKind.Station)
            {
                var want = world.Recipes(c.Id).Where(r => r.Enabled).SelectMany(r => r.Inputs).Select(s => s.Item).Distinct().FirstOrDefault();
                return want.Guid != 0 ? $"wants {catalog.NameOf(want)}" : "no recipe switched on";
            }
            var top = world.Contents(c.Id).OrderByDescending(s => s.Amount).FirstOrDefault();
            return top.Amount > 0 ? $"holds {top.Amount:N0} {catalog.NameOf(top.Item)}" : "empty";
        }
    }

    // ---------------- find ----------------

    public sealed record FoundChest(ContainerId Id, string Name, PlotId Plot, string PlotLabel, bool Here, int Count, bool Produces = false);

    public sealed class FoundItem
    {
        public ItemId Item { get; init; }
        public string Name { get; init; } = string.Empty;
        public long Total { get; init; }
        public int MatchRank { get; init; }
        /// <summary>Grouped by plot, standing plot first.</summary>
        public List<FoundChest> Chests { get; init; } = new();
    }

    public sealed class FindResult
    {
        public List<FoundItem> Items { get; } = new();
        public List<FoundChest> ChestsByName { get; } = new();
        /// <summary>Categories the query named; the UI offers "Pull one stack of each".</summary>
        public List<string> Categories { get; } = new();
        /// <summary>True when the query matched several items by partial name and the UI should show a pick list.</summary>
        public bool Ambiguous { get; init; }
    }

    public static class Find
    {
        public const int MaxItems = 25;
        public static int MatchRank(string name, string query)
        {
            var value = NameFold.Fold(name); var needle = NameFold.Fold(query);
            if (needle.Length == 0) return 4;
            if (value == needle) return 0;
            if (value.StartsWith(needle, StringComparison.Ordinal)) return 1;
            if ((" " + value + " ").Contains(" " + needle + " ")) return 2;
            return value.Contains(needle) ? 3 : 4;
        }

        /// <summary>
        /// Exact item or alias first; then a category; then every item whose name contains the text.
        /// Chest names are searched too. Nothing is guessed: a partial match on several items is
        /// returned as an ambiguous list.
        /// </summary>
        /// <param name="displayMatches">Items the client matched by their game display name, best first;
        /// they come after the server's own name matches.</param>
        public static FindResult Run(IWorldView world, ItemCatalog catalog, IReadOnlyList<PlotId> island, PlotId standing, Func<PlotId, string> plotLabel, string query, bool partialMatch = true, int kind = 0, Func<ItemId, bool>? includeItem = null,
            IReadOnlyList<ItemId>? displayMatches = null)
        {
            var text = NameFold.Fold(query);
            if (text == "atheneum") text = "athenaeum";
            var items = new List<ItemId>();
            var categories = new List<string>();
            var ambiguous = false;

            if (text.Length > 0 && kind != 1)
            {
                if (catalog.TryResolveItem(text, out var exact)) items.Add(exact);
                else if (partialMatch && catalog.TryResolveCategory(text, out var cat))
                {
                    categories.Add(cat);
                    items.AddRange(catalog.Members(cat));
                }
                else
                {
                    items.AddRange(catalog.AllItems.Where(i => (includeItem == null || includeItem(i)) && MatchRank(catalog.NameOf(i), text) < (partialMatch ? 4 : 1))
                        .OrderBy(i => MatchRank(catalog.NameOf(i), text)).ThenBy(catalog.NameOf, StringComparer.OrdinalIgnoreCase).Take(MaxItems));
                    ambiguous = items.Count > 1;
                }
                if (categories.Count == 0 && displayMatches != null)
                {
                    foreach (var extra in displayMatches)
                        if (!items.Contains(extra) && items.Count < MaxItems) items.Add(extra);
                    ambiguous = items.Count > 1;
                }
            }

            var result = new FindResult { Ambiguous = ambiguous };
            result.Categories.AddRange(categories);

            var chests = island.SelectMany(world.ContainersOn).Where(c => c.Kind is NodeKind.Chest or NodeKind.Station).ToList();
            var contents = chests.ToDictionary(c => c.Id, c => c.Role == StationRole.Refinement
                ? world.Contents(c.Id).Concat(world.Contents(c.Id, InvPart.Output)).ToArray()
                : world.Contents(c.Id).ToArray());
            var outputs = chests.Where(c => c.Kind == NodeKind.Station)
                .ToDictionary(c => c.Id, c => world.Recipes(c.Id).SelectMany(r => r.Outputs).Select(o => o.Item).ToHashSet());
            foreach (var item in items.Where(i => includeItem == null || includeItem(i)).Take(MaxItems))
            {
                var found = chests.Select(c => (c, n: contents[c.Id].Where(s => s.Item == item).Sum(s => s.Amount),
                        makes: outputs.TryGetValue(c.Id, out var products) && products.Contains(item)))
                    .Where(x => x.n > 0 || x.makes)
                    .Select(x => new FoundChest(x.c.Id, x.c.DisplayName, x.c.Plot, plotLabel(x.c.Plot), x.c.Plot == standing, x.n, x.makes))
                    .OrderBy(f => f.Here ? 0 : 1).ThenBy(f => f.Count > 0 ? 0 : 1)
                    .ThenBy(f => f.PlotLabel, StringComparer.OrdinalIgnoreCase).ThenByDescending(f => f.Count)
                    .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var rank = MatchRank(catalog.NameOf(item), text);
                if (displayMatches != null && displayMatches.Contains(item)) rank = Math.Min(rank, 1); // matched by its game name
                result.Items.Add(new FoundItem { Item = item, Name = catalog.NameOf(item), MatchRank = rank, Total = found.Sum(f => (long)f.Count), Chests = found });
            }
            result.Items.Sort((a, b) => { int rank = a.MatchRank.CompareTo(b.MatchRank); return rank != 0 ? rank : b.Total != a.Total ? b.Total.CompareTo(a.Total) : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name); });
            if (text.Length > 0 && kind != 2)
                result.ChestsByName.AddRange(chests.Where(c => MatchRank(c.DisplayName, text) < (partialMatch ? 4 : 1) || MatchRank(c.FurnitureName, text) < (partialMatch ? 4 : 1))
                    .OrderBy(c => Math.Min(MatchRank(c.DisplayName, text), MatchRank(c.FurnitureName, text)))
                    .ThenBy(c => c.Plot == standing ? 0 : 1).ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Take(40).Select(c => new FoundChest(c.Id, c.DisplayName, c.Plot, plotLabel(c.Plot), c.Plot == standing, contents[c.Id].Sum(s => s.Amount))));

            return result;
        }
    }

    // ---------------- Mass Deposit preview ----------------

    public sealed class DepositPreview
    {
        public ItemId Item { get; init; }
        public DepositCandidate? Best { get; init; }
        public DepositCandidate? RunnerUp { get; init; }
        /// <summary>Where the next full stack would actually land, after free space.</summary>
        public ContainerId? NextStackGoesTo { get; init; }
        /// <summary>"Exact names, priority 2", "Rule on line 6".</summary>
        public string Why { get; init; } = string.Empty;
        /// <summary>Every candidate in order, for the expanded view.</summary>
        public List<DepositCandidate> All { get; init; } = new();
    }

    public static class Preview
    {
        public static DepositPreview For(DepositContext ctx, ItemId item)
        {
            var ranking = DepositRanker.Rank(ctx, item).InOrder.ToList();
            var plan = DepositPlanner.Plan(ctx, item, ctx.Catalog.MaxStack(item));
            var best = ranking.FirstOrDefault();
            string why = best == null
                ? "No chest on the island takes this item"
                : best.Why;
            return new DepositPreview
            {
                Item = item,
                Best = best,
                RunnerUp = ranking.Skip(1).FirstOrDefault(),
                NextStackGoesTo = plan.Count > 0 ? plan[0].To : null,
                Why = why,
                All = ranking,
            };
        }
    }
}
