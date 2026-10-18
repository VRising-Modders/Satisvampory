// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Satisvampory.Engine.Model
{
    /// <summary>
    /// One game item as Progenitor reports it: prefab guid, prefab name, display name when known,
    /// stack size, and the game's own ItemCategory flags and ItemType.
    /// </summary>
    public sealed record GameItem(int Guid, string PrefabName, string? DisplayName, int MaxStack, long CategoryFlags, int ItemType);

    /// <summary>The game's ItemCategory flags (facts about the game's data, not configuration).</summary>
    [Flags]
    public enum GameItemCategory : long
    {
        None = 0,
        Weapon = 1,
        Armor = 2,
        Gem = 4,
        BloodBound = 8,
        Flower = 16,
        Lumber = 32,
        Stone = 64,
        BloodEssence = 128,
        SoulBound = 256,
        TeleportBound = 512,
        Silver = 1024,
        LoseDurabilityOnDeath = 2048,
        Knowledge = 4096,
        Blood = 8192,
        Relic = 16384,
        Coin = 32768,
        Consumable = 65536,
        Herb = 131072,
        Bag = 262144,
        Saddle = 524288,
        Fish = 1048576,
        StablesIngredient = 2097152,
        BatBound = 4194304,
        Jewel = 8388608,
        Alchemy = 16777216,
        Tailoring = 33554432,
        Mineral = 67108864,
        Woodworking = 134217728,
        Magic = 268435456,
        BloodPotion = 536870912,
        Soulshard = 1073741824,
        Locked = unchecked((long)0xFFFFFFFF80000000),
    }

    /// <summary>The game's ItemType values.</summary>
    public enum GameItemType
    {
        Stackable = 0,
        Consumable = 1,
        Equippable = 2,
        ItemBuilding = 3,
        Memory = 4,
        Tech = 5,
        OrbOfKnowledge = 6,
        VBloodEssence = 7,
        Jewel = 8,
        Bag = 9,
        Saddle = 10,
    }

    /// <summary>
    /// Builds Satisvampory's item catalog from the game's item list (spec 01, "Categories").
    /// Built-in categories come from the game's ItemCategory flags and ItemType first, then a few
    /// prefab-name families the flags do not separate (ore from ingots, planks, hides, bones,
    /// seeds, mushrooms). Rules are Satisvampory's own; an admin can edit the result per island.
    /// </summary>
    public static class CatalogBuilder
    {
        sealed record CategoryRule(string Name, string[] Aliases, Func<GameItem, bool> Match);

        static bool Flag(GameItem i, GameItemCategory f) => (i.CategoryFlags & (long)f) != 0;
        static bool Type(GameItem i, GameItemType t) => i.ItemType == (int)t;
        static bool Name(GameItem i, params string[] parts) =>
            parts.Any(p => i.PrefabName.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0
                || (i.DisplayName?.IndexOf(p, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);

        static bool IsMushroom(GameItem i) => Name(i, "Mushroom", "Clarion", "Toadstool");
        static bool IsSeed(GameItem i) => Name(i, "Seed", "Spore") && !Name(i, "Sapling");

        static readonly CategoryRule[] Rules =
        {
            new("ore", new[] { "ores" }, i => Flag(i, GameItemCategory.Mineral) && Name(i, "Ore") && !Name(i, "Ingot", "Bar")),
            new("ingots", new[] { "ingot", "bars" }, i => Flag(i, GameItemCategory.Mineral) && Name(i, "Ingot", "Bar")),
            new("minerals", new[] { "mineral", "material", "materials" }, i => Flag(i, GameItemCategory.Mineral)),
            new("stone", new[] { "stones" }, i => Flag(i, GameItemCategory.Stone)),
            new("wood", new[] { "lumber", "logs" }, i => Flag(i, GameItemCategory.Lumber) || Name(i, "Sapling")),
            new("planks", new[] { "plank" }, i => Name(i, "Plank")),
            new("woodworking", new string[0], i => Flag(i, GameItemCategory.Woodworking)),
            new("gems", new[] { "gem" }, i => Flag(i, GameItemCategory.Gem)),
            new("jewels", new[] { "jewel" }, i => Flag(i, GameItemCategory.Jewel) || Type(i, GameItemType.Jewel)),
            new("flowers", new[] { "flower" }, i => Flag(i, GameItemCategory.Flower) && !IsMushroom(i) && !IsSeed(i)),
            new("herbs", new[] { "herb" }, i => Flag(i, GameItemCategory.Herb) && !IsMushroom(i) && !IsSeed(i)),
            new("mushrooms", new[] { "mushroom" }, i => IsMushroom(i) && !IsSeed(i)),
            new("seeds", new[] { "seed", "spores" }, IsSeed),
            new("blood", new string[0], i => Flag(i, GameItemCategory.BloodEssence)),
            new("bloodpotions", new[] { "blood potions" }, i => Flag(i, GameItemCategory.BloodPotion)),
            new("consumables", new[] { "consumable", "potion", "potions" }, i => Flag(i, GameItemCategory.Consumable) || Type(i, GameItemType.Consumable)),
            new("alchemy", new string[0], i => Flag(i, GameItemCategory.Alchemy)),
            new("tailoring", new[] { "thread", "cloth" }, i => Flag(i, GameItemCategory.Tailoring)),
            new("hides", new[] { "leather", "leathers", "hide", "pelts" }, i => Name(i, "Leather", "Hide", "Pelt")),
            new("bones", new[] { "bone" }, i => Name(i, "Bone") && !Flag(i, GameItemCategory.Weapon)),
            new("coins", new[] { "coin" }, i => Flag(i, GameItemCategory.Coin)),
            new("silver", new string[0], i => Flag(i, GameItemCategory.Silver)),
            new("fish", new string[0], i => Flag(i, GameItemCategory.Fish)),
            new("knowledge", new[] { "scroll", "paper", "book", "books" }, i => Flag(i, GameItemCategory.Knowledge) || Type(i, GameItemType.Tech) || Type(i, GameItemType.OrbOfKnowledge) || Type(i, GameItemType.Memory)),
            new("magic", new string[0], i => Flag(i, GameItemCategory.Magic)),
            new("soulshards", new[] { "soulshard", "shards" }, i => Flag(i, GameItemCategory.Soulshard)),
            new("relics", new[] { "relic" }, i => Flag(i, GameItemCategory.Relic)),
            new("bags", new[] { "bag" }, i => Flag(i, GameItemCategory.Bag) || Type(i, GameItemType.Bag)),
            new("saddles", new[] { "saddle" }, i => Flag(i, GameItemCategory.Saddle) || Type(i, GameItemType.Saddle)),
            new("feed", new[] { "stables" }, i => Flag(i, GameItemCategory.StablesIngredient)),
            new("weapons", new[] { "weapon" }, i => Flag(i, GameItemCategory.Weapon)),
            new("armor", new[] { "armour" }, i => Flag(i, GameItemCategory.Armor)),
        };

        /// <summary>Built-in category names, for the UI and tests.</summary>
        public static IEnumerable<string> BuiltInNames => Rules.Select(r => r.Name);

        /// <summary>Exact short aliases for single items, by display name (spec 01).</summary>
        static readonly (string Alias, string DisplayName)[] ItemAliases =
        {
            ("be", "Blood Essence"),
            ("gbe", "Greater Blood Essence"),
            ("pbe", "Primal Blood Essence"),
            ("abe", "Ancestral Blood Essence"),
            ("gss", "Greater Stygian Shard"),
            ("sgs", "Siege Golem Stone"),
            ("dsi", "Dark Silver Ingot"),
            ("ot", "Onyx Tear"),
        };

        /// <summary>
        /// The game's recipe stand-ins ("FakeItem_FeedPrisoner_SageFish" is the prison's Feed Prisoner recipe,
        /// "FakeItem_AnyFish" a recipe's "any fish"): never stored, so never an item to find, filter or count.
        /// </summary>
        public static bool IsStandIn(string? prefabName) =>
            prefabName != null && prefabName.StartsWith("FakeItem_", StringComparison.Ordinal);

        public static ItemCatalog Build(IEnumerable<GameItem> items)
        {
            var catalog = new ItemCatalog();
            var kept = new List<GameItem>();
            foreach (var i in items)
            {
                if (i.Guid == 0 || Flag(i, GameItemCategory.Locked) || IsStandIn(i.PrefabName)) continue;
                var name = DisplayNameOf(i);
                if (name.Length == 0) continue;
                catalog.AddItem(new ItemId(i.Guid), name, i.MaxStack > 0 ? i.MaxStack : 1);
                kept.Add(i with { DisplayName = name });
            }

            foreach (var rule in Rules)
                catalog.AddCategory(rule.Name, kept.Where(rule.Match).Select(i => new ItemId(i.Guid)).ToList(), rule.Aliases);

            foreach (var (alias, display) in ItemAliases)
                if (catalog.TryResolveItem(display, out var id)) catalog.AddAlias(alias, id);

            catalog.SealBuiltIns();
            return catalog;
        }

        /// <summary>
        /// The game's display name when Progenitor has it; otherwise a readable name made from the
        /// prefab name: "Item_Ingredient_Mineral_IronOre" → "Iron Ore".
        /// </summary>
        public static string DisplayNameOf(GameItem i)
        {
            if (!string.IsNullOrWhiteSpace(i.DisplayName)) return i.DisplayName!.Trim();
            var parts = i.PrefabName.Split('_').Where(p => p.Length > 0).ToList();
            var noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Item", "Ingredient", "Mineral", "Consumable", "Building", "Resource", "Tier01", "Tier02", "Tier03", "Tier04", "Tier05", "Tier06", "Tier07", "Tier08", "T01", "T02", "T03", "T04", "T05", "T06", "T07", "T08" };
            var kept = parts.Where(p => !noise.Contains(p)).ToList();
            if (kept.Count == 0) return string.Empty;
            return string.Join(" ", kept.Select(SplitCamel)).Trim();
        }

        static string SplitCamel(string s)
        {
            var sb = new StringBuilder();
            for (var k = 0; k < s.Length; k++)
            {
                var c = s[k];
                if (k > 0 && char.IsUpper(c) && (char.IsLower(s[k - 1]) || (k + 1 < s.Length && char.IsLower(s[k + 1]) && char.IsUpper(s[k - 1]))))
                    sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
