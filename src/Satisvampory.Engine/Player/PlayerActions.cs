// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.World;

namespace Satisvampory.Engine.Player
{
    public sealed record ActionMove(ContainerId From, ContainerId To, ItemId Item, int Amount);

    /// <summary>What a stash, pull or tidy did, for the notice and its hover breakdown.</summary>
    public sealed class ActionResult
    {
        public List<ActionMove> Moves { get; } = new();

        /// <summary>Amount per item that could not be placed or found.</summary>
        public Dictionary<ItemId, int> Left { get; } = new();

        public int Items => Moves.Sum(m => m.Amount);
        public int Kinds => Moves.Select(m => m.Item).Distinct().Count();
        public int Containers(bool byDestination) => Moves.Select(m => byDestination ? m.To : m.From).Distinct().Count();

        internal void Leave(ItemId item, int amount)
        {
            if (amount > 0) Left[item] = (Left.TryGetValue(item, out var n) ? n : 0) + amount;
        }
    }

    /// <summary>
    /// Quick stash, pull and tidy (spec 07). The player's bag is addressed by a ContainerId the
    /// plugin's adapter maps to the character inventory. Each action plans one item at a time
    /// against the live world, so free space used by one item is seen by the next.
    /// </summary>
    public static class PlayerActions
    {
        // ---------------- quick stash ----------------

        /// <param name="stashable">The bag minus the hotbar, as the adapter reads it.</param>
        public static ActionResult Stash(IWorld world, DepositContext ctx, ContainerId bag, IEnumerable<ItemStack> stashable)
        {
            var result = new ActionResult();
            foreach (var stack in Sum(stashable))
            {
                var left = stack.Amount;
                foreach (var plan in DepositPlanner.Plan(ctx, stack.Item, left))
                {
                    var moved = world.Transfer(bag, InvPart.Main, plan.To, stack.Item, plan.Amount);
                    if (moved > 0) result.Moves.Add(new ActionMove(bag, plan.To, stack.Item, moved));
                    left -= moved;
                }
                result.Leave(stack.Item, left);
            }
            return result;
        }

        // ---------------- pull ----------------

        /// <summary>
        /// Take items into the bag, worst home first. Ignores reserve. With
        /// <paramref name="neverLastStack"/>, each chest keeps one stack of the item.
        /// </summary>
        public static ActionResult Pull(IWorld world, DepositContext ctx, ContainerId bag, ItemId item, int amount, bool neverLastStack)
        {
            var result = new ActionResult();
            PullInto(world, ctx, bag, item, amount, neverLastStack, result);
            return result;
        }

        /// <summary>Pull <paramref name="perItem"/> (default one stack) of every item in an item group.</summary>
        public static ActionResult PullGroup(IWorld world, DepositContext ctx, ContainerId bag, string group, int? perItem, bool neverLastStack)
        {
            var result = new ActionResult();
            foreach (var item in ctx.Catalog.Members(group).OrderBy(i => ctx.Catalog.NameOf(i)))
                PullInto(world, ctx, bag, item, perItem ?? ctx.Catalog.MaxStack(item), neverLastStack, result);
            return result;
        }

        static void PullInto(IWorld world, DepositContext ctx, ContainerId bag, ItemId item, int amount, bool neverLastStack, ActionResult result)
        {
            var left = amount;
            var stack = ctx.Catalog.MaxStack(item);
            foreach (var src in DepositPlanner.ReverseOrder(ctx, item))
            {
                if (left <= 0) break;
                var have = world.Count(src, item);
                var spare = neverLastStack ? have - Math.Min(have, stack) : have;
                var take = Math.Min(left, spare);
                if (take <= 0) continue;
                var moved = world.Transfer(src, InvPart.Main, bag, item, take);
                if (moved > 0) result.Moves.Add(new ActionMove(src, bag, item, moved));
                left -= moved;
                if (moved < take) break; // bag full
            }
            result.Leave(item, left);
        }

        // ---------------- tidy ----------------

        /// <summary>
        /// Move items already in chests to strictly better homes. Ignores reserve.
        /// Sources: ordinary chests, overflow and spoils. Never hearts, skip chests, special-purpose
        /// chests, or anything in a list or chest group. Dests: ordinary chests; a list or group
        /// member only when it is within the item's top two rows; never overflow or spoils.
        /// </summary>
        public static ActionResult Tidy(IWorld world, DepositContext ctx, LogisticsGraph graph)
        {
            var result = new ActionResult();
            var wired = Wired(graph);
            var chests = Scope.Chests(world, ctx.Scope).ToList();

            bool IsSource(ContainerInfo c) =>
                !wired.Contains(c.Id) && c.Deposit.Special is SpecialKind.None or SpecialKind.Overflow or SpecialKind.Spoils;

            bool IsDest(DepositCandidate c) =>
                c.Chest.Deposit.Special == SpecialKind.None && (!wired.Contains(c.Chest.Id) || c.RowIndex <= 1);

            var sources = chests.Where(IsSource).ToList();
            var items = sources.SelectMany(c => world.Contents(c.Id)).Select(s => s.Item).Distinct().ToList();

            foreach (var item in items)
            {
                var order = DepositRanker.Rank(ctx, item).InOrder.ToList();
                var position = new Dictionary<ContainerId, int>();
                for (var i = 0; i < order.Count; i++) position[order[i].Chest.Id] = i;

                // Worst-placed sources first, so the best homes fill from the strays.
                foreach (var src in sources.OrderByDescending(s => position.TryGetValue(s.Id, out var p) ? p : int.MaxValue))
                {
                    var have = world.Count(src.Id, item);
                    if (have <= 0) continue;
                    var srcPos = position.TryGetValue(src.Id, out var sp) ? sp : int.MaxValue;

                    for (var i = 0; i < order.Count && i < srcPos && have > 0; i++)
                    {
                        var dest = order[i];
                        if (dest.Chest.Id == src.Id || !IsDest(dest)) continue;
                        var room = world.FreeSpace(dest.Chest.Id, item);
                        if (room <= 0) continue;
                        var moved = world.Transfer(src.Id, InvPart.Main, dest.Chest.Id, item, Math.Min(room, have));
                        if (moved > 0) result.Moves.Add(new ActionMove(src.Id, dest.Chest.Id, item, moved));
                        have -= moved;
                    }
                }
            }
            return result;
        }

        static HashSet<ContainerId> Wired(LogisticsGraph graph)
        {
            var set = new HashSet<ContainerId>();
            foreach (var e in graph.Edges())
            {
                set.Add(e.From);
                set.Add(e.To);
            }
            foreach (var (id, lists) in graph.Lists)
                if (lists.Input.Count > 0 || lists.Output.Count > 0) set.Add(id);
            foreach (var g in graph.Groups.Values)
                foreach (var m in g.Members) set.Add(m);
            return set;
        }

        static IEnumerable<ItemStack> Sum(IEnumerable<ItemStack> stacks) =>
            stacks.GroupBy(s => s.Item).Select(g => new ItemStack(g.Key, g.Sum(s => s.Amount)));
    }
}
