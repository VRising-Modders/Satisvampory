// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Persistence;
using Satisvampory.Engine.Stations;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class FilterMigrationTests
    {
        readonly FakeWorld _w = new();
        readonly LogisticsGraph _g = new();
        readonly Dictionary<ContainerId, DepositOptions> _deposit = new();
        static readonly PlotId Home = new(1);

        [Fact]
        public void Link_filters_become_the_receivers_accepts_and_sender_side_filters_are_reported()
        {
            var ore = _w.Chest(Home, "Ore");
            var furnace = _w.Chest(Home, "Furnace", kind: NodeKind.Station);
            var bin = _w.Chest(Home, "Bin");
            var input = ListEntry.Of(ore.Id, _g.NextSequence());
            input.Filter.Items.Add(IronOre);
            _g.For(furnace.Id).Input.Add(input);
            var output = ListEntry.Of(bin.Id, _g.NextSequence());
            output.Filter.ExcludeItems.Add(Coal);
            _g.For(ore.Id).Output.Add(output);
            var toBelt = ListEntry.OfBelt(DefaultBelt.Id, _g.NextSequence());
            toBelt.Filter.Items.Add(Plank);
            DefaultBelt.Initialize(_g, new ContainerInfo[0]);
            _g.For(bin.Id).Output.Add(toBelt);

            var r = FilterMigration.MoveToAccepts(_g, _deposit, id => _w.Find(id)?.DisplayName ?? "?", i => i.Guid.ToString());

            Assert.Equal(Takes.OnlyListed, _deposit[furnace.Id].Takes);
            Assert.True(_deposit[furnace.Id].ItemPriority.ContainsKey(IronOre));
            Assert.Contains(Coal, _deposit[bin.Id].BannedItems);
            Assert.Equal(Takes.Anything, _deposit[bin.Id].Takes);
            Assert.Single(r.Dropped);
            Assert.All(_g.Lists.Values.SelectMany(l => l.Input.Concat(l.Output)), e => Assert.True(e.Filter.IsAnything));
            Assert.True(SaveCodec.Restore(new IslandSave { Version = 6 }, out _).FiltersDue);
        }

        [Fact]
        public void A_station_is_fed_only_what_its_accepts_allow()
        {
            var supply = _w.Chest(Home, "Supplies");
            var furnace = _w.Chest(Home, "Furnace", kind: NodeKind.Station);
            furnace.Role = StationRole.Refinement;
            _w.StationRecipes[furnace.Id] = new List<Recipe> { new(1, new[] { new ItemStack(IronOre, 10) }, new[] { new ItemStack(IronIngot, 1) }, true) };
            _w.Put(supply, IronOre, 100);
            _g.For(furnace.Id).Input.Add(ListEntry.Of(supply.Id, _g.NextSequence()));
            furnace.Deposit.BannedItems.Add(IronOre);
            Tick();
            Assert.Equal(0, _w.Count(furnace.Id, IronOre));
            furnace.Deposit.BannedItems.Clear();
            // "Only listed" with nothing listed (imported stations) does not starve it.
            furnace.Deposit.Takes = Takes.OnlyListed;
            Tick();
            Assert.True(_w.Count(furnace.Id, IronOre) > 0);
        }

        void Tick() => new ConveyorTick(_w, new DepositContext(Catalog(), _w, new[] { Home }, Home), _g,
            new CastleRules { DefaultReserve = 0 }, new TickState()).Run(0);
    }
}
