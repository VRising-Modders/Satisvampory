using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Loops;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Persistence;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.World;
using Satisvampory.Engine.Ui;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests;

public class DirectedBeltTests
{
    static string Id(ContainerId id) => id.Value.ToString("N");
    static IslandState Reload(IslandState s) => SaveCodec.Restore(
        SaveCodec.FromJson(SaveCodec.ToJson(SaveCodec.Capture(s, s.Graph.Sequence))), out _);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Material_sources_and_output_preview_require_both_end_filters(bool builtIn)
    {
        var w = new FakeWorld(); var home = new PlotId(1); var g = new LogisticsGraph();
        var supplier = w.Chest(home, "Supplier"); var receiver = w.Chest(home, "Receiver");
        DefaultBelt.Initialize(g, System.Array.Empty<ContainerInfo>());
        var belt = builtIn ? g.Belts[DefaultBelt.Id] : g.BeltOrCreate("Custom");
        BeltOps.AddMembers(g, belt.Id, new[] { supplier.Id });
        BeltOps.AddMembers(g, belt.Id, new[] { receiver.Id }, false);
        var output = g.For(supplier.Id).Output.Single();
        var input = g.For(receiver.Id).Input.Single();
        output.Filter.Categories.Add("ore");
        input.Filter.ExcludeItems.Add(CopperOre);
        var permitted = new[] { supplier.Id };
        var ctx = new DepositContext(Catalog(), w, new[] { home }, home);
        foreach (var blocked in new[] { Coal, CopperOre })
        {
            Assert.Empty(MaterialPolicy.ForInput(g, receiver.Id, permitted, blocked, Catalog()));
            var step = Assert.Single(Evaluator.OutputList(ctx, g, supplier.Id, blocked, 10).Steps);
            Assert.Equal(receiver.Id, step.Id);
            Assert.Equal(0, step.Amount);
            Assert.Contains("filter", step.Why);
        }
        Assert.Equal(permitted, MaterialPolicy.ForInput(g, receiver.Id, permitted, IronOre, Catalog()));
        Assert.Equal(10, Assert.Single(Evaluator.OutputList(ctx, g, supplier.Id, IronOre, 10).Steps).Amount);
        input.Filter.ExcludeItems.Add(IronOre);
        Assert.Empty(MaterialPolicy.ForInput(g, receiver.Id, permitted, IronOre, Catalog()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Null_legacy_lists_and_versionless_saves_migrate_without_reapplying_defaults(bool explicitVersion)
    {
        var w = new FakeWorld(); var home = new PlotId(1);
        var supplier = w.Chest(home, "Supplier"); var receiver = w.Chest(home, "Receiver");
        var cleared = w.Chest(home, "Cleared"); var belt = BeltId.New();
        var json = $$"""
        {
          {{(explicitVersion ? "\"Version\": 1," : "")}}
          "Belts": [{ "Id": "{{belt.Value:N}}", "Name": "Legacy", "Members": ["{{Id(supplier.Id)}}"] }],
          "Containers": {
            "{{Id(receiver.Id)}}": { "Output": null, "Input": [null, {"Kind": "belt", "Belt": "{{belt.Value:N}}", "Filter": { "Items": null, "ExcludeItems": null, "Categories": null, "ExcludeCategories": null } }] },
            "{{Id(cleared.Id)}}": { "Input": null, "Output": null }
          }
        }
        """;
        // The plugin's store deserializes DTOs directly rather than calling FromJson.
        var typed = System.Text.Json.JsonSerializer.Deserialize<IslandSave>(json)!;
        Assert.Equal(1, typed.Version);
        Assert.Single(SaveCodec.Restore(typed, out _).Graph.Edges());
        var state = SaveCodec.Restore(SaveCodec.FromJson(json), out _);
        Assert.Equal(IslandSave.CurrentVersion, SaveCodec.Capture(state, state.Graph.Sequence).Version);
        for (var pass = 0; pass < 2; pass++)
        {
            DefaultBelt.Initialize(state.Graph, w.ContainersOn(home));
            Assert.Empty(state.Graph.For(cleared.Id).Input);
            Assert.Empty(state.Graph.For(cleared.Id).Output);
            Assert.Empty(state.Graph.Members(DefaultBelt.Id));
            var edge = Assert.Single(state.Graph.Edges());
            Assert.Equal((supplier.Id, receiver.Id), (edge.From, edge.To));
            Assert.True(edge.Filter.Allows(IronOre, Catalog()));
            state = Reload(state);
        }
    }

    [Fact]
    public void Null_save_is_refused_and_null_legacy_belts_mean_no_members()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => SaveCodec.FromJson("null"));
        var save = SaveCodec.FromJson("{\"Version\":1,\"Belts\":null,\"Containers\":null}");
        Assert.Empty(SaveCodec.Restore(save, out _).Graph.Edges());
    }

    [Fact]
    public void Custom_belt_moves_only_allowed_stock_in_receiver_order()
    {
        var w = new FakeWorld(); var home = new PlotId(1); var g = new LogisticsGraph();
        var source = w.Chest(home, "Source");
        var first = w.Chest(home, "First", capacity: 10);
        var second = w.Chest(home, "Second", capacity: 10);
        w.Put(source, IronOre, 20).Put(source, Coal, 20);
        var belt = g.BeltOrCreate("Ore");
        BeltOps.AddMembers(g, belt.Id, new[] { source.Id });
        BeltOps.AddMembers(g, belt.Id, new[] { first.Id, second.Id }, false);
        g.For(source.Id).Output.Single().Filter.ExcludeItems.Add(Coal);
        BeltOps.Move(g, belt.Id, 1, 0, false);
        var ctx = new DepositContext(Catalog(), w, new[] { home }, home);
        var report = new ConveyorTick(w, ctx, g, new CastleRules { DefaultReserve = 0 }, new TickState()).Run(0);
        Assert.Equal(new[] { second.Id, first.Id }, report.Moves.Select(m => m.To));
        Assert.Equal(20, w.Count(source.Id, Coal));
        Assert.Equal(0, w.Count(source.Id, IronOre));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_belt_kinds_use_direction_and_both_filters(bool builtIn)
    {
        var state = new IslandState();
        var g = state.Graph;
        DefaultBelt.Initialize(g, System.Array.Empty<ContainerInfo>());
        var belt = builtIn ? g.Belts[DefaultBelt.Id] : g.BeltOrCreate("Ore");
        var (a, b, c) = (ContainerId.New(), ContainerId.New(), ContainerId.New());
        BeltOps.AddMembers(g, belt.Id, new[] { a, b });
        Assert.Empty(g.Edges());
        BeltOps.AddMembers(g, belt.Id, new[] { c }, output: false);
        g.For(a).Output.Single().Filter.Categories.Add("ore");
        g.For(c).Input.Single().Filter.ExcludeItems.Add(CopperOre);
        var edges = g.Edges();
        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => { Assert.Equal(c, e.To); Assert.False(e.DefinedAsOutput); });
        var edge = edges.Single(e => e.From == a);
        Assert.True(edge.Filter.Allows(IronOre, Catalog()));
        Assert.False(edge.Filter.Allows(CopperOre, Catalog()));
        Assert.False(edge.Filter.Allows(Coal, Catalog()));
        Assert.Equal(new[] { c }, g.Expand(g.For(a).Output.Single()));
        Assert.Equal(new[] { a, b }, g.Expand(g.For(c).Input.Single()));
        Assert.False(g.Allows(g.For(c).Input.Single(), c, false, a, Coal, Catalog()));
        Assert.Equal(2, Reload(state).Graph.Edges().Count);
    }

    [Fact]
    public void Direction_order_duplicate_and_removal_survive_reload()
    {
        var s = new IslandState(); var g = s.Graph; var belt = g.BeltOrCreate("Line");
        var (a, b, c, d) = (ContainerId.New(), ContainerId.New(), ContainerId.New(), ContainerId.New());
        BeltOps.AddMembers(g, belt.Id, new[] { a, b });
        BeltOps.AddMembers(g, belt.Id, new[] { c, d }, false);
        g.For(a).Output.Single().Filter.ExcludeItems.Add(Coal);
        Assert.True(BeltOps.Move(g, belt.Id, 1, 0));
        Assert.True(BeltOps.Move(g, belt.Id, 1, 0, false));
        Assert.Equal(new[] { b, a }, g.Suppliers(belt.Id));
        Assert.Equal(new[] { d, c }, g.Receivers(belt.Id));
        Assert.Equal(new[] { d, c }, g.Edges().Where(e => e.From == a).Select(e => e.To));
        Assert.True(BeltOps.Duplicate(g, belt.Id, "Copy", out var copy).Ok);
        g.For(a).Output.Single(e => e.Belt == copy!.Id).Filter.ExcludeItems.Clear();
        Assert.Contains(Coal, g.For(a).Output.Single(e => e.Belt == belt.Id).Filter.ExcludeItems);
        Assert.True(BeltOps.Remove(g, belt.Id, b));
        Assert.True(BeltOps.Remove(g, belt.Id, d, false));
        var back = Reload(s).Graph;
        Assert.Equal(new[] { a }, back.Suppliers(belt.Id));
        Assert.Equal(new[] { c }, back.Receivers(belt.Id));
        Assert.Equal(new[] { b, a }, back.Suppliers(copy!.Id));
        Assert.Equal(new[] { d, c }, back.Receivers(copy.Id));
        Assert.DoesNotContain("\"Members\"", SaveCodec.ToJson(SaveCodec.Capture(s, g.Sequence)));
    }

    [Fact]
    public void Mixed_legacy_membership_preserves_exact_routes_filters_order_and_is_idempotent()
    {
        var belt = BeltId.New();
        var (a, b, x, y) = (ContainerId.New(), ContainerId.New(), ContainerId.New(), ContainerId.New());
        var save = new IslandSave { Version = 1, NextSequence = 0 };
        save.Belts.Add(new BeltDto { Id = belt.Value.ToString("N"), Name = "Old", Members = new() { Id(b), Id(a) } });
        save.Containers[Id(x)] = new ContainerDto { Input = new() { new EntryDto {
            Kind = "belt", Belt = belt.Value.ToString("N"), Sequence = 12,
            Filter = new FilterDto { Categories = new() { "ore" }, ExcludeItems = new() { CopperOre.Guid } } } } };
        save.Containers[Id(y)] = new ContainerDto { Output = new() { new EntryDto {
            Kind = "belt", Belt = belt.Value.ToString("N"), Sequence = 15,
            Filter = new FilterDto { Items = new() { Coal.Guid } } } } };
        var state = SaveCodec.Restore(SaveCodec.FromJson(SaveCodec.ToJson(save)), out var sequence);
        Assert.True(sequence >= 15);
        for (var pass = 0; pass < 3; pass++)
        {
            var g = state.Graph; var edges = g.Edges();
            Assert.Equal(4, edges.Count);
            Assert.Equal(new[] { b, a }, g.Expand(g.For(x).Input.Single()));
            Assert.Equal(new[] { b, a }, g.Expand(g.For(y).Output.Single()));
            Assert.DoesNotContain(edges, e => e.From == y && e.To == x);
            Assert.DoesNotContain(edges, e => (e.From == a || e.From == b) && (e.To == a || e.To == b));
            Assert.All(edges.Where(e => e.To == x), e => {
                Assert.True(e.Filter.Allows(IronOre, Catalog()));
                Assert.False(e.Filter.Allows(CopperOre, Catalog()));
                Assert.False(e.Filter.Allows(Coal, Catalog()));
            });
            Assert.All(edges.Where(e => e.From == y), e => {
                Assert.True(e.DefinedAsOutput);
                Assert.True(e.Filter.Allows(Coal, Catalog()));
                Assert.False(e.Filter.Allows(IronOre, Catalog()));
            });
            Assert.Empty(LoopFinder.Find(edges));
            state = Reload(state);
        }
    }

    [Fact]
    public void Legacy_self_loop_and_unused_members_survive_migration()
    {
        var a = ContainerId.New(); var b = ContainerId.New();
        var loop = BeltId.New(); var unused = BeltId.New();
        var save = new IslandSave { Version = 1 };
        save.Belts.Add(new BeltDto { Id = loop.Value.ToString("N"), Name = "Loop", Members = new() { Id(a) } });
        save.Belts.Add(new BeltDto { Id = unused.Value.ToString("N"), Name = "Unused", Members = new() { Id(b) } });
        save.Containers[Id(a)] = new ContainerDto { Output = new() {
            new EntryDto { Kind = "belt", Belt = loop.Value.ToString("N"), Sequence = 3 } } };
        var state = Reload(SaveCodec.Restore(save, out _));
        Assert.Single(state.Graph.Edges());
        Assert.Single(LoopFinder.Find(state.Graph.Edges()));
        Assert.Equal(new[] { b }, state.Graph.Suppliers(unused));
    }

    [Fact]
    public void Directed_belts_close_real_loops_and_removing_one_direction_breaks_them()
    {
        var g = new LogisticsGraph(); var a = ContainerId.New(); var b = ContainerId.New();
        var forward = g.BeltOrCreate("Forward"); var back = g.BeltOrCreate("Back");
        BeltOps.AddMembers(g, forward.Id, new[] { a });
        BeltOps.AddMembers(g, forward.Id, new[] { b }, false);
        BeltOps.AddMembers(g, back.Id, new[] { b });
        Assert.Empty(LoopFinder.Find(g.Edges()));
        BeltOps.AddMembers(g, back.Id, new[] { a }, false);
        var loop = Assert.Single(LoopFinder.Find(g.Edges()));
        Assert.Equal((b, a), (loop.Closing.From, loop.Closing.To));
        BeltOps.Remove(g, back.Id, a, false);
        Assert.Empty(LoopFinder.Find(g.Edges()));
        Assert.Single(g.For(a).Output);
    }

    [Fact]
    public void Removed_default_connection_is_not_recreated_by_management_or_reload()
    {
        var s = new IslandState(); var w = new FakeWorld(); var c = w.Chest(new PlotId(1), "Storage");
        DefaultBelt.Initialize(s.Graph, new[] { c });
        BeltOps.Remove(s.Graph, DefaultBelt.Id, c.Id);
        s = Reload(s);
        DefaultBelt.Initialize(s.Graph, new[] { c });
        Assert.Empty(s.Graph.For(c.Id).Output);
        Assert.Empty(s.Graph.Members(DefaultBelt.Id));
    }
}
