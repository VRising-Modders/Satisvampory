// SPDX-License-Identifier: AGPL-3.0-only
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Player;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class PlayerActionTests
    {
        static readonly PlotId Home = new(1);
        static readonly PlotId Away = new(2);

        readonly ItemCatalog _cat = Catalog();
        readonly FakeWorld _w = new();
        readonly ContainerInfo _bag;

        public PlayerActionTests()
        {
            // The bag lives off-plot so it is never a home or a source.
            _bag = _w.Chest(new PlotId(999), "bag", capacity: 400);
        }

        DepositContext Ctx() => new(_cat, _w, new[] { Home, Away }, Home);

        [Fact]
        public void Stash_sends_each_item_home_and_reports_leftovers()
        {
            var ore = _w.Chest(Home, "ore", capacity: 30).OnlyCategory("ore", 5);
            var misc = _w.Chest(Away, "misc", capacity: 15);
            _w.Put(_bag, IronOre, 40).Put(_bag, Plank, 10);

            var r = PlayerActions.Stash(_w, Ctx(), _bag.Id, _w.Contents(_bag.Id));
            Assert.Equal(30, _w.Count(ore.Id, IronOre));
            Assert.Equal(15, _w.Count(misc.Id, IronOre) + _w.Count(misc.Id, Plank));
            Assert.Equal(5, r.Left.Values.Sum());
            Assert.Equal(2, r.Containers(byDestination: true));
        }

        [Fact]
        public void Pull_takes_worst_home_first_and_keeps_the_last_stack()
        {
            var exact = _w.Chest(Home, "Iron Ore").OnlyItem(IronOre, 5);
            var stray = _w.Chest(Home, "Planks").OnlyItem(Plank);
            _w.Put(exact, IronOre, 300).Put(stray, IronOre, 150);

            var r = PlayerActions.Pull(_w, Ctx(), _bag.Id, IronOre, 250, neverLastStack: true);
            // Stray keeps 100 (one stack), gives 50; exact gives 200 and keeps 100.
            Assert.Equal(100, _w.Count(stray.Id, IronOre));
            Assert.Equal(100, _w.Count(exact.Id, IronOre));
            Assert.Equal(250, _w.Count(_bag.Id, IronOre));
            Assert.Equal(stray.Id, r.Moves[0].From);
        }

        [Fact]
        public void Pull_group_takes_one_stack_of_each()
        {
            var c = _w.Chest(Home, "ore");
            _w.Put(c, IronOre, 500).Put(c, CopperOre, 30);
            var r = PlayerActions.PullGroup(_w, Ctx(), _bag.Id, "ore", null, neverLastStack: false);
            Assert.Equal(100, _w.Count(_bag.Id, IronOre));
            Assert.Equal(30, _w.Count(_bag.Id, CopperOre));
            Assert.Equal(70, r.Left[CopperOre]);
        }

        [Fact]
        public void Tidy_moves_strays_home_but_never_drains_wired_chests()
        {
            var home = _w.Chest(Home, "Iron Ore").OnlyItem(IronOre, 5);
            var general = _w.Chest(Home, "");
            var wired = _w.Chest(Home, "Planks").OnlyItem(Plank);
            var overflow = _w.Chest(Home, "spill");
            overflow.Deposit.Special = SpecialKind.Overflow;
            var skip = _w.Chest(Home, "x");
            skip.Deposit.Special = SpecialKind.Skip;
            _w.Put(general, IronOre, 20).Put(wired, IronOre, 20).Put(overflow, IronOre, 5).Put(skip, IronOre, 7);

            var g = new LogisticsGraph();
            g.For(wired.Id).Output.Add(ListEntry.MassDeposit());

            var r = PlayerActions.Tidy(_w, Ctx(), g);
            Assert.Equal(25, _w.Count(home.Id, IronOre));
            Assert.Equal(20, _w.Count(wired.Id, IronOre));
            Assert.Equal(7, _w.Count(skip.Id, IronOre));
            Assert.Equal(0, _w.Count(overflow.Id, IronOre));
            Assert.Equal(2, r.Containers(byDestination: false));
        }

        [Fact]
        public void Tidy_only_moves_to_strictly_better_homes()
        {
            var a = _w.Chest(Home, "Iron Ore").OnlyItem(IronOre);
            var b = _w.Chest(Home, "Iron Ore").OnlyItem(IronOre);
            _w.Put(a, IronOre, 10).Put(b, IronOre, 10);
            var r = PlayerActions.Tidy(_w, Ctx(), new LogisticsGraph());
            // Both are exact homes; whichever ranks second may move to the first, never back.
            Assert.True(r.Moves.Count <= 1);
            Assert.Equal(20, _w.Count(a.Id, IronOre) + _w.Count(b.Id, IronOre));
        }
    }
}
