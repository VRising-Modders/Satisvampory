// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Loops;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Stations;
using Satisvampory.Engine.World;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class ScopeTests
    {
        static readonly PlotInfo MineA = new(new PlotId(1), Owner: 10, Excluded: false);
        static readonly PlotInfo MineB = new(new PlotId(2), Owner: 10, Excluded: false);
        static readonly PlotInfo Theirs = new(new PlotId(3), Owner: 20, Excluded: false);
        static readonly PlotInfo[] Clan = { MineA, MineB, Theirs };

        static IReadOnlyList<PlotId> Resolve(SharingState s, PlotInfo standing, bool member = true, bool actorIn = true, PlotInfo[]? plots = null, FakeWorld? w = null) =>
            Scope.Resolve(standing, member, actorIn, s, plots ?? Clan, w ?? new FakeWorld());

        [Fact]
        public void Defaults_share_the_whole_clan()
        {
            Assert.Equal(new[] { MineA.Id, MineB.Id, Theirs.Id }, Resolve(new SharingState(), MineA));
        }

        [Fact]
        public void Castle_share_only_joins_the_standing_owners_plots()
        {
            var s = new SharingState { ClanShareAllowed = false };
            Assert.Equal(new[] { MineA.Id, MineB.Id }, Resolve(s, MineA));
            Assert.Equal(new[] { Theirs.Id }, Resolve(s, Theirs));
        }

        [Fact]
        public void Opting_out_of_clan_share_hides_your_plots_and_stops_your_reach()
        {
            var plots = new[] { MineA, MineB, Theirs with { OwnerInClanShare = false } };
            Assert.Equal(new[] { MineA.Id, MineB.Id }, Resolve(new SharingState(), MineA, plots: plots));
            Assert.Equal(new[] { MineA.Id, MineB.Id }, Resolve(new SharingState(), MineA, actorIn: false));
        }

        [Fact]
        public void Locked_opt_out_keeps_sharing_on()
        {
            var s = new SharingState { PlayersMayLeaveClanShare = false };
            Assert.Equal(3, Resolve(s, MineA, actorIn: false).Count);
        }

        [Fact]
        public void Owner_can_turn_off_sharing_between_own_castles()
        {
            var s = new SharingState { ClanShareAllowed = false };
            var a = MineA with { OwnerSharesCastles = false };
            Assert.Equal(new[] { a.Id }, Resolve(s, a, plots: new[] { a, MineB }));
        }

        [Fact]
        public void Guests_excluded_and_raided_plots()
        {
            var s = new SharingState();
            Assert.Equal(new[] { MineA.Id }, Resolve(s, MineA, member: false));
            Assert.Equal(new[] { MineA.Id, Theirs.Id }, Resolve(s, MineA, plots: new[] { MineA, MineB with { Excluded = true }, Theirs }));
            var w = new FakeWorld();
            w.Raided.Add(Theirs.Id);
            Assert.Equal(new[] { MineA.Id, MineB.Id }, Resolve(s, MineA, w: w));
            w.Raided.Add(MineA.Id);
            Assert.Empty(Resolve(s, MineA, w: w));
        }
    }

    public class StationTests
    {
        static Recipe Smelt(bool on = true) =>
            new(1, new[] { new ItemStack(IronOre, 10), new ItemStack(Coal, 1) }, new[] { new ItemStack(IronIngot, 1) }, on);

        static IReadOnlyList<ItemStack> Held(params (ItemId, int)[] s) => s.Select(x => new ItemStack(x.Item1, x.Item2)).ToList();

        [Fact]
        public void Pulls_nothing_unless_one_whole_craft_is_available()
        {
            var plan = StationPlanner.Plan(new[] { Smelt() }, Held(), i => i == IronOre ? 500 : 0);
            Assert.Empty(plan.Pull);
            Assert.Equal(Coal, plan.Missing[1]);
        }

        [Fact]
        public void Pulls_whole_crafts_up_to_the_feed_target()
        {
            var plan = StationPlanner.Plan(new[] { Smelt() }, Held(), i => i == IronOre ? 37 : 100);
            Assert.Equal(3, plan.Crafts[1]);
            Assert.Equal(30, plan.Pull[IronOre]);
            Assert.Equal(3, plan.Pull[Coal]);

            var full = StationPlanner.Plan(new[] { Smelt() }, Held(), _ => 10_000);
            Assert.Equal(StationPlanner.DefaultFeedCrafts, full.Crafts[1]);
        }

        [Fact]
        public void Held_stock_counts_toward_the_craft_and_reduces_the_pull()
        {
            var plan = StationPlanner.Plan(new[] { Smelt() }, Held((IronOre, 8)), i => i == IronOre ? 2 : 1);
            Assert.Equal(1, plan.Crafts[1]);
            Assert.Equal(2, plan.Pull[IronOre]);
        }

        [Fact]
        public void Shared_ingredient_splits_in_whole_crafts()
        {
            var a = new Recipe(1, new[] { new ItemStack(Coal, 2) }, new ItemStack[0], true);
            var b = new Recipe(2, new[] { new ItemStack(Coal, 2), new ItemStack(IronOre, 1) }, new ItemStack[0], true);
            var plan = StationPlanner.Plan(new[] { a, b }, Held(), i => i == Coal ? 9 : 100);
            Assert.Equal(2, plan.Crafts[1]);
            Assert.Equal(2, plan.Crafts[2]);
            Assert.Equal(8, plan.Pull[Coal]);
        }

        [Fact]
        public void Switched_off_recipe_leaves_its_inputs_as_leftovers()
        {
            var plan = StationPlanner.Plan(new[] { Smelt(on: false) }, Held((IronOre, 25), (Coal, 3)), _ => 1000);
            Assert.Empty(plan.Pull);
            Assert.Equal(25, plan.Leftovers[IronOre]);
            Assert.Equal(3, plan.Leftovers[Coal]);
        }

        [Fact]
        public void Partial_inputs_that_cannot_complete_a_craft_are_leftovers()
        {
            var plan = StationPlanner.Plan(new[] { Smelt() }, Held((IronOre, 4)), _ => 0);
            Assert.Equal(4, plan.Leftovers[IronOre]);
        }
    }

    public class LoopTests
    {
        static ContainerId Id() => ContainerId.New();

        [Fact]
        public void Finds_a_ring_and_its_closing_edge()
        {
            var g = new LogisticsGraph();
            var (a, b, c, d) = (Id(), Id(), Id(), Id());
            g.For(a).Output.Add(ListEntry.Of(b, g.NextSequence()));
            g.For(b).Output.Add(ListEntry.Of(c, g.NextSequence()));
            g.For(c).Output.Add(ListEntry.Of(a, g.NextSequence()));
            g.For(c).Output.Add(ListEntry.Of(d, g.NextSequence()));

            var loops = LoopFinder.Find(g.Edges());
            var loop = Assert.Single(loops);
            Assert.Equal(3, loop.Members.Count);
            Assert.DoesNotContain(d, loop.Members);
            Assert.Equal((c, a), (loop.Closing.From, loop.Closing.To));
        }

        [Fact]
        public void Input_and_output_entries_for_the_same_edge_count_once_and_self_loop_is_a_loop()
        {
            var g = new LogisticsGraph();
            var (a, b) = (Id(), Id());
            g.For(a).Output.Add(ListEntry.Of(b));
            g.For(b).Input.Add(ListEntry.Of(a));
            Assert.Single(g.Edges());
            Assert.Empty(LoopFinder.Find(g.Edges()));

            g.For(a).Input.Add(ListEntry.Of(a));
            Assert.Single(LoopFinder.Find(g.Edges()));
        }

        [Fact]
        public void Groups_expand_and_forget_removes_a_destroyed_chest()
        {
            var g = new LogisticsGraph();
            var (a, b, c) = (Id(), Id(), Id());
            g.GroupOrCreate("Ore in").Members.AddRange(new[] { b, c });
            g.For(a).Output.Add(ListEntry.OfGroup("ore IN"));
            Assert.Equal(2, g.Edges().Count);
            Assert.Equal(1, g.Forget(b));
            Assert.Single(g.Edges());
        }
    }
}
