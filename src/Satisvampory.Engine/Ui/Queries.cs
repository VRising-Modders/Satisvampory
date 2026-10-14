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

            bool WouldLoop(ContainerId other)
            {
                var (from, to) = q.Side == PickerSide.Input ? (other, q.Self) : (q.Self, other);
                var trial = edges.Append(new Edge(from, to, new ItemFilter(), q.Self, q.Side == PickerSide.Output, long.MaxValue)).ToList();
                return LoopFinder.Find(trial).Any(l => l.Members.Contains(q.Self) && l.Members.Contains(other));
            }

            void AddContainer(ContainerInfo c, bool inIsland)
            {
                var label = c.Plot == q.Standing ? "here" : plotLabel(c.Plot);
                var hint = Hint(world, catalog, c);
                if (!Matches(c.DisplayName, label, hint)) return;

                string? refused = null;
                if (c.Id == q.Self) refused = "This is the container you are editing";
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
                var hint = belt.Members.Count == 1 ? "1 chest" : $"{belt.Members.Count} chests";
                if (!Matches(belt.Name, string.Empty, hint)) continue;
                rows.Add(new PickerRow
                {
                    Kind = PickerRowKind.Belt,
                    Section = PickerSection.Belts,
                    Belt = belt.Id,
                    Name = belt.Name,
                    Hint = hint,
                    Refused = belt.Members.Contains(q.Self) ? "This container is on that belt" : null,
                    ClosesLoop = belt.Members.Any(WouldLoop),
                });
            }

            if (Matches("Mass Deposit", string.Empty, string.Empty))
                rows.Add(new PickerRow
                {
                    Kind = PickerRowKind.MassDeposit,
                    Section = PickerSection.MassDeposit,
                    Name = "Mass Deposit",
                    Hint = q.Side == PickerSide.Output ? "send each item where it belongs" : "pull from wherever items sit worst",
                });

            return rows
                .OrderBy(r => r.Section)
                .ThenBy(r => r.Refused == null ? 0 : 1)
                .ThenBy(r => r.Distance ?? float.MaxValue)
                .ThenBy(r => r.PlotLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
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

    public sealed record FoundChest(ContainerId Id, string Name, PlotId Plot, string PlotLabel, bool Here, int Count);

    public sealed class FoundItem
    {
        public ItemId Item { get; init; }
        public string Name { get; init; } = string.Empty;
        public long Total { get; init; }
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

        /// <summary>
        /// Exact item or alias first; then a category; then every item whose name contains the text.
        /// Chest names are searched too. Nothing is guessed: a partial match on several items is
        /// returned as an ambiguous list.
        /// </summary>
        public static FindResult Run(IWorldView world, ItemCatalog catalog, IReadOnlyList<PlotId> island, PlotId standing, Func<PlotId, string> plotLabel, string query)
        {
            var text = NameFold.Fold(query);
            var items = new List<ItemId>();
            var categories = new List<string>();
            var ambiguous = false;

            if (text.Length > 0)
            {
                if (catalog.TryResolveItem(text, out var exact)) items.Add(exact);
                else if (catalog.TryResolveCategory(text, out var cat))
                {
                    categories.Add(cat);
                    items.AddRange(catalog.Members(cat));
                }
                else
                {
                    items.AddRange(catalog.AllItems.Where(i => NameFold.Fold(catalog.NameOf(i)).Contains(text)).Take(MaxItems));
                    ambiguous = items.Count > 1;
                }
            }

            var result = new FindResult { Ambiguous = ambiguous };
            result.Categories.AddRange(categories);

            var chests = island.SelectMany(world.ContainersOn).Where(c => c.Kind == NodeKind.Chest).ToList();
            foreach (var item in items)
            {
                var found = chests
                    .Select(c => (c, n: world.Count(c.Id, item)))
                    .Where(x => x.n > 0)
                    .Select(x => new FoundChest(x.c.Id, x.c.DisplayName, x.c.Plot, x.c.Plot == standing ? "here" : plotLabel(x.c.Plot), x.c.Plot == standing, x.n))
                    .OrderBy(f => f.Here ? 0 : 1).ThenBy(f => f.PlotLabel, StringComparer.OrdinalIgnoreCase).ThenByDescending(f => f.Count)
                    .ToList();
                result.Items.Add(new FoundItem { Item = item, Name = catalog.NameOf(item), Total = found.Sum(f => (long)f.Count), Chests = found });
            }
            result.Items.Sort((a, b) => b.Total.CompareTo(a.Total));

            if (text.Length > 0)
                result.ChestsByName.AddRange(chests
                    .Where(c => NameFold.Fold(c.DisplayName).Contains(text))
                    .Select(c => new FoundChest(c.Id, c.DisplayName, c.Plot, c.Plot == standing ? "here" : plotLabel(c.Plot), c.Plot == standing, world.Contents(c.Id).Sum(s => s.Amount)))
                    .OrderBy(f => f.Here ? 0 : 1).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase));

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
                : best.RuleLine > 0 ? $"Rule on line {best.RuleLine}" : best.Priority != 0 ? $"{best.Row}, priority {best.Priority}" : best.Row;
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
