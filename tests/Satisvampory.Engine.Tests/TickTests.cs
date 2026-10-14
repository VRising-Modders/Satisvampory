// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Loops;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Stations;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class TickTests
    {
        static readonly PlotId Home = new(1);
        static readonly PlotId Away = new(2);

        readonly ItemCatalog _cat = Catalog();
        readonly FakeWorld _w = new();
        readonly LogisticsGraph _g = new();
        readonly CastleRules _rules = new() { DefaultReserve = 0 };
        readonly TickState _state = new();

        TickReport Tick(double now = 0, PlotId[]? scope = null, TickOptions? opt = null)
        {
            var ctx = new DepositContext(_cat, _w, scope ?? new[] { Home, Away }, Home);
            return new ConveyorTick(_w, ctx, _g, _rules, _state, opt ?? new TickOptions { MovesPerPlot = 1000, MovesPerTick = 1000 }).Run(now);
        }

        ContainerInfo Furnace(bool smeltOn = true)
        {
            var f = _w.Chest(Home, "Furnace", kind: NodeKind.Station);
            _w.StationRecipes[f.Id] = new List<Recipe>
            {
                new(1, new[] { new ItemStack(IronOre, 10), new ItemStack(Coal, 1) }, new[] { new ItemStack(IronIngot, 1) }, smeltOn),
            };
            return f;
        }

        [Fact]
        public void Chest_edge_moves_only_what_the_filter_allows()
        {
            var a = _w.Chest(Home, "in");
            var b = _w.Chest(Away, "out");
            _w.Put(a, IronOre, 50).Put(a, Plank, 20);
            var entry = ListEntry.Of(b.Id);
            entry.Filter.Categories.Add("ore");
            _g.For(a.Id).Output.Add(entry);

            Tick();
            Assert.Equal(50, _w.Count(b.Id, IronOre));
            Assert.Equal(20, _w.Count(a.Id, Plank));
        }

        [Fact]
        public void Furnace_pulls_whole_crafts_only_and_honors_reserve()
        {
            _rules.DefaultReserve = 10;
            var f = Furnace();
            var ore = _w.Chest(Home, "Ore in");
            _w.Put(ore, IronOre, 47).Put(ore, Coal, 100);
            _g.For(f.Id).Input.Add(ListEntry.Of(ore.Id));

            Tick();
            // 47 ore minus reserve 10 = 37 available → 3 whole crafts.
            Assert.Equal(30, _w.Count(f.Id, IronOre));
            Assert.Equal(3, _w.Count(f.Id, Coal));
            Assert.Equal(17, _w.Count(ore.Id, IronOre));
        }

        [Fact]
        public void Furnace_without_a_full_craft_pulls_nothing()
        {
            var f = Furnace();
            var ore = _w.Chest(Home, "Ore in");
            _w.Put(ore, IronOre, 500);
            _g.For(f.Id).Input.Add(ListEntry.Of(ore.Id));

            var report = Tick();
            Assert.Equal(0, _w.Count(f.Id, IronOre));
            Assert.Contains("not-enough", report.Diagnostics[f.Id]);
        }

        [Fact]
        public void Leftovers_return_to_their_source_after_the_grace()
        {
            var f = Furnace();
            var ore = _w.Chest(Home, "Ore in");
            _w.Put(ore, IronOre, 20).Put(ore, Coal, 2);
            _g.For(f.Id).Input.Add(ListEntry.Of(ore.Id));
            Tick(0);
            Assert.Equal(20, _w.Count(f.Id, IronOre));

            _w.StationRecipes[f.Id] = _w.StationRecipes[f.Id].Select(r => r with { Enabled = false }).ToList();
            Tick(1);
            Assert.Equal(20, _w.Count(f.Id, IronOre)); // grace not over
            Tick(40);
            Assert.Equal(0, _w.Count(f.Id, IronOre));
            Assert.Equal(20, _w.Count(ore.Id, IronOre));
        }

        [Fact]
        public void Station_output_goes_to_mass_deposit()
        {
            var f = Furnace();
            var ingots = _w.Chest(Home, "ingots");
            _w.Put(f, IronIngot, 7, InvPart.Output);
            _g.For(f.Id).Output.Add(ListEntry.MassDeposit());

            Tick();
            Assert.Equal(7, _w.Count(ingots.Id, IronIngot));
        }

        [Fact]
        public void Cycle_once_loop_sleeps_then_wakes_on_an_outside_change()
        {
            var a = _w.Chest(Home, "a");
            var b = _w.Chest(Home, "b");
            _g.For(a.Id).Output.Add(ListEntry.Of(b.Id, _g.NextSequence()));
            _g.For(b.Id).Output.Add(ListEntry.Of(a.Id, _g.NextSequence()));
            _w.Put(a, Plank, 10);

            var moves = 0;
            for (var t = 0; t < 6; t++) moves += Tick(t).Moves.Count;
            Assert.InRange(moves, 1, 4);
            Assert.Empty(Tick(7).Moves);

            _w.Put(a, Plank, 5);
            Assert.NotEmpty(Tick(8).Moves);
        }

        [Fact]
        public void Loop_mode_off_drops_only_the_closing_edge()
        {
            var a = _w.Chest(Home, "a");
            var b = _w.Chest(Home, "b");
            _g.For(a.Id).Output.Add(ListEntry.Of(b.Id, _g.NextSequence()));
            _g.For(b.Id).Output.Add(ListEntry.Of(a.Id, _g.NextSequence()));
            _w.Put(a, Plank, 10);
            var report = Tick(0, opt: new TickOptions { LoopsAllowed = false, MovesPerPlot = 100, MovesPerTick = 100 });
            Assert.Equal(10, _w.Count(b.Id, Plank));
            Assert.Single(report.Loops);
            for (var t = 1; t < 5; t++) Tick(t, opt: new TickOptions { LoopsAllowed = false });
            Assert.Equal(10, _w.Count(b.Id, Plank));
        }

        [Fact]
        public void Even_distribution_balances_a_loop()
        {
            var a = _w.Chest(Home, "a");
            var b = _w.Chest(Home, "b");
            _g.For(a.Id).Output.Add(ListEntry.Of(b.Id, _g.NextSequence()));
            _g.For(b.Id).Output.Add(ListEntry.Of(a.Id, _g.NextSequence()));
            _w.Put(a, Plank, 1000);
            Tick(0);
            _state.Loops.Values.Single().Mode = LoopMode.EvenDistribution;
            for (var t = 1; t < 10; t++) Tick(t);
            var diff = System.Math.Abs(_w.Count(a.Id, Plank) - _w.Count(b.Id, Plank));
            Assert.True(diff <= 100, $"diff {diff}");
        }

        [Fact]
        public void Edge_to_a_plot_outside_the_island_is_out_of_scope()
        {
            var a = _w.Chest(Home, "a");
            var b = _w.Chest(Away, "b");
            _w.Put(a, Plank, 10);
            _g.For(a.Id).Output.Add(ListEntry.Of(b.Id));
            var report = Tick(scope: new[] { Home });
            Assert.Equal(0, _w.Count(b.Id, Plank));
            Assert.Contains("out-of-scope", report.Diagnostics[a.Id]);
        }

        [Fact]
        public void Per_plot_budget_caps_moves()
        {
            var a = _w.Chest(Home, "a");
            var b = _w.Chest(Home, "b");
            foreach (var item in new[] { Plank, Lumber, Coal, Ruby, IronOre }) _w.Put(a, item, 1);
            _g.For(a.Id).Output.Add(ListEntry.Of(b.Id));
            var report = Tick(opt: new TickOptions { MovesPerPlot = 2 });
            Assert.Equal(2, report.Moves.Count);
        }
    }
}

