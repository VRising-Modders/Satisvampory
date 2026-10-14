// SPDX-License-Identifier: AGPL-3.0-only
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.World;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class SnapshotViewTests
    {
        [Fact]
        public void Reads_each_inventory_once_and_answers_counts_from_memory()
        {
            var w = new FakeWorld();
            var p = new PlotId(1);
            var a = w.Chest(p, "A");
            var b = w.Chest(p, "B");
            w.Put(a, IronOre, 40).Put(b, IronOre, 10).Put(b, Plank, 5);
            var snap = new SnapshotView(w);
            var ctx = new DepositContext(Catalog(), snap, new[] { p }, p);

            for (var i = 0; i < 20; i++)
            {
                Assert.Equal(2, DepositPlanner.ReverseOrder(ctx, IronOre).Count);
                Assert.Single(DepositPlanner.ReverseOrder(ctx, Plank));
            }
            Assert.Equal(2, snap.Reads);
            Assert.Equal(new[] { IronOre, Plank }.Length, snap.ItemsIn(new[] { p }).Count);
        }
    }
}
