// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Deposit
{
    /// <summary>How one chest's deposit settings rate one item (spec 01, 03). Names play no part.</summary>
    public static class DepositScore
    {
        public static bool IsBanned(ContainerInfo chest, ItemId item, ItemCatalog catalog)
        {
            var o = chest.Deposit;
            if (o.BannedItems.Contains(item)) return true;
            foreach (var c in o.BannedCategories)
                if (catalog.InCategory(item, c)) return true;
            return false;
        }

        /// <summary>
        /// The entry that applies to the item: the item's own entry wins over any category entry;
        /// between categories the larger magnitude wins. Null when nothing is listed for it.
        /// </summary>
        public static (int Adjust, string Source)? Entry(ContainerInfo chest, ItemId item, ItemCatalog catalog)
        {
            var o = chest.Deposit;
            if (o.ItemPriority.TryGetValue(item, out var p)) return (p, catalog.NameOf(item));
            (int, string)? best = null;
            foreach (var kv in o.CategoryPriority)
                if (catalog.InCategory(item, kv.Key) && (best == null || Math.Abs(kv.Value) > Math.Abs(best.Value.Item1)))
                    best = (kv.Value, "Category " + kv.Key);
            return best;
        }

        /// <summary>Whether the chest takes the item at all, before rules, specials and free space.</summary>
        public static bool Takes(ContainerInfo chest, ItemId item, ItemCatalog catalog)
        {
            if (IsBanned(chest, item, catalog)) return false;
            return chest.Deposit.Takes switch
            {
                Model.Takes.Anything => true,
                Model.Takes.OnlyListed => Entry(chest, item, catalog) != null,
                _ => false,
            };
        }

        /// <summary>Base priority plus the applying entry's adjustment.</summary>
        public static int Score(ContainerInfo chest, ItemId item, ItemCatalog catalog) =>
            chest.Deposit.BasePriority + (Entry(chest, item, catalog)?.Adjust ?? 0);

        /// <summary>Plain words for the preview: "Category ore, priority 60".</summary>
        public static string Explain(ContainerInfo chest, ItemId item, ItemCatalog catalog)
        {
            var b = chest.Deposit.BasePriority;
            if (Entry(chest, item, catalog) is { } e)
                return $"{e.Source}, priority {e.Adjust + b}";
            return b == 0 ? "takes anything" : $"takes anything, priority {b}";
        }
    }
}