namespace Satisvampory.Engine.Tests
{
    public class HeartListTests
    {
        static readonly PlotId Home = new(1);

        [Fact]
        public void Hearts_are_not_pulled_from_unless_their_own_output_says_so()
        {
            var cat = TestItems.Catalog();
            var w = new FakeWorld();
            var g = new LogisticsGraph();
            var heart = w.Chest(Home, "", kind: NodeKind.Heart);
            var chest = w.Chest(Home, "misc");
            w.Put(heart, TestItems.BloodEssence, 500);
            g.For(chest.Id).Input.Add(ListEntry.Of(heart.Id));
            var ctx = new DepositContext(cat, w, new[] { Home }, Home);
            new ConveyorTick(w, ctx, g, new CastleRules(), new TickState()).Run(0);
            Assert.Equal(0, w.Count(chest.Id, TestItems.BloodEssence));

            g.For(heart.Id).Output.Add(ListEntry.Of(chest.Id));
            new ConveyorTick(w, ctx, g, new CastleRules(), new TickState()).Run(1);
            Assert.Equal(500, w.Count(chest.Id, TestItems.BloodEssence));
            Assert.DoesNotContain(Deposit.DepositRanker.Rank(ctx, TestItems.BloodEssence).InOrder, c => c.Chest == heart);
        }

        [Fact]
        public void Heart_feed_uses_its_input_list_else_the_island()
        {
            var cat = TestItems.Catalog();
            var w = new FakeWorld();
            var g = new LogisticsGraph();
            var heart = w.Chest(Home, "", kind: NodeKind.Heart);
            var other = w.Chest(Home, "", kind: NodeKind.Heart);
            var a = w.Chest(Home, "misc");
            var b = w.Chest(Home, "blood");
            foreach (var c in new[] { a, b, other }) w.Put(c, TestItems.BloodEssence, 100);
            var ctx = new DepositContext(cat, w, new[] { Home }, Home);

            Assert.Equal(new[] { a.Id, b.Id }, ConveyorTick.HeartSources(w, ctx, g, heart.Id, TestItems.BloodEssence));

            g.For(heart.Id).Input.Add(ListEntry.Of(b.Id));
            g.For(heart.Id).Input.Add(ListEntry.Of(other.Id));
            Assert.Equal(new[] { b.Id }, ConveyorTick.HeartSources(w, ctx, g, heart.Id, TestItems.BloodEssence));
        }
    }
}
