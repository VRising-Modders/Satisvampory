using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Stations;
using Satisvampory.Engine.Ui;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests;

public class FindTests
{
    [Theory]
    [InlineData("Iron", "iron", 0)]
    [InlineData("Iron ingot", "iron", 1)]
    [InlineData("Dark iron ingot", "iron", 2)]
    [InlineData("Ironwood", "wood", 3)]
    [InlineData("Copper", "iron", 4)]
    public void Ranks_name_relevance(string name, string query, int rank) => Assert.Equal(rank, Find.MatchRank(name, query));

    [Fact]
    public void Finds_unstocked_disabled_recipe_producer_without_widening_scope()
    {
        var w = new FakeWorld(); var home = new PlotId(1); var away = new PlotId(2);
        var station = w.Chest(home, "Workshop", kind: NodeKind.Station);
        station.FurnitureName = "Furnace";
        var remote = w.Chest(away, "Remote"); w.Put(remote, IronIngot, 200);
        w.StationRecipes[station.Id] = new List<Recipe> { new(1, new[] { new ItemStack(IronOre, 10) }, new[] { new ItemStack(IronIngot, 1) }, false) };
        var local = Find.Run(w, Catalog(), new[] { home }, home, p => "Plot " + p, "iron ingot");
        var item = Assert.Single(local.Items); var place = Assert.Single(item.Chests);
        Assert.Equal(station.Id, place.Id); Assert.True(place.Produces); Assert.Equal(0, item.Total);
        var clan = Find.Run(w, Catalog(), new[] { home, away }, home, p => "Plot " + p, "iron ingot");
        Assert.Equal(2, clan.Items[0].Chests.Count); Assert.Equal(200, clan.Items[0].Total);
        Assert.Single(Find.Run(w, Catalog(), new[] { home }, home, _ => "Home", "furnace").ChestsByName);
    }

    [Fact]
    public void Includes_refining_output_once_and_preserves_plot_label()
    {
        var w = new FakeWorld(); var home = new PlotId(1);
        var station = w.Chest(home, "Furnace", kind: NodeKind.Station); station.Role = StationRole.Refinement;
        w.Put(station, IronIngot, 2).Put(station, IronIngot, 7, InvPart.Output);
        var found = Find.Run(w, Catalog(), new[] { home }, home, _ => "Hollow Keep", "iron ingot");
        Assert.Equal(9, Assert.Single(found.Items).Total);
        Assert.Equal("Hollow Keep", found.Items[0].Chests[0].PlotLabel);
    }
    [Fact]
    public void Filters_keep_locations_for_items_and_exact_mode_rejects_partial_names()
    {
        var w = new FakeWorld(); var home = new PlotId(1);
        var chest = w.Chest(home, "Iron store"); w.Put(chest, IronIngot, 5);
        var containers = Find.Run(w, Catalog(), new[] { home }, home, _ => "Home", "iron", kind: 1);
        Assert.Empty(containers.Items); Assert.Single(containers.ChestsByName);
        var items = Find.Run(w, Catalog(), new[] { home }, home, _ => "Home", "iron ingot", kind: 2);
        Assert.Empty(items.ChestsByName); Assert.Single(Assert.Single(items.Items).Chests);
        Assert.Empty(Find.Run(w, Catalog(), new[] { home }, home, _ => "Home", "iron", false, 1).ChestsByName);
        Assert.Single(Find.Run(w, Catalog(), new[] { home }, home, _ => "Home", "iron store", false, 1).ChestsByName);
    }

    [Fact]
    public void Finds_research_station_by_familiar_spelling()
    {
        var w = new FakeWorld(); var home = new PlotId(1);
        var station = w.Chest(home, "", kind: NodeKind.Station); station.FurnitureName = "Athenaeum";
        Assert.Single(Find.Run(w, Catalog(), new[] { home }, home, _ => "Home", "atheneum").ChestsByName);
    }
    [Fact]
    public void Excluded_construction_is_not_returned_even_for_an_exact_query()
    {
        var w = new FakeWorld(); var home = new PlotId(1);
        var catalog = Catalog(); var wall = new ItemId(987654);
        catalog.AddItem(wall, "Castle Wall", 1);
        var result = Find.Run(w, catalog, new[] { home }, home, _ => "Home", "Castle Wall", includeItem: i => i != wall);
        Assert.Empty(result.Items);
        Assert.True(catalog.TryResolveItem("Castle Wall", out _));
    }
}
