// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Stations;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests;

public class DirectionalConveyorTests
{
    readonly FakeWorld world = new();
    readonly LogisticsGraph graph = new();
    readonly PlotId plot = new(1);
    readonly ItemCatalog catalog = Catalog();
    readonly ContainerInfo source;
    readonly ContainerInfo station;
    readonly ListEntry supply;
    readonly ListEntry receive;

    public DirectionalConveyorTests()
    {
        source = world.Chest(plot, "Supply");
        station = world.Chest(plot, "Station", kind: NodeKind.Station);
        station.Role = StationRole.Refinement;
        var belt = graph.BeltOrCreate("Resources");
        supply = ListEntry.OfBelt(belt.Id);
        receive = ListEntry.OfBelt(belt.Id);
        graph.For(source.Id).Output.Add(supply);
        graph.For(station.Id).Input.Add(receive);
        world.Put(source, Coal, 30);
        world.StationRecipes[station.Id] = new List<Recipe>
        {
            new(1, new[] { new ItemStack(Coal, 1) }, new[] { new ItemStack(IronIngot, 1) }, true)
        };
    }

    DepositContext Context() => new(catalog, world, new[] { plot }, plot);
    TickReport Tick() => new ConveyorTick(world, Context(), graph,
        new CastleRules { DefaultReserve = 0 }, new TickState(), new TickOptions { FeedCrafts = 1 }).Run(0);

    [Theory]
    [InlineData(StationRole.Refinement, true)]
    [InlineData(StationRole.Refinement, false)]
    [InlineData(StationRole.Brazier, true)]
    [InlineData(StationRole.Brazier, false)]
    public void Either_connection_can_refuse_station_feed(StationRole role, bool sourceRefuses)
    {
        station.Role = role;
        (sourceRefuses ? supply : receive).Filter.ExcludeItems.Add(Coal);
        Assert.Empty(Tick().Moves);
        Assert.Equal(30, world.Count(source.Id, Coal));
        Assert.Equal(0, world.Count(station.Id, Coal));
    }

    [Theory]
    [InlineData(StationRole.Refinement)]
    [InlineData(StationRole.Brazier)]
    public void Matching_connections_feed_only_from_suppliers(StationRole role)
    {
        station.Role = role;
        supply.Filter.Items.Add(Coal);
        receive.Filter.Items.Add(Coal);
        var receiverOnly = world.Chest(plot, "Receiver only");
        graph.For(receiverOnly.Id).Input.Add(ListEntry.OfBelt(receive.Belt));
        world.Put(receiverOnly, Coal, 100);
        var report = Tick();
        Assert.Contains(report.Moves, m => m.From == source.Id && m.To == station.Id);
        Assert.DoesNotContain(report.Moves, m => m.From == receiverOnly.Id);
        Assert.True(world.Count(station.Id, Coal) > 0);
        Assert.True(world.Count(receiverOnly.Id, Coal) >= 100);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Heart_sources_check_both_connection_filters(bool sourceRefuses)
    {
        (sourceRefuses ? supply : receive).Filter.ExcludeItems.Add(Coal);
        Assert.Empty(ConveyorTick.HeartSources(world, Context(), graph, station.Id, Coal));
        (sourceRefuses ? supply : receive).Filter.ExcludeItems.Clear();
        Assert.Equal(new[] { source.Id }, ConveyorTick.HeartSources(world, Context(), graph, station.Id, Coal));
    }

    [Fact]
    public void Filtered_mass_input_does_not_bypass_a_denied_belt_connection()
    {
        supply.Filter.ExcludeItems.Add(Coal);
        var mass = ListEntry.MassDeposit();
        mass.Filter.Items.Add(Ruby);
        graph.For(station.Id).Input.Add(mass);
        Assert.Empty(Tick().Moves);
        Assert.Empty(ConveyorTick.HeartSources(world, Context(), graph, station.Id, Coal));
    }

    [Fact]
    public void Migration_peer_restrictions_are_respected()
    {
        graph.For(source.Id).Output.Clear();
        graph.For(source.Id).Output.Add(new ListEntry
        {
            Kind = EntryKind.Belt, Belt = supply.Belt, BeltPeers = new HashSet<ContainerId>()
        });
        Assert.Empty(Tick().Moves);
        Assert.Empty(ConveyorTick.HeartSources(world, Context(), graph, station.Id, Coal));
    }
}
