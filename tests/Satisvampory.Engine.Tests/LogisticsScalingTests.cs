// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.World;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class LogisticsScalingTests
    {
        [Fact]
        public void Large_belt_pair_checks_do_not_rebuild_the_entire_belt()
        {
            var graph = new LogisticsGraph();
            var belt = graph.BeltOrCreate("Supply");
            var suppliers = Enumerable.Range(0, 100).Select(_ => ContainerId.New()).ToArray();
            var receivers = Enumerable.Range(0, 40).Select(_ => ContainerId.New()).ToArray();
            BeltOps.AddMembers(graph, belt.Id, suppliers, output: true);
            BeltOps.AddMembers(graph, belt.Id, receivers, output: false);
            var catalog = Catalog();
            graph.Edges(); // Exclude first-use/JIT allocation from the measured path.
            var before = GC.GetAllocatedBytesForCurrentThread();
            var edges = graph.Edges();
            var edgeBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(4000, edges.Count);
            Assert.True(edgeBytes < 25_000_000, $"Edge expansion allocated {edgeBytes:N0} bytes");
            var entry = graph.For(receivers[0]).Input[0];
            before = GC.GetAllocatedBytesForCurrentThread();
            int allowed = 0;
            for (int repeat = 0; repeat < 100; repeat++)
                foreach (var supplier in suppliers)
                    if (graph.Allows(entry, receivers[0], false, supplier, IronOre, catalog)) allowed++;
            var checkBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(10000, allowed);
            Assert.True(checkBytes < 5_000_000, $"Pair checks allocated {checkBytes:N0} bytes");
            graph.For(suppliers[0]).Output[0].Filter.ExcludeItems.Add(IronOre);
            Assert.False(graph.Allows(entry, receivers[0], false, suppliers[0], IronOre, catalog));
        }

        [Fact]
        public void Tick_snapshots_refresh_after_moves_destruction_and_next_pass()
        {
            var live = new FakeWorld();
            var plot = new PlotId(1);
            var a = live.Chest(plot, "A", capacity: 100);
            var b = live.Chest(plot, "B", capacity: 100);
            live.Put(a, IronOre, 40, InvPart.Output);
            var tick = new TickWorld(live);
            Assert.Equal(40, tick.Count(a.Id, IronOre, InvPart.Output));
            Assert.Equal(0, tick.Count(b.Id, IronOre));
            Assert.Equal(100, tick.FreeSpace(b.Id, Coal));
            Assert.Equal(15, tick.Transfer(a.Id, InvPart.Output, b.Id, IronOre, 15));
            Assert.Equal(25, tick.Count(a.Id, IronOre, InvPart.Output));
            Assert.Equal(15, tick.Count(b.Id, IronOre));
            Assert.Equal(85, tick.FreeSpace(b.Id, Coal));
            Assert.Equal(5, tick.Destroy(b.Id, IronOre, 5));
            Assert.Equal(10, tick.Count(b.Id, IronOre));
            live.Put(b, IronOre, 3);
            Assert.Equal(10, tick.Count(b.Id, IronOre));
            tick.Clear();
            Assert.Equal(13, tick.Count(b.Id, IronOre));
        }
    }
}
