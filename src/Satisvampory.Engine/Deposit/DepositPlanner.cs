// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Deposit
{
    public readonly record struct PlannedMove(ContainerId To, int Amount);

    public static class DepositPlanner
    {
        /// <summary>
        /// Where <paramref name="amount"/> of an item would go, by Mass Deposit order and free
        /// space. A split tier is filled round-robin a stack at a time; other tiers fill in order.
        /// <paramref name="exclude"/> is never a target (the source being emptied).
        /// </summary>
        public static List<PlannedMove> Plan(DepositContext ctx, ItemId item, int amount, ContainerId? exclude = null)
        {
            var moves = new Dictionary<ContainerId, int>();
            var order = new List<ContainerId>();
            var left = amount;
            if (left <= 0) return new List<PlannedMove>();

            var ranking = DepositRanker.Rank(ctx, item);
            var stack = ctx.Catalog.MaxStack(item);

            foreach (var tier in ranking.Tiers)
            {
                var members = tier.Members.Where(m => m.Chest.Id != exclude).ToList();
                var free = members.ToDictionary(m => m.Chest.Id, m => ctx.World.FreeSpace(m.Chest.Id, item));

                if (tier.Split && members.Count > 1)
                {
                    var progress = true;
                    while (left > 0 && progress)
                    {
                        progress = false;
                        foreach (var m in members)
                        {
                            var take = Min(left, stack, free[m.Chest.Id]);
                            if (take <= 0) continue;
                            Add(m.Chest.Id, take);
                            free[m.Chest.Id] -= take;
                            left -= take;
                            progress = true;
                            if (left == 0) break;
                        }
                    }
                }
                else
                {
                    foreach (var m in members)
                    {
                        var take = Min(left, free[m.Chest.Id], int.MaxValue);
                        if (take <= 0) continue;
                        Add(m.Chest.Id, take);
                        left -= take;
                        if (left == 0) break;
                    }
                }
                if (left == 0) break;
            }

            return order.Select(id => new PlannedMove(id, moves[id])).ToList();

            void Add(ContainerId id, int n)
            {
                if (!moves.ContainsKey(id)) { moves[id] = 0; order.Add(id); }
                moves[id] += n;
            }
        }

        /// <summary>
        /// Pull order for an item (spec 02, "Reverse Mass Deposit priority"): chests that are not a
        /// home for it at all first, then homes from worst to best. Skip chests and special-purpose
        /// chests other than overflow and spoils are never sources.
        /// </summary>
        public static List<ContainerId> ReverseOrder(DepositContext ctx, ItemId item)
        {
            var ranked = DepositRanker.Rank(ctx, item).InOrder.Select(c => c.Chest.Id).ToList();
            var rankedSet = new HashSet<ContainerId>(ranked);

            var holders = World.Scope.Chests(ctx.World, ctx.Scope)
                .Where(c => c.Deposit.Special is SpecialKind.None or SpecialKind.Overflow or SpecialKind.Spoils)
                .Where(c => ctx.World.Count(c.Id, item) > 0)
                .Select(c => c.Id)
                .ToList();

            var result = holders.Where(id => !rankedSet.Contains(id)).ToList();
            for (var i = ranked.Count - 1; i >= 0; i--)
                if (holders.Contains(ranked[i])) result.Add(ranked[i]);
            return result;
        }

        static int Min(int a, int b, int c) => a < b ? (a < c ? a : c) : (b < c ? b : c);
    }
}
