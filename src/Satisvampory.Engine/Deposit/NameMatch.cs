// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Deposit
{
    /// <summary>How a chest's name relates to one item.</summary>
    public enum NameMatchKind
    {
        /// <summary>Named, and the name does not match the item.</summary>
        Custom,
        /// <summary>Blank, a generic word, or the furniture's own name.</summary>
        Generic,
        /// <summary>The name is an item group the item is in, or dest-typed furniture for it.</summary>
        ItemGroup,
        /// <summary>The name is the item's name or an exact alias.</summary>
        Exact,
    }

    public static class NameMatch
    {
        static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
        {
            "chest", "chests", "container", "storage", "empty", "general", "misc", "miscellaneous",
            "everything", "everything else", "all", "other", "others", "extra", "dump", "stuff",
        };

        public static NameMatchKind Classify(ContainerInfo chest, ItemId item, ItemCatalog catalog)
        {
            var name = NameFold.Fold(chest.Name);

            if (name.Length == 0)
                return chest.FurnitureGroup != null && catalog.InGroup(item, chest.FurnitureGroup)
                    ? NameMatchKind.ItemGroup
                    : NameMatchKind.Generic;

            if (catalog.TryResolveItem(name, out var exact))
                return exact == item ? NameMatchKind.Exact : NameMatchKind.Custom;

            if (catalog.TryResolveGroup(name, out var wholeGroup))
                return catalog.InGroup(item, wholeGroup) ? NameMatchKind.ItemGroup : NameMatchKind.Custom;

            if (IsGeneric(name, chest)) return NameMatchKind.Generic;

            // Multi-word names: any word that is an item group the item belongs to. Group words
            // match by membership only, never by substring ("Blood Jewel" is a jewel, not blood).
            foreach (var word in name.Split(' '))
                if (catalog.TryResolveGroup(word, out var g) && catalog.InGroup(item, g))
                    return NameMatchKind.ItemGroup;

            return NameMatchKind.Custom;
        }

        static bool IsGeneric(string folded, ContainerInfo chest)
        {
            if (GenericWords.Contains(folded)) return true;
            if (chest.FurnitureName.Length > 0 && folded == NameFold.Fold(chest.FurnitureName)) return true;
            // "small chest", "large storage" and similar furniture-only names.
            var words = folded.Split(' ');
            if (words.Length == 2 && words[0] is "small" or "medium" or "large" or "huge" or "big"
                && GenericWords.Contains(words[1]))
                return true;
            return false;
        }

        /// <summary>Base priority plus the most specific item priority (spec 01).</summary>
        public static int EffectivePriority(ContainerInfo chest, ItemId item, ItemCatalog catalog)
        {
            var o = chest.Deposit;
            if (o.ItemPriority.TryGetValue(item, out var p)) return o.BasePriority + p;
            var best = 0;
            foreach (var kv in o.GroupPriority)
                if (catalog.InGroup(item, kv.Key) && Math.Abs(kv.Value) > Math.Abs(best))
                    best = kv.Value;
            return o.BasePriority + best;
        }

        public static bool IsBanned(ContainerInfo chest, ItemId item, ItemCatalog catalog)
        {
            var o = chest.Deposit;
            if (o.BannedItems.Contains(item)) return true;
            foreach (var g in o.BannedGroups)
                if (catalog.InGroup(item, g)) return true;
            return false;
        }
    }
}
