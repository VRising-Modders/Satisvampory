// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Persistence;
using Satisvampory.Engine.Stations;
using Xunit;
using Xunit.Abstractions;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class DefaultBeltScaleTests
    {
        readonly ITestOutputHelper _out;
        public DefaultBeltScaleTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void A_full_default_belt_tick_stays_cheap()
        {
            // The shape of a real imported island: ~220 chests on the Default Belt, 40 stations drawing from it.
            var world = new FakeWorld();
            var plot = new PlotId(1);
            var state = new IslandState();
            var catalog = Catalog();
            for (int i = 0; i < 220; i++)
            {
                var c = world.Chest(plot, "Chest " + i);
                if (i % 3 == 0) world.Put(c, IronOre, 200);
                if (i % 5 == 0) world.Put(c, Coal, 100);
            }
            for (int i = 0; i < 40; i++)
            {
                var s = world.Chest(plot, "Furnace " + i, kind: NodeKind.Station);
                s.Role = StationRole.Refinement;
                world.StationRecipes[s.Id] = new List<Recipe>
                {
                    new(1, new[] { new ItemStack(IronOre, 10), new ItemStack(Coal, 1) }, new[] { new ItemStack(IronIngot, 1) }, true),
                };
            }
            DefaultBelt.Initialize(state.Graph, world.ContainersOn(plot));
            var ctx = new DepositContext(catalog, world, new[] { plot }, plot);
            new ConveyorTick(world, ctx, state.Graph, new CastleRules { DefaultReserve = 0 }, new TickState()).Run(0); // warm-up / JIT

            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            var report = new ConveyorTick(world, ctx, state.Graph, new CastleRules { DefaultReserve = 0 }, new TickState()).Run(5);
            watch.Stop();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            _out.WriteLine($"tick {watch.Elapsed.TotalMilliseconds:0} ms, {bytes / 1_000_000.0:0.0} MB, {report.Moves.Count} moves");
            // Was 1.3 s and 277 MB before the per-tick caches (a 1.9 s server hitch live). Loose bounds:
            // this guards against that regression, not against a slow CI machine.
            Assert.True(watch.Elapsed.TotalMilliseconds < 750, $"tick took {watch.Elapsed.TotalMilliseconds:0} ms");
            Assert.True(bytes < 80_000_000, $"tick allocated {bytes:N0} bytes");
        }
    }
}
