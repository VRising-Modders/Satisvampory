// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Persistence;
using Satisvampory.Engine.Stations;
using Satisvampory.Engine.World;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class DefaultBeltTests
    {
        readonly FakeWorld world = new();
        readonly IslandState state = new();
        readonly PlotId home = new(1);
        readonly PlotId away = new(2);
        readonly ItemCatalog catalog = Catalog();

        void Initialize() => DefaultBelt.Initialize(state.Graph, world.ContainersOn(home).Concat(world.ContainersOn(away)));
        TickReport Tick(params PlotId[] scope) => new ConveyorTick(world,
            new DepositContext(catalog, world, scope.Length == 0 ? new[] { home, away } : scope, home),
            state.Graph, new CastleRules { DefaultReserve = 0 }, new TickState()).Run(0);

        ContainerInfo Furnace(bool enabled = true)
        {
            var station = world.Chest(home, "Furnace", kind: NodeKind.Station);
            station.Role = StationRole.Refinement;
            world.StationRecipes[station.Id] = new List<Recipe>
            {
                new(1, new[] { new ItemStack(IronOre, 10), new ItemStack(Coal, 1) },
                    new[] { new ItemStack(IronIngot, 1) }, enabled),
            };
            return station;
        }

        [Fact]
        public void Stations_send_nothing_out_until_the_player_sets_an_output()
        {
            var station = Furnace();
            Initialize();
            Assert.Single(state.Graph.For(station.Id).Input);
            Assert.Empty(state.Graph.For(station.Id).Output);
        }

        [Fact]
        public void Version_4_removes_only_untouched_default_station_outputs()
        {
            var untouched = Furnace();
            var edited = world.Chest(home, "Smithy", kind: NodeKind.Station);
            edited.Role = StationRole.Workstation;
            foreach (var s in new[] { untouched, edited })
            {
                var l = state.Graph.For(s.Id);
                l.Input.Add(ListEntry.OfBelt(DefaultBelt.Id, state.Graph.NextSequence()));
                l.Output.Add(ListEntry.MassDeposit(state.Graph.NextSequence()));
            }
            var target = world.Chest(home, "Ingots");
            state.Graph.For(edited.Id).Output.Add(new ListEntry { Kind = EntryKind.Container, Container = target.Id, Sequence = state.Graph.NextSequence() });

            var changed = DefaultBelt.RemoveDefaultStationOutputs(state.Graph, world.ContainersOn(home));
            Assert.Equal(new[] { untouched.Id }, changed);
            Assert.Empty(state.Graph.For(untouched.Id).Output);
            Assert.Equal(2, state.Graph.For(edited.Id).Output.Count);
            Assert.True(SaveCodec.Restore(new IslandSave { Version = 3 }, out _).DefaultOutputsDue);
        }

        [Fact]
        public void Containers_from_before_the_default_belt_join_it_once()
        {
            // Lists that existed before the Default Belt: a chest with only a deposit setting, a
            // station with no Input, a spoils chest. Initialize leaves every one of them alone.
            var old = world.Chest(home, "Old ore");
            state.Graph.For(old.Id).KeepEmpty = true;
            var spoils = world.Chest(home, "Spoils");
            spoils.Deposit.Special = SpecialKind.Spoils;
            state.Graph.For(spoils.Id).KeepEmpty = true;
            var station = Furnace();
            state.Graph.For(station.Id).KeepEmpty = true;
            Initialize();
            var reverse = new[] { old.Id };
            Assert.Empty(MaterialPolicy.ForInput(state.Graph, station.Id, reverse, IronOre, catalog));

            var all = world.ContainersOn(home).ToList();
            var changed = DefaultBelt.MigrateExisting(state.Graph, all);
            Assert.Equal(new[] { old.Id, station.Id }.OrderBy(i => i.Value), changed.OrderBy(i => i.Value));
            Assert.Contains(old.Id, DefaultBelt.Suppliers(state.Graph));
            Assert.DoesNotContain(spoils.Id, DefaultBelt.Suppliers(state.Graph));
            Assert.Equal(reverse, MaterialPolicy.ForInput(state.Graph, station.Id, reverse, IronOre, catalog));

            // Running it again changes nothing.
            Assert.Empty(DefaultBelt.MigrateExisting(state.Graph, all));
        }

        [Fact]
        public void Only_saves_older_than_version_3_are_due_for_the_migration()
        {
            Assert.True(SaveCodec.Restore(new IslandSave { Version = 2 }, out _).DefaultBeltMigrationDue);
            Assert.False(SaveCodec.Restore(new IslandSave { Version = IslandSave.CurrentVersion }, out _).DefaultBeltMigrationDue);
        }

        [Fact]
        public void Suppliers_do_not_empty_into_each_other()
        {
            var a = world.Chest(home, "A");
            var b = world.Chest(home, "B");
            world.Put(a, IronOre, 50);
            Initialize();
            Assert.Empty(state.Graph.Edges());
            Assert.Empty(Tick().Moves);
            Assert.Equal(50, world.Count(a.Id, IronOre));
            Assert.Equal(0, world.Count(b.Id, IronOre));
        }

        [Theory]
        [InlineData(true, 27, 20)]
        [InlineData(true, 9, 0)]
        [InlineData(false, 27, 0)]
        public void Default_belt_supplies_only_complete_enabled_crafts(bool enabled, int ore, int expected)
        {
            var supply = world.Chest(away, "Supplies");
            var station = Furnace(enabled);
            world.Put(supply, IronOre, ore).Put(supply, Coal, 50);
            Initialize();
            // The player sets the Default Belt as the furnace's Input.
            state.Graph.For(station.Id).Input[0] = ListEntry.OfBelt(DefaultBelt.Id, state.Graph.NextSequence());
            Tick();
            Assert.Equal(expected, world.Count(station.Id, IronOre));
            Assert.Equal(expected / 10, world.Count(station.Id, Coal));
        }

        [Fact]
        public void The_automatic_input_feeds_a_factory_for_its_enabled_recipes()
        {
            var supply = world.Chest(away, "Supplies");
            var station = Furnace();
            world.Put(supply, IronOre, 100).Put(supply, Coal, 50);
            Initialize();
            Assert.True(Assert.Single(state.Graph.For(station.Id).Input).Auto);
            Tick();
            Assert.True(world.Count(station.Id, IronOre) > 0);
        }

        [Fact]
        public void Version_5_marks_automatic_inputs_and_empties_factories_they_fed()
        {
            var supply = world.Chest(home, "Supplies");
            var press = Furnace();
            var tomb = world.Chest(home, "Tomb", kind: NodeKind.Station); tomb.Role = StationRole.UnitSpawner;
            var bench = world.Chest(home, "Bench", kind: NodeKind.Station); bench.Role = StationRole.Workstation;
            var filtered = world.Chest(home, "Filtered", kind: NodeKind.Station); filtered.Role = StationRole.Refinement;
            foreach (var c in new[] { press, bench, filtered })
                state.Graph.For(c.Id).Input.Add(ListEntry.OfBelt(DefaultBelt.Id, state.Graph.NextSequence()));
            state.Graph.For(filtered.Id).Input[0].Filter.Items.Add(Coal);
            state.Graph.For(supply.Id).Output.Add(ListEntry.OfBelt(DefaultBelt.Id, state.Graph.NextSequence()));
            world.Put(press, IronOre, 30);
            world.InputStates[press.Id] = new StationInputState(true, 1, true);

            var clear = DefaultBelt.MarkAutoInputs(state.Graph, world.ContainersOn(home));
            Assert.Equal(new[] { press.Id }, clear);
            Assert.True(state.Graph.For(bench.Id).Input[0].Auto);
            Assert.False(state.Graph.For(filtered.Id).Input[0].Auto);

            var restored = SaveCodec.Restore(SaveCodec.FromJson(SaveCodec.ToJson(SaveCodec.Capture(state, state.Graph.Sequence))), out _);
            Assert.True(restored.Graph.For(press.Id).Input[0].Auto);

            var toClear = new HashSet<ContainerId>(clear);
            void Run() => new ConveyorTick(world, new DepositContext(catalog, world, new[] { home }, home), state.Graph,
                new CastleRules { DefaultReserve = 0 }, new TickState(), new TickOptions { StationsToClear = toClear }).Run(0);
            Run(); // even while it works: a factory would otherwise use up what it was wrongly fed
            Assert.Equal(0, world.Count(press.Id, IronOre));
            Assert.Equal(30, world.Count(supply.Id, IronOre));
            Assert.Empty(toClear);

            // A station this world does not know (another island under the same settings) stays listed.
            var elsewhere = new ContainerId(System.Guid.NewGuid());
            toClear.Add(elsewhere);
            Run();
            Assert.Contains(elsewhere, toClear);
        }

        [Fact]
        public void Cleanup_empties_a_coffin_and_leaves_a_unit_station_its_ingredients()
        {
            var chest = world.Chest(home, "Storage");
            var coffin = world.Chest(home, "Coffin", kind: NodeKind.Station); coffin.Role = StationRole.Coffin;
            var tomb = world.Chest(home, "Tomb", kind: NodeKind.Station); tomb.Role = StationRole.UnitSpawner;
            world.StationRecipes[tomb.Id] = new List<Recipe> { new(7, new[] { new ItemStack(Coal, 8) }, new ItemStack[0], true) };
            world.Put(coffin, IronOre, 40).Put(tomb, IronOre, 20).Put(tomb, Coal, 16);
            var toClear = new HashSet<ContainerId> { coffin.Id, tomb.Id };
            new ConveyorTick(world, new DepositContext(catalog, world, new[] { home }, home), state.Graph,
                new CastleRules { DefaultReserve = 0 }, new TickState(), new TickOptions { StationsToClear = toClear }).Run(0);
            Assert.Equal(0, world.Count(coffin.Id, IronOre));
            Assert.Equal(0, world.Count(tomb.Id, IronOre));
            Assert.Equal(16, world.Count(tomb.Id, Coal));
            Assert.Equal(60, world.Count(chest.Id, IronOre));
            Assert.Empty(toClear);
            Assert.True(SaveCodec.Restore(new IslandSave { Version = 5 }, out _).MisfiledStationsDue);
        }

        [Fact]
        public void Only_saves_older_than_version_5_mark_automatic_inputs()
        {
            Assert.True(SaveCodec.Restore(new IslandSave { Version = 4 }, out _).AutoInputsDue);
            Assert.False(SaveCodec.Restore(new IslandSave { Version = 5 }, out _).AutoInputsDue);
        }

        [Fact]
        public void Sharing_scope_and_supplier_filters_limit_feeding()
        {
            var supply = world.Chest(away, "Supplies");
            var station = Furnace();
            world.Put(supply, IronOre, 100).Put(supply, Coal, 100);
            Initialize();
            Assert.Empty(Tick(home).Moves);
            state.Graph.For(supply.Id).Output[0].Filter.ExcludeItems.Add(Coal);
            Assert.Empty(Tick().Moves);
            Assert.Equal(0, world.Count(station.Id, IronOre));
        }

        [Fact]
        public void Existing_lists_and_cleared_defaults_survive_reload()
        {
            var configured = world.Chest(home, "Imported");
            var fresh = world.Chest(home, "New");
            state.Graph.For(configured.Id).Output.Add(ListEntry.Of(fresh.Id));
            Initialize();
            Assert.Equal(EntryKind.Container, Assert.Single(state.Graph.For(configured.Id).Output).Kind);
            state.Graph.For(fresh.Id).Output.Clear();
            var saved = SaveCodec.Capture(state, state.Graph.Sequence);
            Assert.True(saved.Containers[fresh.Id.Value.ToString("N")].KeepEmpty);
            var restored = SaveCodec.Restore(SaveCodec.FromJson(SaveCodec.ToJson(saved)), out _);
            DefaultBelt.Initialize(restored.Graph, world.ContainersOn(home));
            Assert.Empty(restored.Graph.For(fresh.Id).Output);
        }

        [Fact]
        public void Hearts_and_special_storage_do_not_supply_default_belt()
        {
            world.Chest(home, "Heart", kind: NodeKind.Heart);
            world.Chest(home, "Trash").Deposit.Special = SpecialKind.Trash;
            world.Chest(home, "Skip").Deposit.Special = SpecialKind.Skip;
            Initialize();
            Assert.Empty(DefaultBelt.Suppliers(state.Graph));
        }

        [Fact]
        public void Ineligible_servant_container_gets_no_defaults_or_automatic_supply_edges()
        {
            var coffin = world.Chest(home, "Renamed by player");
            coffin.DefaultBeltEligible = false;
            world.Put(coffin, IronOre, 100).Put(coffin, Coal, 20).Put(coffin, Sword, 1);
            Furnace();
            Initialize();
            Initialize();
            Assert.DoesNotContain(coffin.Id, DefaultBelt.Suppliers(state.Graph));
            Assert.False(state.Graph.Lists.ContainsKey(coffin.Id));
            Assert.Empty(Tick().Moves);
            Assert.Equal(1, world.Count(coffin.Id, Sword));
        }

        [Fact]
        public void Ineligible_container_keeps_every_configured_connection()
        {
            var coffin = world.Chest(home, "Servant coffin");
            coffin.DefaultBeltEligible = false;
            var target = world.Chest(home, "Storage");
            var output = ListEntry.Of(target.Id);
            output.Filter.Items.Add(Sword);
            state.Graph.For(coffin.Id).Output.Add(output);
            Initialize();
            Assert.Same(output, Assert.Single(state.Graph.For(coffin.Id).Output));
            Assert.Contains(Sword, output.Filter.Items);
        }

        [Fact]
        public void Furniture_label_does_not_control_default_eligibility()
        {
            var ordinary = world.Chest(home, "SpecialStation ServantCoffin");
            Initialize();
            Assert.Contains(ordinary.Id, DefaultBelt.Suppliers(state.Graph));
        }

        [Fact]
        public void Bench_inputs_use_reverse_priority_and_can_be_cleared()
        {
            var a = world.Chest(home, "A");
            var b = world.Chest(home, "B");
            var station = Furnace();
            Initialize();
            var reverse = new[] { b.Id, a.Id };
            Assert.Equal(reverse, MaterialPolicy.ForInput(state.Graph, station.Id, reverse, IronOre, catalog));
            state.Graph.For(a.Id).Output.Clear();
            Assert.Equal(new[] { b.Id }, MaterialPolicy.ForInput(state.Graph, station.Id, reverse, IronOre, catalog));
            state.Graph.For(station.Id).Input.Clear();
            Assert.Empty(MaterialPolicy.ForInput(state.Graph, station.Id, reverse, IronOre, catalog));
        }

        [Fact]
        public void Chest_edges_obey_accepts_rules()
        {
            var a = world.Chest(home, "A");
            var b = world.Chest(home, "B");
            world.Put(a, IronOre, 50);
            b.Deposit.Takes = Takes.OnlyListed;
            b.Deposit.ItemPriority[Coal] = 0;
            state.Graph.For(a.Id).Output.Add(ListEntry.Of(b.Id));
            Assert.Empty(Tick().Moves);
            Assert.Equal(50, world.Count(a.Id, IronOre));
        }

        [Fact]
        public void Bench_inventory_is_not_prefilled_or_reclaimed_by_conveyors()
        {
            var supply = world.Chest(home, "Supplies");
            var bench = Furnace();
            bench.Role = StationRole.Workstation;
            world.Put(supply, IronOre, 100).Put(supply, Coal, 100);
            world.Put(bench, IronOre, 3);
            Initialize();
            Assert.Empty(Tick().Moves);
            Assert.Equal(3, world.Count(bench.Id, IronOre));
        }

        [Fact]
        public void Leftovers_prefer_best_input_home_then_fall_back_when_inputs_are_full()
        {
            var best = world.Chest(home, "Best input", capacity: 10);
            best.Deposit.BasePriority = 5;
            var other = world.Chest(home, "Other input", capacity: 10);
            var fallback = world.Chest(home, "General storage");
            fallback.Deposit.BasePriority = 10;
            var station = Furnace(false);
            world.InputStates[station.Id] = Satisvampory.Engine.Stations.StationInputState.Idle;
            world.Put(station, IronOre, 25);
            state.Graph.For(station.Id).Input.Add(ListEntry.Of(other.Id));
            state.Graph.For(station.Id).Input.Add(ListEntry.Of(best.Id));
            var ctx = new DepositContext(catalog, world, new[] { home }, home);
            var ticks = new TickState();
            var rules = new CastleRules { DefaultReserve = 0 };
            new ConveyorTick(world, ctx, state.Graph, rules, ticks).Run(0);
            var report = new ConveyorTick(world, ctx, state.Graph, rules, ticks).Run(31);
            Assert.Equal(new[] { best.Id, other.Id, fallback.Id }, report.Moves.Select(m => m.To));
            Assert.Equal(0, world.Count(station.Id, IronOre));
        }
    }
}
