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
    public class EvaluatorTests
    {
        static readonly PlotId Home = new(1), Away = new(2);
        readonly ItemCatalog _cat = Catalog();
        readonly FakeWorld _w = new();

        DepositContext Ctx() => new(_cat, _w, new[] { Home, Away }, Home) { PlotLabel = p => p == Home ? "Hollow Keep" : "Plot 2" };

        static string Reason(Evaluation e, ContainerInfo c) => e.NotUsed.Single(s => s.Id == c.Id).Reason;

        [Fact]
        public void Push_shows_order_amounts_and_why_each_chest_is_not_used()
        {
            var best = _w.Chest(Home, "Ore bin", capacity: 100).OnlyCategory("ore", 3);
            var second = _w.Chest(Away, "Ore 2", capacity: 1000).OnlyItem(IronOre, 1);
            var general = _w.Chest(Home, "General");
            var planks = _w.Chest(Home, "Planks").OnlyItem(Plank);
            var banned = _w.Chest(Home, "No ore");
            banned.Deposit.BannedCategories.Add("ore");
            var trash = _w.Chest(Home, "Trash").Kind(SpecialKind.Trash);
            var full = _w.Chest(Home, "Full", capacity: 0).OnlyItem(IronOre, 2);

            var e = Evaluator.Push(Ctx(), IronOre, 150);
            Assert.Equal(new[] { (best.Id, 100), (second.Id, 50) }, e.Steps.Select(s => (s.Id, s.Amount)));
            Assert.Equal("Category ore, priority 3", e.Steps[0].Why);
            Assert.Equal("Plot 2", e.Steps[1].Plot);
            Assert.Equal(0, e.Short);
            Assert.Equal("Not needed: better homes took it all", Reason(e, general));
            Assert.Equal("Takes only listed items, and this is not one", Reason(e, planks));
            Assert.Equal("Bans category ore", Reason(e, banned));
            Assert.StartsWith("Trash chest", Reason(e, trash));
            Assert.Equal("No free space", Reason(e, full));
        }

        [Fact]
        public void Pull_shows_worst_home_first_reserve_and_last_stack()
        {
            var best = _w.Chest(Home, "Best").OnlyItem(IronOre, 5);
            var stray = _w.Chest(Home, "Stray").OnlyItem(Plank);
            var single = _w.Chest(Away, "Single");
            var empty = _w.Chest(Home, "Empty");
            _w.Put(best, IronOre, 300).Put(stray, IronOre, 150).Put(single, IronOre, 40);

            var e = Evaluator.Pull(Ctx(), IronOre, 250, new Evaluator.PullOptions { NeverLastStack = true });
            Assert.Equal(stray.Id, e.Steps[0].Id);
            Assert.Equal(50, e.Steps[0].Amount);
            Assert.Equal("Not a home for it, drained first", e.Steps[0].Why);
            Assert.Equal("Keeps its last stack", Reason(e, single));
            Assert.Equal("Holds none", Reason(e, empty));
            Assert.Equal(250, e.Covered);

            var reserved = Evaluator.Pull(Ctx(), IronOre, 1000, new Evaluator.PullOptions { Reserve = 100 });
            Assert.Equal(390, reserved.Covered);
            Assert.Equal(610, reserved.Short);
        }

        [Fact]
        public void Pull_from_an_input_list_names_chests_outside_it()
        {
            var a = _w.Chest(Home, "A");
            var b = _w.Chest(Home, "B");
            _w.Put(a, Coal, 10).Put(b, Coal, 10);
            var e = Evaluator.Pull(Ctx(), Coal, 5, new Evaluator.PullOptions { Sources = new[] { a.Id } });
            Assert.Equal(a.Id, e.Steps.Single().Id);
            Assert.Equal("Not in this Input list", Reason(e, b));
        }

        [Fact]
        public void Output_list_walks_rows_with_filters_stations_and_mass_deposit()
        {
            var src = _w.Chest(Home, "Src");
            var furnace = _w.Chest(Home, "Furnace", kind: NodeKind.Station);
            _w.StationRecipes[furnace.Id] = new List<Recipe> { new(1, new[] { new ItemStack(Coal, 1) }, new ItemStack[0], true) };
            var stoneOnly = _w.Chest(Home, "Stone only");
            var bin = _w.Chest(Home, "Bin", capacity: 30);
            var home = _w.Chest(Away, "Ore home").OnlyItem(IronOre, 2);
            var g = new LogisticsGraph();
            g.For(src.Id).Output.Add(ListEntry.Of(furnace.Id));
            var filtered = ListEntry.Of(stoneOnly.Id);
            filtered.Filter.Items.Add(Plank);
            g.For(src.Id).Output.Add(filtered);
            g.For(src.Id).Output.Add(ListEntry.Of(bin.Id));
            g.For(src.Id).Output.Add(ListEntry.MassDeposit());

            var e = Evaluator.OutputList(Ctx(), g, src.Id, IronOre, 100);
            EvalStep Step(ContainerInfo c) => e.Steps.Single(s => s.Id == c.Id);
            Assert.Equal((0, "Row 1: no recipe switched on uses this item"), (Step(furnace).Amount, Step(furnace).Why));
            Assert.Equal((0, "Row 2: its filter excludes this item"), (Step(stoneOnly).Amount, Step(stoneOnly).Why));
            Assert.Equal(30, Step(bin).Amount);
            Assert.StartsWith("Row 4, Mass Deposit:", Step(home).Why);
            Assert.Equal(100, e.Covered);
        }

        [Fact]
        public void Mass_deposit_first_then_a_chest_shows_the_chest_getting_zero()
        {
            // 2,000 Iron Ingot out of a chest whose Output is row 1 Mass Deposit, row 2 a chest
            // that Mass Deposit never ranks.
            var src = _w.Chest(Home, "Furnace out");
            var vault = _w.Chest(Away, "Ingot vault", capacity: 1600).OnlyCategory("ingots", 3);
            var general = _w.Chest(Home, "General", capacity: 5000);
            var spare = _w.Chest(Home, "Spare").OnlyItem(Plank);
            var g = new LogisticsGraph();
            g.For(src.Id).Output.Add(ListEntry.MassDeposit());
            g.For(src.Id).Output.Add(ListEntry.Of(spare.Id));

            var e = Evaluator.OutputList(Ctx(), g, src.Id, IronIngot, 2000);
            var steps = e.Steps.Where(s => s.Id == vault.Id || s.Id == general.Id || s.Id == spare.Id).Select(s => (s.Id, s.Amount)).ToArray();
            Assert.Equal(new[] { (vault.Id, 1600), (general.Id, 400), (spare.Id, 0) }, steps);
            Assert.Equal("Row 2: Mass Deposit in row 1 took it all", e.Steps.Single(s => s.Id == spare.Id).Why);
            Assert.Equal(0, e.Short);
        }
    }
}
