// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.World;

namespace Satisvampory.Engine.Ui
{
    public sealed record EvalStep(ContainerId Id, string Name, string Plot, int Amount, string Why);

    public sealed record EvalSkip(ContainerId Id, string Name, string Plot, string Reason);

    /// <summary>The evaluator's answer to "what if n of X" (spec 03, "Evaluator").</summary>
    public sealed class Evaluation
    {
        public ItemId Item { get; init; }
        public int Asked { get; init; }
        public List<EvalStep> Steps { get; } = new();
        public List<EvalSkip> NotUsed { get; } = new();
        public int Covered => Steps.Sum(s => s.Amount);
        public int Short => Math.Max(0, Asked - Covered);
    }

    /// <summary>
    /// Dry runs. Nothing moves: each answer is computed from the current contents and settings,
    /// the same way the real action would, and lists every chest that would not be used with why.
    /// </summary>
    public static class Evaluator
    {
        // ---------------- push: where would n of X go ----------------

        public static Evaluation Push(DepositContext ctx, ItemId item, int amount, ContainerId? from = null)
        {
            var eval = new Evaluation { Item = item, Asked = amount };
            var ranking = DepositRanker.Rank(ctx, item).InOrder.ToList();
            var byId = ranking.ToDictionary(c => c.Chest.Id);
            var plan = DepositPlanner.Plan(ctx, item, amount, from);
            var used = new HashSet<ContainerId>();

            foreach (var m in plan)
            {
                var c = byId[m.To];
                eval.Steps.Add(new EvalStep(m.To, c.Chest.DisplayName, Label(ctx, c.Chest.Plot), m.Amount, c.Why));
                used.Add(m.To);
            }

            foreach (var chest in Scope.Chests(ctx.World, ctx.Scope))
            {
                if (used.Contains(chest.Id) || chest.Id == from) continue;
                string reason;
                if (byId.TryGetValue(chest.Id, out var cand))
                    reason = ctx.World.FreeSpace(chest.Id, item) <= 0 ? "No free space" : "Not needed: better homes took it all";
                else
                    reason = WhyNotHome(ctx, chest, item);
                eval.NotUsed.Add(new EvalSkip(chest.Id, chest.DisplayName, Label(ctx, chest.Plot), reason));
            }
            return eval;
        }

        static string WhyNotHome(DepositContext ctx, ContainerInfo chest, ItemId item)
        {
            var o = chest.Deposit;
            if (o.Special == SpecialKind.Skip) return "Skip chest";
            if (o.BannedItems.Contains(item)) return $"Bans {ctx.Catalog.NameOf(item)}";
            foreach (var c in o.BannedCategories)
                if (ctx.Catalog.InCategory(item, c)) return $"Bans category {c}";
            if (SpecialKinds.IsSpecialPurpose(o.Special)) return $"{o.Special} chest: used only when an override or rule names it";
            if (o.Takes == Takes.Nothing) return "Takes nothing";
            if (o.Takes == Takes.OnlyListed && DepositScore.Entry(chest, item, ctx.Catalog) == null) return "Takes only listed items, and this is not one";
            return "Removed by a Mass Deposit rule";
        }

        // ---------------- pull: where would n of X come from ----------------

        public sealed class PullOptions
        {
            /// <summary>Leave this much on the island (crafting, stations); 0 for a player's pull.</summary>
            public int Reserve { get; init; }
            public bool NeverLastStack { get; init; }
            /// <summary>An Input list's own sources, in its order; null means the island in reverse Mass Deposit order.</summary>
            public IReadOnlyList<ContainerId>? Sources { get; init; }
        }

        public static Evaluation Pull(DepositContext ctx, ItemId item, int amount, PullOptions? options = null)
        {
            options ??= new PullOptions();
            var eval = new Evaluation { Item = item, Asked = amount };
            var order = options.Sources?.ToList() ?? DepositPlanner.ReverseOrder(ctx, item);
            var homes = DepositRanker.Rank(ctx, item).InOrder.ToDictionary(c => c.Chest.Id);
            var stack = ctx.Catalog.MaxStack(item);
            var islandTotal = Scope.Chests(ctx.World, ctx.Scope).Sum(c => (long)ctx.World.Count(c.Id, item));
            var spare = Math.Max(0, islandTotal - options.Reserve);
            var left = (long)Math.Min(amount, spare);
            var used = new HashSet<ContainerId>();
            var reasons = new Dictionary<ContainerId, string>();

            foreach (var id in order)
            {
                var chest = ctx.World.Find(id);
                if (chest == null) continue;
                var have = ctx.World.Count(id, item, chest.Kind == NodeKind.Station ? InvPart.Output : InvPart.Main);
                if (have <= 0) { reasons[id] = "Holds none"; continue; }
                if (left <= 0) { reasons[id] = options.Reserve > 0 && amount > spare ? $"Kept as reserve ({options.Reserve} on the island)" : "Not needed: earlier sources covered it"; continue; }
                var canGive = options.NeverLastStack ? have - Math.Min(have, stack) : have;
                if (canGive <= 0) { reasons[id] = "Keeps its last stack"; continue; }
                var take = (int)Math.Min(left, canGive);
                var why = homes.TryGetValue(id, out var h) ? (h.IsOverflow ? "Overflow, drained first" : $"Home rank {h.Tier + 1}: {h.Why}") : "Not a home for it, drained first";
                eval.Steps.Add(new EvalStep(id, chest.DisplayName, Label(ctx, chest.Plot), take, why));
                used.Add(id);
                left -= take;
            }

            foreach (var chest in Scope.Chests(ctx.World, ctx.Scope))
            {
                if (used.Contains(chest.Id)) continue;
                string reason;
                if (reasons.TryGetValue(chest.Id, out var r)) reason = r;
                else if (chest.Deposit.Special == SpecialKind.Skip) reason = "Skip chest";
                else if (SpecialKinds.IsSpecialPurpose(chest.Deposit.Special)) reason = $"{chest.Deposit.Special} chest: never a source";
                else if (ctx.World.Count(chest.Id, item) <= 0) reason = "Holds none";
                else if (options.Sources != null) reason = "Not in this Input list";
                else reason = "Not needed: earlier sources covered it";
                eval.NotUsed.Add(new EvalSkip(chest.Id, chest.DisplayName, Label(ctx, chest.Plot), reason));
            }
            return eval;
        }

