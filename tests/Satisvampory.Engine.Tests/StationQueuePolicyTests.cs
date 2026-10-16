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

public class StationQueuePolicyTests
{
    readonly FakeWorld world = new();
    readonly LogisticsGraph graph = new();
    readonly TickState state = new();
    readonly PlotId plot = new(1);
    readonly ContainerInfo bench;
    readonly ContainerInfo home;

    public StationQueuePolicyTests()
    {
        bench = world.Chest(plot, "Bench", kind: NodeKind.Station);
        bench.Role = StationRole.Workstation;
        home = world.Chest(plot, "Storage");
        graph.For(bench.Id).Input.Add(ListEntry.Of(home.Id));
        world.StationRecipes[bench.Id] = new List<Recipe>
        {
            new(1, new[] { new ItemStack(IronOre, 10) }, new[] { new ItemStack(IronIngot, 1) }, true)
        };
    }

    TickReport Tick(double now) => new ConveyorTick(world,
        new DepositContext(Catalog(), world, new[] { plot }, plot), graph,
        new CastleRules { DefaultReserve = 0 }, state,
        new TickOptions { LeftoverGraceSeconds = 10 }).Run(now);

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 0, true)]
    [InlineData(true, -1, false)]
    public void Unknown_queued_active_or_invalid_reads_protect_all_items(bool known, int count, bool active)
    {
        world.InputStates[bench.Id] = new(known, count, active);
        world.Put(bench, IronOre, 5).Put(bench, Ruby, 7);
        graph.For(bench.Id).Output.Add(ListEntry.Of(home.Id));
        graph.For(bench.Id).Output.Add(ListEntry.MassDeposit());
        Tick(0);
        Assert.Empty(Tick(100).Moves);
        Assert.Equal(5, world.Count(bench.Id, IronOre));
        Assert.Equal(7, world.Count(bench.Id, Ruby));
        Assert.Empty(state.LeftoverSince);
    }

    [Fact]
    public void Known_idle_returns_all_items_only_after_grace_even_with_recipe_enabled()
    {
        world.InputStates[bench.Id] = StationInputState.Idle;
        world.Put(bench, IronOre, 15);
        graph.For(bench.Id).Output.Add(ListEntry.MassDeposit());
        Assert.Empty(Tick(0).Moves);
        Assert.Empty(Tick(9).Moves);
        Assert.Single(Tick(10).Moves);
        Assert.Equal(15, world.Count(home.Id, IronOre));
        Assert.Equal(0, world.Count(bench.Id, IronOre));
    }

    [Fact]
    public void Queue_interrupts_grace_and_restarts_it_when_idle_again()
    {
        world.Put(bench, IronOre, 15);
        world.InputStates[bench.Id] = StationInputState.Idle;
        Tick(0);
        world.InputStates[bench.Id] = new(true, 1, false);
        Tick(9);
        world.InputStates[bench.Id] = StationInputState.Idle;
        Assert.Empty(Tick(20).Moves);
        Assert.Empty(Tick(29).Moves);
        Assert.Single(Tick(30).Moves);
    }

    [Fact]
    public void Workstation_never_prefills_from_inputs_outputs_or_reverse_mass_deposit()
    {
        world.InputStates[bench.Id] = StationInputState.Idle;
        world.Put(home, IronOre, 100);
        graph.For(home.Id).Output.Add(ListEntry.Of(bench.Id));
        graph.For(bench.Id).Input.Add(ListEntry.MassDeposit());
        Assert.Empty(Tick(0).Moves);
        Assert.Empty(Tick(100).Moves);
        Assert.Equal(100, world.Count(home.Id, IronOre));
        Assert.Equal(0, world.Count(bench.Id, IronOre));
    }

    [Fact]
    public void Active_refinement_retains_inputs_but_releases_separate_output()
    {
        bench.Role = StationRole.Refinement;
        world.InputStates[bench.Id] = new(true, 0, true);
        world.StationRecipes[bench.Id].Clear();
        world.Put(bench, IronOre, 15).Put(bench, IronIngot, 2, InvPart.Output);
        graph.For(bench.Id).Output.Add(ListEntry.Of(home.Id));
        Tick(0);
        Tick(100);
        Assert.Equal(15, world.Count(bench.Id, IronOre));
        Assert.Equal(2, world.Count(home.Id, IronIngot));
        world.InputStates[bench.Id] = StationInputState.Idle;
        Assert.Empty(Tick(200).Moves);
        Tick(210);
        Assert.Equal(15, world.Count(home.Id, IronOre));
    }

    [Fact]
    public void Loop_balancing_cannot_prefill_or_extract_workstation_inputs()
    {
        world.InputStates[bench.Id] = new(true, 1, true);
        world.Put(bench, IronOre, 100);
        world.Put(home, Ruby, 100);
        graph.For(bench.Id).Output.Add(ListEntry.Of(home.Id, graph.NextSequence()));
        graph.For(home.Id).Output.Add(ListEntry.Of(bench.Id, graph.NextSequence()));
        Tick(0);
        Assert.Single(state.Loops);
        state.Loops.Values.Single().Mode = LoopMode.EvenDistribution;
        Assert.Empty(Tick(100).Moves);
        Assert.Equal(100, world.Count(bench.Id, IronOre));
        Assert.Equal(100, world.Count(home.Id, Ruby));
    }

    [Fact]
    public void Idle_bench_sends_sword_to_output_and_returns_ingredients_to_input_home()
    {
        var output = world.Chest(plot, "Weapons");
        world.StationRecipes[bench.Id] = new List<Recipe>
        {
            new(2, new[] { new ItemStack(IronOre, 10) }, new[] { new ItemStack(Sword, 1) }, false)
        };
        world.InputStates[bench.Id] = StationInputState.Idle;
        graph.For(bench.Id).Output.Add(ListEntry.Of(output.Id));
        world.Put(bench, Sword, 1).Put(bench, IronOre, 7);
        Assert.Single(Tick(0).Moves);
        Assert.Equal(1, world.Count(output.Id, Sword));
        Assert.Equal(0, world.Count(home.Id, Sword));
        Assert.Equal(7, world.Count(bench.Id, IronOre));
        Tick(10);
        Assert.Equal(7, world.Count(home.Id, IronOre));
        Assert.Equal(0, world.Count(output.Id, IronOre));
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 0, true)]
    public void Bench_products_stay_put_until_queue_is_known_empty_and_inactive(bool known, int count, bool active)
    {
        world.Put(bench, IronIngot, 3);
        graph.For(bench.Id).Output.Add(ListEntry.MassDeposit());
        world.InputStates[bench.Id] = new(known, count, active);
        Assert.Empty(Tick(0).Moves);
        Assert.Empty(Tick(100).Moves);
        world.InputStates[bench.Id] = StationInputState.Idle;
        Assert.Single(Tick(101).Moves);
        Assert.Equal(3, world.Count(home.Id, IronIngot));
    }

    [Fact]
    public void Output_filter_cannot_be_bypassed_by_treating_products_as_leftovers()
    {
        world.Put(bench, IronIngot, 3);
        world.InputStates[bench.Id] = StationInputState.Idle;
        var output = ListEntry.MassDeposit();
        output.Filter.ExcludeItems.Add(IronIngot);
        graph.For(bench.Id).Output.Add(output);
        Assert.Empty(Tick(0).Moves);
        Assert.Empty(Tick(100).Moves);
        Assert.Equal(3, world.Count(bench.Id, IronIngot));
    }

    [Fact]
    public void Empty_recipe_read_retains_unclassified_inventory()
    {
        world.StationRecipes[bench.Id].Clear();
        world.Put(bench, Sword, 1).Put(bench, IronOre, 7);
        world.InputStates[bench.Id] = StationInputState.Idle;
        graph.For(bench.Id).Output.Add(ListEntry.MassDeposit());
        Assert.Empty(Tick(0).Moves);
        Assert.Empty(Tick(100).Moves);
    }
}
