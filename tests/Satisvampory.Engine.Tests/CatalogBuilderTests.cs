// SPDX-License-Identifier: AGPL-3.0-only
using System.Linq;
using Satisvampory.Engine.Model;
using Xunit;

namespace Satisvampory.Engine.Tests
{
    public class CatalogBuilderTests
    {
        // Invented guids; prefab-name shapes and flags follow the game's conventions.
        static GameItem Item(int g, string prefab, GameItemCategory flags, GameItemType type = GameItemType.Stackable, string? display = null, int stack = 100) =>
            new(g, prefab, display, stack, (long)flags, (int)type);

        static ItemCatalog Build() => CatalogBuilder.Build(new[]
        {
            Item(1, "Item_Ingredient_Mineral_IronOre", GameItemCategory.Mineral, display: "Iron Ore"),
            Item(2, "Item_Ingredient_Mineral_IronIngot", GameItemCategory.Mineral, display: "Iron Ingot"),
            Item(3, "Item_Ingredient_Plank", GameItemCategory.Woodworking, display: "Plank"),
            Item(4, "Item_Ingredient_Wood_Standard", GameItemCategory.Lumber, display: "Lumber"),
            Item(5, "Item_Ingredient_Plant_HellsClarion", GameItemCategory.Flower, display: "Hell's Clarion"),
            Item(6, "Item_Ingredient_Plant_Thistle", GameItemCategory.Flower, display: "Thistle"),
            Item(7, "Item_Ingredient_Plant_ThistleSeed", GameItemCategory.Flower, display: "Thistle Seed"),
            Item(8, "Item_Ingredient_Sapling_Cypress", GameItemCategory.Lumber, display: "Cypress Sapling"),
            Item(9, "Item_BloodEssence_T01", GameItemCategory.BloodEssence, display: "Blood Essence", stack: 500),
            Item(10, "Item_Ingredient_Gem_Ruby_T01", GameItemCategory.Gem, display: "Flawless Ruby"),
            Item(11, "Item_Jewel_Unholy_T01", GameItemCategory.Jewel, GameItemType.Jewel, display: "Unholy Jewel"),
            Item(12, "Item_Weapon_Sword_T01_Bone", GameItemCategory.Weapon, GameItemType.Equippable, display: "Bone Sword", stack: 1),
            Item(13, "Item_Ingredient_Bone", GameItemCategory.None, display: "Bone"),
            Item(14, "Item_Ingredient_Leather", GameItemCategory.Tailoring, display: "Leather"),
            Item(15, "Item_Locked_Thing", GameItemCategory.Locked, display: "Locked"),
            Item(16, "Item_Ingredient_Mineral_DarkSilverIngot", GameItemCategory.Mineral | GameItemCategory.Silver),
        });

        [Fact]
        public void Recipe_stand_ins_are_not_items()
        {
            var c = CatalogBuilder.Build(new[]
            {
                new GameItem(736318803, "Item_Ingredient_Fish_SageFish_T02", "Sage Fish", 10, 0, 0),
                new GameItem(172410251, "FakeItem_FeedPrisoner_SageFish", "Feed Prisoner", 1, 0, 1),
            });
            Assert.True(c.TryResolveItem("Sage Fish", out _));
            Assert.DoesNotContain(new ItemId(172410251), c.AllItems);
        }

        [Fact]
        public void Flags_and_name_families_place_items()
        {
            var c = Build();
            bool In(int guid, string cat) => c.InCategory(new ItemId(guid), cat);

            Assert.True(In(1, "ore") && !In(1, "ingots") && In(1, "minerals"));
            Assert.True(In(2, "ingots") && !In(2, "ore"));
            Assert.True(In(3, "planks") && In(3, "woodworking"));
            Assert.True(In(4, "wood"));
            Assert.True(In(5, "mushrooms") && !In(5, "flowers"));   // Hell's Clarion is a mushroom
            Assert.True(In(6, "flowers"));
            Assert.True(In(7, "seeds") && !In(7, "flowers"));
            Assert.True(In(8, "wood") && !In(8, "seeds"));           // saplings are wood
            Assert.True(In(9, "blood"));
            Assert.True(In(10, "gems") && !In(10, "jewels"));
            Assert.True(In(11, "jewels"));
            Assert.True(In(12, "weapons") && !In(12, "bones"));      // a bone sword is a weapon, not a bone
            Assert.True(In(13, "bones"));
            Assert.True(In(14, "hides") && In(14, "tailoring"));
            Assert.False(c.TryResolveItem("Locked", out _));
        }

        [Fact]
        public void Aliases_names_stacks_and_built_ins()
        {
            var c = Build();
            Assert.True(c.TryResolveItem("be", out var be) && be.Guid == 9);
            Assert.Equal(500, c.MaxStack(be));
            Assert.True(c.TryResolveCategory("ores", out var ore) && ore == "ore");
            Assert.True(c.TryResolveCategory("leather", out var hides) && hides == "hides");
            Assert.True(c.IsBuiltIn("mushrooms"));
            Assert.Equal("Dark Silver Ingot", c.NameOf(new ItemId(16)));   // made from the prefab name
            Assert.True(c.TryResolveItem("dsi", out var dsi) && dsi.Guid == 16);
        }

        [Theory]
        [InlineData("Item_Ingredient_Mineral_IronOre", "Iron Ore")]
        [InlineData("Item_Consumable_HealingPotion_T01", "Healing Potion")]
        [InlineData("Item_Building_Plants_Mourning_Lily", "Plants Mourning Lily")]
        [InlineData("Item_Ingredient_GemDust", "Gem Dust")]
        public void Display_name_from_prefab(string prefab, string expected) =>
            Assert.Equal(expected, CatalogBuilder.DisplayNameOf(new GameItem(1, prefab, null, 1, 0, 0)));
    }
}
