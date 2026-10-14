// SPDX-License-Identifier: AGPL-3.0-only
using Satisvampory.Engine.Model;
using Xunit;

namespace Satisvampory.Engine.Tests
{
    public class CatalogLiveShapeTests
    {
        [Fact]
        public void An_item_in_the_live_catalog_shape_resolves_by_name()
        {
            // The shape Progenitor's server catalog returned live for Stone (2026-10-14).
            var cat = CatalogBuilder.Build(new[] { new GameItem(-1531666018, "Item_Ingredient_Stone", null, 1000, 67108864, 0) });
            Assert.True(cat.TryResolveItem("stone", out var id));
            Assert.Equal(-1531666018, id.Guid);
            Assert.Equal("Stone", cat.NameOf(id));
        }

        [Fact]
        public void A_two_word_prefab_name_resolves_by_its_spaced_name()
        {
            // Live server entry for Copper Ore (2026-10-14): no display name, Mineral|Ingredient flags.
            var cat = CatalogBuilder.Build(new[] { new GameItem(-1805325497, "Item_Ingredient_Mineral_CopperOre", null, 250, 67109376, 0) });
            Assert.Equal("Copper Ore", cat.NameOf(new ItemId(-1805325497)));
            Assert.True(cat.TryResolveItem("Copper Ore", out var id));
            Assert.Equal(-1805325497, id.Guid);
        }
    }
}
