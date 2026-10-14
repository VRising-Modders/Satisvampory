// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Stations;
using Satisvampory.Engine.World;

namespace Satisvampory.Engine.Player
{
    public enum InventoryStatus
    {
        None,
        AtCap,
        BelowReserve,
        NoStock,
        Short,
    }

    public enum RuleSource
    {
        Default,
        Category,
        Item,
    }

    /// <summary>One row of the Inventory page (spec 15).</summary>
    public sealed class InventoryRow
    {
        public ItemId Item { get; init; }
        public string Name { get; init; } = string.Empty;
        public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();
        public long Total { get; init; }
        public long Here { get; init; }
        public int Chests { get; init; }
        public long InStations { get; init; }
        public int Reserve { get; init; }
        public RuleSource ReserveFrom { get; init; }
        public long Spare => Math.Max(0, Total - Reserve);
        public int? Cap { get; init; }
        public RuleSource CapFrom { get; init; }
        public long Demand { get; init; }
        public InventoryStatus Status { get; init; }
        public IReadOnlyDictionary<PlotId, long> PerPlot { get; init; } = new Dictionary<PlotId, long>();
    }

    public enum InventoryColumn
    {
        Item,
        Category,
        Total,
        Here,
        Chests,
        InStations,
        Reserve,
        Spare,
        Cap,
        Demand,
        Status,
    }

    public readonly record struct SortKey(InventoryColumn Column, bool Descending = false);

    public static class InventoryReport
    {
        /// <summary>Build every row for the island. Items with no stock appear only when stations want them.</summary>
        public static List<InventoryRow> Build(IWorldView world, ItemCatalog catalog, IReadOnlyList<PlotId> island, PlotId standing, CastleRules rules, int feedCrafts = StationPlanner.DefaultFeedCrafts)
        {
            var total = new Dictionary<ItemId, long>();
            var here = new Dictionary<ItemId, long>();
            var chests = new Dictionary<ItemId, int>();
            var stations = new Dictionary<ItemId, long>();
            var perPlot = new Dictionary<ItemId, Dictionary<PlotId, long>>();
            var demand = new Dictionary<ItemId, long>();

            static void Add<TK>(Dictionary<TK, long> d, TK k, long n) where TK : notnull => d[k] = (d.TryGetValue(k, out var v) ? v : 0) + n;

            foreach (var plot in island)
                foreach (var c in world.ContainersOn(plot))
                {
                    if (c.Kind == NodeKind.Chest)
                    {
                        foreach (var s in world.Contents(c.Id))
                        {
                            Add(total, s.Item, s.Amount);
                            if (plot == standing) Add(here, s.Item, s.Amount);
                            chests[s.Item] = (chests.TryGetValue(s.Item, out var n) ? n : 0) + 1;
                            if (!perPlot.TryGetValue(s.Item, out var pp)) perPlot[s.Item] = pp = new Dictionary<PlotId, long>();
                            Add(pp, plot, s.Amount);
                        }
                    }
                    else if (c.Kind == NodeKind.Station)
                    {
                        foreach (var s in world.Contents(c.Id).Concat(world.Contents(c.Id, InvPart.Output)))
                            Add(stations, s.Item, s.Amount);
                        foreach (var r in world.Recipes(c.Id).Where(r => r.Enabled))
                            foreach (var input in r.Inputs)
                                Add(demand, input.Item, (long)input.Amount * feedCrafts);
                    }
                }

            var items = total.Keys.Concat(demand.Keys).Concat(stations.Keys).Distinct();
            var rows = new List<InventoryRow>();
            foreach (var item in items)
            {
                var t = total.TryGetValue(item, out var tv) ? tv : 0;
                var reserve = rules.Reserve(item, catalog);
                var cap = rules.Cap(item, catalog);
                var want = demand.TryGetValue(item, out var dv) ? dv : 0;
                var spare = Math.Max(0, t - reserve);

                var status = InventoryStatus.None;
                if (want > spare && want > 0) status = InventoryStatus.Short;
                else if (t == 0) status = InventoryStatus.NoStock;
                else if (t < reserve) status = InventoryStatus.BelowReserve;
                else if (cap is { } c && t >= c) status = InventoryStatus.AtCap;

                rows.Add(new InventoryRow
                {
                    Item = item,
                    Name = catalog.NameOf(item),
                    Categories = catalog.CategoriesOf(item).OrderBy(x => x).ToList(),
                    Total = t,
                    Here = here.TryGetValue(item, out var hv) ? hv : 0,
                    Chests = chests.TryGetValue(item, out var cv) ? cv : 0,
                    InStations = stations.TryGetValue(item, out var sv) ? sv : 0,
                    Reserve = reserve,
                    ReserveFrom = rules.ItemReserve.ContainsKey(item) ? RuleSource.Item
                        : rules.CategoryReserve.Keys.Any(k => catalog.InCategory(item, k)) ? RuleSource.Category : RuleSource.Default,
                    Cap = cap,
                    CapFrom = rules.ItemCap.ContainsKey(item) ? RuleSource.Item
                        : rules.CategoryCap.Keys.Any(k => catalog.InCategory(item, k)) ? RuleSource.Category : RuleSource.Default,
                    Demand = want,
                    Status = status,
                    PerPlot = perPlot.TryGetValue(item, out var p) ? p : new Dictionary<PlotId, long>(),
                });
            }
            return rows;
        }

        /// <summary>Multi-key sort. Unlimited caps sort last ascending; ties fall back to item name.</summary>
        public static List<InventoryRow> Sort(IEnumerable<InventoryRow> rows, IReadOnlyList<SortKey> keys)
        {
            IOrderedEnumerable<InventoryRow>? ordered = null;
            foreach (var k in keys.Count == 0 ? new[] { new SortKey(InventoryColumn.Item) } : keys)
            {
                Func<InventoryRow, IComparable> f = k.Column switch
                {
                    InventoryColumn.Item => r => r.Name,
                    InventoryColumn.Category => r => r.Categories.FirstOrDefault() ?? "￿",
                    InventoryColumn.Total => r => r.Total,
                    InventoryColumn.Here => r => r.Here,
                    InventoryColumn.Chests => r => r.Chests,
                    InventoryColumn.InStations => r => r.InStations,
                    InventoryColumn.Reserve => r => r.Reserve,
                    InventoryColumn.Spare => r => r.Spare,
                    InventoryColumn.Cap => r => r.Cap is { } c ? (long)c : long.MaxValue,
                    InventoryColumn.Demand => r => r.Demand,
                    _ => r => (int)r.Status,
                };
                ordered = ordered == null
                    ? (k.Descending ? rows.OrderByDescending(f) : rows.OrderBy(f))
                    : (k.Descending ? ordered.ThenByDescending(f) : ordered.ThenBy(f));
            }
            return ordered!.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
