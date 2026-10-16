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

namespace Satisvampory.Engine.Tests;

public class NativeRoutingCoexistenceTests
{
    readonly FakeWorld world = new();
    readonly LogisticsGraph graph = new();
    readonly TickState state = new();
    readonly PlotId plot = new(1);
    readonly ContainerInfo station;
    readonly ContainerInfo chest;

    public NativeRoutingCoexistenceTests()
    {
        station = world.Chest(plot, "Native station", kind: NodeKind.Station);
        station.Role = StationRole.Refinement;
        chest = world.Chest(plot, "Storage");
        world.InputStates[station.Id] = StationInputState.Idle;
        graph.For(station.Id).Input.Add(ListEntry.Of(chest.Id));
    }

    TickReport Tick(double now) => new ConveyorTick(world,
        new DepositContext(Catalog(), world, new[] { plot }, plot), graph,
        new CastleRules { DefaultReserve = 0 }, state,
        new TickOptions { LeftoverGraceSeconds = 10, FeedCrafts = 1 }).Run(now);

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Native_owned_or_unknown_stations_do_not_feed_return_or_send_outputs(bool known, bool enabled)
    {
        station.NativeRoutingKnown = known;
        station.NativeAutoRouteItems = enabled;
        world.Put(chest, Coal, 30).Put(station, Ruby, 5).Put(station, IronIngot, 2, InvPart.Output);
        world.StationRecipes[station.Id] = new List<Recipe>
        {
            new(1, new[] { new ItemStack(Coal, 1) }, new[] { new ItemStack(IronIngot, 1) }, true)
        };
        graph.For(station.Id).Output.Add(ListEntry.Of(chest.Id));
        graph.For(station.Id).Output.Add(ListEntry.MassDeposit());
        Assert.Empty(Tick(0).Moves);
        var report = Tick(100);
        Assert.Empty(report.Moves);
        Assert.Contains(known ? "native-routing" : "native-routing-unknown", report.Diagnostics[station.Id]);
        Assert.Equal(30, world.Count(chest.Id, Coal));
        Assert.Equal(5, world.Count(station.Id, Ruby));
        Assert.Equal(2, world.Count(station.Id, IronIngot, InvPart.Output));
    }

    [Fact]
    public void Native_owned_station_cannot_supply_another_station_or_balance_a_loop()
    {
        station.NativeAutoRouteItems = true;
        world.Put(station, Coal, 30, InvPart.Output).Put(station, Ruby, 100).Put(chest, IronOre, 100);
        var other = world.Chest(plot, "Other station", kind: NodeKind.Station);
        other.Role = StationRole.Refinement;
        world.StationRecipes[other.Id] = new List<Recipe>
        {
            new(1, new[] { new ItemStack(Coal, 1) }, new[] { new ItemStack(IronIngot, 1) }, true)
        };
        graph.For(other.Id).Input.Add(ListEntry.Of(station.Id));
        graph.For(station.Id).Output.Add(ListEntry.Of(chest.Id, graph.NextSequence()));
        graph.For(chest.Id).Output.Add(ListEntry.Of(station.Id, graph.NextSequence()));
        Assert.Empty(Tick(0).Moves);
        foreach (var loop in state.Loops.Values) loop.Mode = LoopMode.EvenDistribution;
        Assert.NotEmpty(state.Loops);
        Assert.Empty(Tick(100).Moves);
        Assert.Equal(0, world.Count(other.Id, Coal));
    }

    [Fact]
    public void Disabling_native_routing_resumes_with_a_fresh_leftover_grace_period()
    {
        world.Put(station, Ruby, 5);
        Tick(0);
        Assert.Single(state.LeftoverSince);
        station.NativeAutoRouteItems = true;
        Assert.Empty(Tick(9).Moves);
        Assert.Empty(state.LeftoverSince);
        station.NativeAutoRouteItems = false;
        Assert.Empty(Tick(20).Moves);
        Assert.Empty(Tick(29).Moves);
        Assert.Single(Tick(30).Moves);
        Assert.Equal(5, world.Count(chest.Id, Ruby));
    }

    [Fact]
    public void Uninitialized_station_ownership_is_fail_closed_but_chests_are_unaffected()
    {
        Assert.True(new ContainerInfo(ContainerId.New(), plot, NodeKind.Station, "Station").AutomationSuspended);
        Assert.False(new ContainerInfo(ContainerId.New(), plot, NodeKind.Chest, "Chest").AutomationSuspended);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Heart_feed_excludes_native_owned_and_unknown_stations(bool known, bool enabled)
    {
        station.NativeRoutingKnown = known;
        station.NativeAutoRouteItems = enabled;
        world.Put(station, BloodEssence, 50).Put(chest, BloodEssence, 20);
        var heart = world.Chest(plot, "Heart", kind: NodeKind.Heart);
        graph.For(heart.Id).Input.Add(ListEntry.Of(station.Id));
        graph.For(heart.Id).Input.Add(ListEntry.Of(chest.Id));
        var ctx = new DepositContext(Catalog(), world, new[] { plot }, plot);
        Assert.Equal(new[] { chest.Id }, ConveyorTick.HeartSources(world, ctx, graph, heart.Id, BloodEssence));
        station.NativeRoutingKnown = true;
        station.NativeAutoRouteItems = false;
        Assert.Equal(new[] { station.Id, chest.Id }, ConveyorTick.HeartSources(world, ctx, graph, heart.Id, BloodEssence));
    }
}
