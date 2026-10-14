// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Stations;
using Satisvampory.Engine.Ui;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class QueryTests
    {
        static readonly PlotId Home = new(1), Away = new(2), Outside = new(3);
        static string Label(PlotId p) => p == Home ? "Hollow Keep" : p == Away ? "Plot 2" : "Plot 3";

        readonly ItemCatalog _cat = Catalog();
        readonly FakeWorld _w = new();
        readonly LogisticsGraph _g = new();

        [Fact]
        public void Picker_sections_greys_hints_and_loop_flags()
        {
            var self = _w.Chest(Home, "Ore in");
            self.X = 0;
            var near = _w.Chest(Home, "Near");
            near.X = 3;
            var far = _w.Chest(Home, "Far");
            far.X = 30;
            _w.Put(far, Plank, 320);
            var away = _w.Chest(Away, "Ore");
            var trash = _w.Chest(Home, "Bin");
            trash.Deposit.Special = SpecialKind.Trash;
            var skip = _w.Chest(Home, "Hidden");
            skip.Deposit.Special = SpecialKind.Skip;
            var heart = _w.Chest(Home, "", kind: NodeKind.Heart);
            var furnace = _w.Chest(Home, "Furnace", kind: NodeKind.Station);
            _w.StationRecipes[furnace.Id] = new List<Recipe> { new(1, new[] { new ItemStack(IronOre, 10) }, new ItemStack[0], true) };
            _w.Chest(Outside, "Their chest");
            _g.For(near.Id).Input.Add(ListEntry.Of(self.Id));    // self → near already
            var belt = _g.BeltOrCreate("Ore belt");
            belt.Members.Add(away.Id);

            var rows = Picker.Rows(_w, _cat, _g, new[] { Home, Away }, Label, new PickerQuery
            {
                Self = self.Id,
                Side = PickerSide.Input,
                Standing = Home,
                Player = (0, 0),
                OutsideIsland = new[] { Outside },
            });

            PickerRow Row(string name) => rows.Single(r => r.Name == name);
            Assert.Equal(PickerSection.Here, rows[0].Section);
            Assert.True(Row("Near").ClosesLoop);                         // near → self would close a loop
            Assert.Null(Row("Near").Refused);
            Assert.Equal("holds 320 Plank", Row("Far").Hint);
            Assert.True(Row("Near").Distance < Row("Far").Distance);
            Assert.Equal("Plot 2", Row("Ore").PlotLabel);
            Assert.Equal(PickerSection.SpecialChests, Row("Bin").Section);
            Assert.NotNull(Row("Hidden").Refused);
            Assert.Equal("Hearts only give items through their own Output", rows.Single(r => r.Container == heart.Id).Refused);
            Assert.Equal("wants Iron Ore", Row("Furnace").Hint);
            Assert.Equal("Outside your sharing island", Row("Their chest").Refused);
            Assert.Equal(PickerSection.Belts, Row("Ore belt").Section);
            Assert.Equal(PickerSection.MassDeposit, rows.Last().Section);

            var searched = Picker.Rows(_w, _cat, _g, new[] { Home, Away }, Label, new PickerQuery { Self = self.Id, Side = PickerSide.Input, Standing = Home, Search = "plank" });
            Assert.Equal(new[] { "Far" }, searched.Select(r => r.Name));
        }

        [Fact]
        public void Find_item_category_partial_and_chest_names()
        {
            var a = _w.Chest(Home, "Ore chest");
            var b = _w.Chest(Away, "misc");
            _w.Put(a, IronOre, 10).Put(b, IronOre, 40).Put(b, CopperOre, 5);

            var exact = Find.Run(_w, _cat, new[] { Home, Away }, Home, Label, "iron ore");
            var item = Assert.Single(exact.Items);
            Assert.Equal(50, item.Total);
            Assert.True(item.Chests[0].Here);
            Assert.False(exact.Ambiguous);

            var cat = Find.Run(_w, _cat, new[] { Home, Away }, Home, Label, "ores");
            Assert.Equal(new[] { "ore" }, cat.Categories);
            Assert.Equal(2, cat.Items.Count);

            var partial = Find.Run(_w, _cat, new[] { Home, Away }, Home, Label, "ore");
            Assert.True(partial.Categories.Count == 1 || partial.Ambiguous);
            var chestHit = Find.Run(_w, _cat, new[] { Home, Away }, Home, Label, "chest");
            Assert.Equal("Ore chest", Assert.Single(chestHit.ChestsByName).Name);

            var iron = Find.Run(_w, _cat, new[] { Home, Away }, Home, Label, "iron");
            Assert.True(iron.Ambiguous);
        }

        [Fact]
        public void Preview_names_the_row_priority_and_rule()
        {
            var exact = _w.Chest(Home, "Iron Ore", capacity: 50);
            var general = _w.Chest(Home, "");
            var ctx = new DepositContext(_cat, _w, new[] { Home }, Home);
            var p = Preview.For(ctx, IronOre);
            Assert.Equal(exact, p.Best!.Chest);
            Assert.Equal(general, p.RunnerUp!.Chest);
            Assert.Equal("Exact names", p.Why);
            Assert.Equal(exact.Id, p.NextStackGoesTo);

            exact.Deposit.BasePriority = 2;
            Assert.Equal("Priority chests, priority 2", Preview.For(ctx, IronOre).Why);

            var script = AdvancedScript.Compile("for \"Iron Ore\": first general", _cat);
            var ruled = new DepositContext(_cat, _w, new[] { Home }, Home) { Script = script };
            Assert.Equal("Rule on line 1", Preview.For(ruled, IronOre).Why);

            Assert.Equal("No chest on the island takes this item", Preview.For(new DepositContext(_cat, _w, new[] { Away }, Away), IronOre).Why);
        }
    }
}
