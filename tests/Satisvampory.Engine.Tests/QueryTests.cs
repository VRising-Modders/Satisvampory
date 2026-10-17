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
        public void Picker_stays_fast_on_a_default_belt_island_and_flags_output_loops()
        {
            var state = new Persistence.IslandState();
            for (var i = 0; i < 250; i++) _w.Chest(Home, "Chest " + i);
            for (var i = 0; i < 40; i++) { var s = _w.Chest(Home, "Furnace " + i, kind: NodeKind.Station); s.Role = StationRole.Refinement; }
            DefaultBelt.Initialize(state.Graph, _w.ContainersOn(Home));
            var self = _w.Chest(Home, "Self");
            var feeder = _w.Chest(Home, "Feeder");
            state.Graph.For(feeder.Id).Output.Add(ListEntry.Of(self.Id, state.Graph.NextSequence()));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var rows = Picker.Rows(_w, _cat, state.Graph, new[] { Home }, Label, new PickerQuery { Self = self.Id, Side = PickerSide.Output, Standing = Home });
            watch.Stop();
            Assert.True(watch.ElapsedMilliseconds < 500, $"picker took {watch.ElapsedMilliseconds} ms");
            Assert.True(rows.Single(r => r.Name == "Feeder").ClosesLoop);  // self → feeder closes feeder → self
            Assert.False(rows.Single(r => r.Name == "Chest 3").ClosesLoop);
        }

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
            furnace.FurnitureName = "Furnace";
            var grinder = _w.Chest(Home, "R2S2", kind: NodeKind.Station);
            grinder.FurnitureName = "Grinder";
            _w.StationRecipes[furnace.Id] = new List<Recipe> { new(1, new[] { new ItemStack(IronOre, 10) }, new[] { new ItemStack(IronIngot, 1) }, true) };
            _w.Chest(Outside, "Their chest");
            _g.For(near.Id).Input.Add(ListEntry.Of(self.Id));    // self → near already
            var belt = _g.BeltOrCreate("Ore belt");
            BeltOps.AddMembers(_g, belt.Id, new[] { away.Id });

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
            Assert.Equal("makes Iron Ingot", Row("Furnace").Hint);
            Assert.Contains(rows, r => r.Name == "R2S2 · Grinder");          // a station named apart from its type shows it           // as a source it gives its products
            Assert.Equal("Outside your sharing island", Row("Their chest").Refused);
            Assert.Equal(PickerSection.Belts, Row("Ore belt").Section);
            Assert.DoesNotContain(rows, r => r.Kind == PickerRowKind.MassDeposit); // a chest has no demand to pull for
            var outRows = Picker.Rows(_w, _cat, _g, new[] { Home, Away }, Label, new PickerQuery { Self = self.Id, Side = PickerSide.Output, Standing = Home });
            Assert.Equal("Push: Mass Deposit", outRows.Last().Name);
            Assert.Equal("wants Iron Ore", outRows.Single(r => r.Name == "Furnace").Hint); // as a destination it takes ingredients
            var furnaceRows = Picker.Rows(_w, _cat, _g, new[] { Home, Away }, Label, new PickerQuery { Self = furnace.Id, Side = PickerSide.Input, Standing = Home });
            Assert.Equal("Pull: reverse Mass Deposit", furnaceRows.Last().Name);

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
            Assert.Equal(new[] { "Ore chest", "misc" }, chestHit.ChestsByName.Select(c => c.Name)); // furniture types are searchable too

            var iron = Find.Run(_w, _cat, new[] { Home, Away }, Home, Label, "iron");
            Assert.True(iron.Ambiguous);
        }

        [Fact]
        public void Preview_explains_the_priority_and_rule()
        {
            var exact = _w.Chest(Home, "Iron Ore", capacity: 50).OnlyItem(IronOre, 1);
            var general = _w.Chest(Home, "");
            var ctx = new DepositContext(_cat, _w, new[] { Home }, Home);
            var p = Preview.For(ctx, IronOre);
            Assert.Equal(exact, p.Best!.Chest);
            Assert.Equal(general, p.RunnerUp!.Chest);
            Assert.Equal("Iron Ore +1 on base 0", p.Why);
            Assert.Equal(exact.Id, p.NextStackGoesTo);

            exact.Deposit.BasePriority = 2;
            Assert.Equal("Iron Ore +1 on base 2", Preview.For(ctx, IronOre).Why);

            var script = AdvancedScript.Compile("for \"Iron Ore\": first general", _cat);
            var ruled = new DepositContext(_cat, _w, new[] { Home }, Home) { Script = script };
            Assert.Equal("Rule on line 1", Preview.For(ruled, IronOre).Why);

            Assert.Equal("No chest on the island takes this item", Preview.For(new DepositContext(_cat, _w, new[] { Away }, Away), IronOre).Why);
        }
    }
}

namespace Satisvampory.Engine.Tests
{
    public class MaterialPolicyTests
    {
        [Fact]
        public void Per_item_reverse_order_reserve_once_and_vanilla_fallbacks()
        {
            var cat = TestItems.Catalog();
            var w = new FakeWorld();
            var home = new PlotId(1);
            var ironHome = w.Chest(home, "Iron Ore").OnlyItem(TestItems.IronOre, 5);
            var woodHome = w.Chest(home, "wood").OnlyCategory("wood", 5);
            var dump = w.Chest(home, "");
            var skip = w.Chest(home, "s");
            skip.Deposit.Special = SpecialKind.Skip;
            foreach (var c in new[] { ironHome, woodHome, dump, skip }) w.Put(c, TestItems.IronOre, 10).Put(c, TestItems.Plank, 10);
            var ctx = new DepositContext(cat, w, new[] { home }, home);
            var rules = new Lists.CastleRules { DefaultReserve = 25 };
            var policy = new World.MaterialPolicy();

            var iron = policy.Sources(World.MaterialUse.Craft, true, true, ctx, rules, TestItems.IronOre)!.Value;
            var wood = policy.Sources(World.MaterialUse.Build, true, true, ctx, rules, TestItems.Plank)!.Value;
            Assert.Equal(ironHome.Id, iron.SpendOrder[^1]);   // best home drained last
            Assert.Equal(woodHome.Id, wood.SpendOrder[^1]);   // each item follows its own order
            Assert.DoesNotContain(skip.Id, iron.SpendOrder);
            Assert.Equal(25, iron.PoolReserve);

            Assert.Null(policy.Sources(World.MaterialUse.Research, true, true, ctx, rules, TestItems.IronOre));
            Assert.Null(policy.Sources(World.MaterialUse.Craft, true, false, ctx, rules, TestItems.IronOre));
            var r = policy.Revision;
            policy.Invalidate();
            Assert.True(policy.Revision > r);
        }
    }
}
