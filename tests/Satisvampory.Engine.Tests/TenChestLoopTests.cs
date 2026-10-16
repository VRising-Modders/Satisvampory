// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Loops;
using Satisvampory.Engine.Model;
using Xunit;
namespace Satisvampory.Engine.Tests;
public class TenChestLoopTests
{
    [Fact]
    public void Loaded_ring_conserves_items_bounds_moves_and_sleeps_after_one_cycle()
    {
        var world = new FakeWorld();
        var plot = new PlotId(1);
        var graph = new LogisticsGraph();
        var chests = Enumerable.Range(0, 10).Select(i => world.Chest(plot, "Loop " + i, capacity: 14000)).ToArray();
        for (int i = 0; i < chests.Length; i++)
        {
            graph.For(chests[i].Id).Output.Add(ListEntry.Of(chests[(i + 1) % 10].Id, graph.NextSequence()));
            world.Put(chests[i], TestItems.Lumber, 5000).Put(chests[i], TestItems.Plank, 5500);
        }
        var context = new DepositContext(TestItems.Catalog(), world, new[] { plot }, plot);
        var state = new TickState();
        TickReport Tick(int t) => new ConveyorTick(world, context, graph, new CastleRules(), state).Run(t * 5);
        int moved = 0;
        for (int t = 0; t < 20; t++)
        {
            var report = Tick(t);
            Assert.Single(report.Loops);
            Assert.Equal(10, report.Loops[0].Members.Count);
            Assert.InRange(report.Moves.Count, 0, 12);
            Assert.Equal(50000, chests.Sum(c => world.Count(c.Id, TestItems.Lumber)));
            Assert.Equal(55000, chests.Sum(c => world.Count(c.Id, TestItems.Plank)));
            if (t >= 10) Assert.Empty(report.Moves);
            moved += report.Moves.Count;
        }
        Assert.True(moved > 0);
        Assert.Equal(0, state.Loops.Values.Single().PassesLeft);
        world.Put(chests[0], TestItems.Lumber, 1);
        Assert.NotEmpty(Tick(20).Moves);
    }
    [Fact]
    public void Even_distribution_moves_surplus_through_a_ring_without_a_neighbor_gradient_deadlock()
    {
        var world = new FakeWorld(); var plot = new PlotId(1); var graph = new LogisticsGraph();
        var chests = Enumerable.Range(0, 10).Select(i => world.Chest(plot, "Loop " + i, capacity: 14000)).ToArray();
        var catalog = TestItems.Catalog(); catalog.AddItem(TestItems.Lumber, "Wood", 1000);
        catalog.AddItem(TestItems.Plank, "Stone", 1000);
        var wood = new[] { 6000, 0, 8000, 0, 6000, 8000, 2000, 13000, 6000, 1000 };
        var stone = new[] { 8000, 0, 6000, 6000, 6000, 5500, 5500, 500, 8000, 9500 };
        for (int i = 0; i < 10; i++)
        {
            graph.For(chests[i].Id).Output.Add(ListEntry.Of(chests[(i + 1) % 10].Id, graph.NextSequence()));
            world.Put(chests[i], TestItems.Lumber, wood[i]).Put(chests[i], TestItems.Plank, stone[i]);
        }
        var context = new DepositContext(catalog, world, new[] { plot }, plot);
        var state = new TickState(); var loop = LoopFinder.Find(graph.Edges()).Single();
        state.Loops[loop.Key] = new LoopState { Mode = LoopMode.EvenDistribution };
        for (int t = 0; t < 12; t++)
        {
            var report = new ConveyorTick(world, context, graph, new CastleRules(), state).Run(t * 5);
            Assert.InRange(report.Moves.Count, 0, 12);
            Assert.Equal(50000, chests.Sum(c => world.Count(c.Id, TestItems.Lumber)));
            Assert.Equal(55000, chests.Sum(c => world.Count(c.Id, TestItems.Plank)));
            Assert.All(report.Moves, m => Assert.Contains(graph.Edges(), e => e.From == m.From && e.To == m.To));
        }
        Assert.True(LoopController.IsBalanced(loop, world, stackSize: catalog.MaxStack));
        Assert.False(state.Loops[loop.Key].Stalled);
        Assert.Empty(new ConveyorTick(world, context, graph, new CastleRules(), state).Run(60).Moves);
    }
    [Fact]
    public void Changing_loop_mode_wakes_a_sleeping_loop_without_an_inventory_edit()
    {
        var world = new FakeWorld(); var plot = new PlotId(1); var graph = new LogisticsGraph();
        var a = world.Chest(plot, "A"); var b = world.Chest(plot, "B");
        graph.For(a.Id).Output.Add(ListEntry.Of(b.Id)); graph.For(b.Id).Output.Add(ListEntry.Of(a.Id));
        world.Put(a, TestItems.Lumber, 900);
        var loop = LoopFinder.Find(graph.Edges()).Single(); var state = new LoopState();
        LoopController.Decide(loop, state, world, 0, true);
        LoopController.Decide(loop, state, world, 5, true);
        Assert.Equal(LoopDecision.Sleep, LoopController.Decide(loop, state, world, 120, true));
        state.Mode = LoopMode.EvenDistribution;
        Assert.Equal(LoopDecision.Balance, LoopController.Decide(loop, state, world, 125, true));
        Assert.False(state.Stalled);
    }
}