        // ---------------- one container's Output list ----------------

        /// <summary>
        /// Where n of X in <paramref name="owner"/> would go through its Output list, row by row.
        /// Every row target appears in <see cref="Evaluation.Steps"/> in list order, including those
        /// that would get 0, with the reason, so "row 2 gets nothing because Mass Deposit in row 1
        /// took it all" is visible at a glance.
        /// </summary>
        public static Evaluation OutputList(DepositContext ctx, LogisticsGraph graph, ContainerId owner, ItemId item, int amount)
        {
            var eval = new Evaluation { Item = item, Asked = amount };
            var lists = graph.For(owner);
            var left = amount;
            for (var i = 0; i < lists.Output.Count; i++)
            {
                var e = lists.Output[i];
                var row = $"Row {i + 1}";
                if (e.Kind == EntryKind.MassDeposit)
                {
                    if (!e.Filter.Allows(item, ctx.Catalog))
                    {
                        eval.Steps.Add(new EvalStep(default, "Mass Deposit", "", 0, $"{row}: its filter excludes this item"));
                        continue;
                    }
                    if (left <= 0)
                    {
                        eval.Steps.Add(new EvalStep(default, "Mass Deposit", "", 0, $"{row}: earlier rows took it all"));
                        continue;
                    }
                    var push = Push(ctx, item, left, owner);
                    var any = false;
                    foreach (var st in push.Steps.Where(st => st.Id != owner))
                    {
                        eval.Steps.Add(st with { Why = $"{row}, Mass Deposit: {st.Why}" });
                        left -= st.Amount;
                        any = true;
                    }
                    if (!any) eval.Steps.Add(new EvalStep(default, "Mass Deposit", "", 0, $"{row}: no chest on the island takes this item"));
                    continue;
                }

                foreach (var id in graph.Expand(e, output: true, owner: owner))
                {
                    var target = ctx.World.Find(id);
                    if (target == null) continue;
                    string? zero = null;
                    if (!graph.Allows(e, owner, true, id, item, ctx.Catalog)) zero = $"{row}: its filter excludes this item";
                    else if (left <= 0) zero = PreviousTookAll(eval, row);
                    else if (target.Kind == NodeKind.Station && !ctx.World.Recipes(id).Any(r => r.Enabled && r.Inputs.Any(x => x.Item == item)))
                        zero = $"{row}: no recipe switched on uses this item";
                    else if (ctx.World.FreeSpace(id, item) <= 0) zero = $"{row}: no free space";

                    if (zero != null)
                    {
                        eval.Steps.Add(new EvalStep(id, target.DisplayName, Label(ctx, target.Plot), 0, zero));
                        continue;
                    }
                    var n = Math.Min(left, ctx.World.FreeSpace(id, item));
                    eval.Steps.Add(new EvalStep(id, target.DisplayName, Label(ctx, target.Plot), n, row));
                    left -= n;
                }
            }
            return eval;
        }

        /// <summary>"Mass Deposit in row 1 took it all" when an earlier Mass Deposit row did, else plain.</summary>
        static string PreviousTookAll(Evaluation eval, string row)
        {
            var md = eval.Steps.LastOrDefault(st => st.Amount > 0 && st.Why.Contains(", Mass Deposit:"));
            if (md != null)
            {
                var earlier = md.Why.Substring(0, md.Why.IndexOf(','));
                return $"{row}: Mass Deposit in {earlier.ToLowerInvariant()} took it all";
            }
            return $"{row}: earlier rows took it all";
        }


        static EvalSkip Skip(DepositContext ctx, ContainerId id, string reason)
        {
            var c = ctx.World.Find(id);
            return new EvalSkip(id, c?.DisplayName ?? "(gone)", c == null ? "" : Label(ctx, c.Plot), reason);
        }

        static string Label(DepositContext ctx, PlotId plot) => plot == ctx.Standing ? "here" : ctx.PlotLabel(plot);
    }
}
