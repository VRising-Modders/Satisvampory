// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Player;
using Satisvampory.Engine.Stations;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class InventoryReportTests
    {
        static readonly PlotId Home = new(1), Away = new(2);

        [Fact]
        public void Builds_totals_status_and_rule_sources_and_sorts()
        {
            var cat = Catalog();
            var w = new FakeWorld();
            var a = w.Chest(Home, "ore");
            var b = w.Chest(Away, "misc");
            w.Put(a, IronOre, 30).Put(b, IronOre, 20).Put(b, Plank, 5).Put(a, Ruby, 400);
            var f = w.Chest(Home, "Furnace", kind: NodeKind.Station);
            w.Put(f, Coal, 3);
            w.StationRecipes[f.Id] = new List<Recipe> { new(1, new[] { new ItemStack(IronOre, 10), new ItemStack(Coal, 1) }, new ItemStack[0], true) };

            var rules = new CastleRules { DefaultReserve = 10 };
            rules.CategoryReserve["wood"] = 50;
            rules.ItemCap[Ruby] = 300;

            var rows = InventoryReport.Build(w, cat, new[] { Home, Away }, Home, rules).ToDictionary(r => r.Item);

            var ore = rows[IronOre];
            Assert.Equal(50, ore.Total);
            Assert.Equal(30, ore.Here);
            Assert.Equal(2, ore.Chests);
            Assert.Equal(50, ore.Demand);           // 10 per craft × 5
            Assert.Equal(40, ore.Spare);
            Assert.Equal(InventoryStatus.Short, ore.Status);
            Assert.Equal(20, ore.PerPlot[Away]);

            Assert.Equal(RuleSource.Category, rows[Plank].ReserveFrom);
            Assert.Equal(InventoryStatus.BelowReserve, rows[Plank].Status);
            Assert.Equal(InventoryStatus.AtCap, rows[Ruby].Status);
            Assert.Equal(RuleSource.Item, rows[Ruby].CapFrom);
            Assert.Equal(3, rows[Coal].InStations);
            Assert.Equal(InventoryStatus.Short, rows[Coal].Status);

            var byTotal = InventoryReport.Sort(rows.Values, new[] { new SortKey(InventoryColumn.Total, Descending: true) });
            Assert.Equal(Ruby, byTotal[0].Item);
            var byCap = InventoryReport.Sort(rows.Values, new[] { new SortKey(InventoryColumn.Cap) });
            Assert.Equal(Ruby, byCap[0].Item); // the only capped item; unlimited sorts last
            var multi = InventoryReport.Sort(rows.Values, new[] { new SortKey(InventoryColumn.Status, true), new SortKey(InventoryColumn.Total, true) });
            Assert.Equal(IronOre, multi[0].Item); // Short, and more stock than coal
        }
    }
}
