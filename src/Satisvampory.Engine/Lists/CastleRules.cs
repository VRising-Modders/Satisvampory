// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Lists
{
    /// <summary>Reserves and production caps for an island (spec 02, 10).</summary>
    public sealed class CastleRules
    {
        /// <summary>Default reserve per item. 0 disables reserve.</summary>
        public int DefaultReserve { get; set; } = 10;

        public Dictionary<ItemId, int> ItemReserve { get; } = new();
        /// <summary>Keyed by folded item group name.</summary>
        public Dictionary<string, int> GroupReserve { get; } = new();

        /// <summary>Production caps; 0 means make none. Missing means unlimited.</summary>
        public Dictionary<ItemId, int> ItemCap { get; } = new();
        public Dictionary<string, int> GroupCap { get; } = new();

        public bool LogisticsOn { get; set; } = true;

        /// <summary>Item override, else the largest group override, else the default.</summary>
        public int Reserve(ItemId item, ItemCatalog catalog)
        {
            if (ItemReserve.TryGetValue(item, out var r)) return r;
            int? best = null;
            foreach (var kv in GroupReserve)
                if (catalog.InGroup(item, kv.Key) && (best == null || kv.Value > best)) best = kv.Value;
            return best ?? DefaultReserve;
        }

        /// <summary>Item cap, else the smallest group cap, else null (unlimited).</summary>
        public int? Cap(ItemId item, ItemCatalog catalog)
        {
            if (ItemCap.TryGetValue(item, out var c)) return c;
            int? best = null;
            foreach (var kv in GroupCap)
                if (catalog.InGroup(item, kv.Key) && (best == null || kv.Value < best)) best = kv.Value;
            return best;
        }
    }
}
