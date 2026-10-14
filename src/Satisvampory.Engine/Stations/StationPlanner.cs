// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Stations
{
    /// <summary>A station recipe as the engine sees it (mirrors Progenitor's StationRecipe).</summary>
    public sealed record Recipe(int Id, IReadOnlyList<ItemStack> Inputs, IReadOnlyList<ItemStack> Outputs, bool Enabled);

    public sealed class StationPlan
    {
        /// <summary>Crafts each recipe is fed for this round, by recipe id.</summary>
        public Dictionary<int, int> Crafts { get; } = new();

        /// <summary>Amount to pull into the station, per item. Always whole crafts.</summary>
        public Dictionary<ItemId, int> Pull { get; } = new();

        /// <summary>
        /// Held input items nothing can be built from right now. The caller returns them after the
        /// grace period if they are still leftovers then (spec 04).
        /// </summary>
        public Dictionary<ItemId, int> Leftovers { get; } = new();

        /// <summary>For recipes that cannot make one craft: the first missing ingredient.</summary>
        public Dictionary<int, ItemId> Missing { get; } = new();
    }

    public static class StationPlanner
    {
        public const int DefaultFeedCrafts = 5;

        /// <summary>
        /// Whole crafts only (spec 04). Crafts are handed out one at a time to the recipe with the
        /// fewest so far, so recipes sharing an ingredient split it in whole crafts. A recipe that
        /// cannot reach one craft from held plus available gets nothing.
        /// </summary>
        /// <param name="heldStacks">The station's input inventory, summed per item.</param>
        /// <param name="available">Count the input list can supply after reserve.</param>
        public static StationPlan Plan(
            IReadOnlyList<Recipe> recipes,
            IReadOnlyList<ItemStack> heldStacks,
            Func<ItemId, int> available,
            int feedCrafts = DefaultFeedCrafts)
        {
            var plan = new StationPlan();
            var heldMap = new Dictionary<ItemId, int>();
            foreach (var st in heldStacks) heldMap[st.Item] = (heldMap.TryGetValue(st.Item, out var h) ? h : 0) + st.Amount;
            int held(ItemId i) => heldMap.TryGetValue(i, out var h) ? h : 0;
            var enabled = recipes.Where(r => r.Enabled && r.Inputs.Count > 0).ToList();

            var budget = new Dictionary<ItemId, long>();
            long Budget(ItemId i)
            {
                if (!budget.TryGetValue(i, out var b)) budget[i] = b = (long)held(i) + available(i);
                return b;
            }

            var used = new Dictionary<ItemId, long>();
            long Used(ItemId i) => used.TryGetValue(i, out var u) ? u : 0;

            foreach (var r in enabled) plan.Crafts[r.Id] = 0;

            while (true)
            {
                Recipe? pick = null;
                foreach (var r in enabled.OrderBy(r => plan.Crafts[r.Id]).ThenBy(r => r.Id))
                {
                    if (plan.Crafts[r.Id] >= feedCrafts) continue;
                    if (r.Inputs.All(s => Used(s.Item) + s.Amount <= Budget(s.Item))) { pick = r; break; }
                }
                if (pick == null) break;
                plan.Crafts[pick.Id]++;
                foreach (var s in pick.Inputs) used[s.Item] = Used(s.Item) + s.Amount;
            }

            foreach (var r in enabled.Where(r => plan.Crafts[r.Id] == 0))
            {
                var missing = r.Inputs.FirstOrDefault(s => s.Amount > Budget(s.Item));
                if (missing.Amount > 0) plan.Missing[r.Id] = missing.Item;
            }

            foreach (var (item, need) in used)
            {
                var pull = need - held(item);
                if (pull > 0) plan.Pull[item] = (int)Math.Min(pull, int.MaxValue);
            }

            // Leftovers: held items no planned craft uses. Extra of an item a planned recipe uses
            // is kept: it is the next round's stock, not a leftover.
            foreach (var stack in heldStacks)
                if (stack.Amount > 0 && !used.ContainsKey(stack.Item))
                    plan.Leftovers[stack.Item] = plan.Leftovers.TryGetValue(stack.Item, out var n) ? n + stack.Amount : stack.Amount;

            return plan;
        }
    }
}
