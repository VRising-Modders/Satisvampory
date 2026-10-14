// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.World;

namespace Satisvampory.Engine.Deposit
{
    /// <summary>Everything one ranking needs. Built per request by the caller.</summary>
    public sealed class DepositContext
    {
        public DepositContext(ItemCatalog catalog, IWorldView world, IReadOnlyList<PlotId> scope, PlotId standing)
        {
            Catalog = catalog;
            World = world;
            Scope = scope;
            Standing = standing;
        }

        public ItemCatalog Catalog { get; }
        public IWorldView World { get; }
        public IReadOnlyList<PlotId> Scope { get; }
        public PlotId Standing { get; }

        /// <summary>
        /// Island settings when sharing is on (the clan's), else the standing castle's. One set per
        /// ranking, so every chest in scope is ordered by the same list.
        /// </summary>
        public MassDepositSettings Settings { get; init; } = new();

        public CompiledScript Script { get; init; } = CompiledScript.Empty;

        /// <summary>Belts by folded name, members in group order.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<ContainerId>> Belts { get; init; } =
            new Dictionary<string, IReadOnlyList<ContainerId>>();

        public Func<PlotId, string> PlotLabel { get; init; } = p => p.ToString();

        /// <summary>Player position for distance tie-breaks, if known.</summary>
        public (float X, float Z)? Player { get; init; }
    }

    public sealed class DepositCandidate
    {
        public DepositCandidate(ContainerInfo chest, int rowIndex, string row, int priority, bool holds)
        {
            Chest = chest;
            RowIndex = rowIndex;
            Row = row;
            Priority = priority;
            Holds = holds;
        }

        public ContainerInfo Chest { get; }
        public int RowIndex { get; }
        /// <summary>Display name of the row that placed it, for the preview ("Exact names").</summary>
        public string Row { get; }
        public int Priority { get; }
        public bool Holds { get; }
        /// <summary>Line of the Advanced rule that decided, or 0.</summary>
        public int RuleLine { get; internal set; }
    }

    public sealed class DepositTier
    {
        public List<DepositCandidate> Members { get; } = new();

        /// <summary>Equal-priority priority chests split deposits; other tiers fill in order.</summary>
        public bool Split { get; init; }
    }

    public sealed class DepositRanking
    {
        public ItemId Item { get; init; }
        public List<DepositTier> Tiers { get; } = new();

        public IEnumerable<DepositCandidate> InOrder => Tiers.SelectMany(t => t.Members);

        public DepositCandidate? Best => InOrder.FirstOrDefault();
    }

    /// <summary>The one deposit order (spec 03). Quick stash, tidy, servant loot, leftovers and Mass Deposit outputs all call this.</summary>
    public static class DepositRanker
    {
        sealed class EffRow
        {
            public BuiltInRow? BuiltIn;
            public Target? Target;
            public int RuleLine;

            public string Label => BuiltIn is { } b ? AdvancedScript.RowDisplayName(b) : Target!.ToString();

            /// <summary>The built-in rank this row stands for; a general or overflow target counts as that rank.</summary>
            public BuiltInRow? Rank => BuiltIn ?? Target?.Kind switch
            {
                TargetKind.General => BuiltInRow.General,
                TargetKind.Overflow => BuiltInRow.Overflow,
                _ => null,
            };
        }

        public static DepositRanking Rank(DepositContext ctx, ItemId item)
        {
            var catalog = ctx.Catalog;
            var rows = new List<EffRow>();
            foreach (var (sel, target) in ctx.Settings.ItemRules)
                if (sel.Matches(item, catalog)) rows.Add(new EffRow { Target = target });
            foreach (var r in ctx.Settings.Rows)
                if (r.Enabled) rows.Add(new EffRow { BuiltIn = r.BuiltIn, Target = r.Target });

            var nevers = new List<(Target Target, BuiltInRow? When, int Line)>();
            string? preferPlot = null;
            var preferHere = false;
            var firstInsert = ctx.Settings.ItemRules.Count(r => r.Selector.Matches(item, catalog));

            foreach (var rule in ctx.Script.Rules)
            {
                if (!rule.Selector.Matches(item, catalog)) continue;
                foreach (var op in rule.Ops)
                {
                    switch (op.Kind)
                    {
                        case RuleOpKind.First:
                            foreach (var t in op.Targets)
                                rows.Insert(firstInsert++, new EffRow { Target = t, RuleLine = op.Line });
                            break;
                        case RuleOpKind.Only:
                            rows = op.Targets.Select(t => new EffRow { Target = t, RuleLine = op.Line }).ToList();
                            firstInsert = rows.Count;
                            break;
                        case RuleOpKind.Never:
                            nevers.Add((op.Targets[0], op.WhenRow, op.Line));
                            break;
                        case RuleOpKind.Move:
                        {
                            var from = rows.FindIndex(r => r.BuiltIn == op.MoveRow);
                            if (from < 0 || rows.FindIndex(r => r.BuiltIn == op.MoveAnchor) < 0) break;
                            var moving = rows[from];
                            rows.RemoveAt(from);
                            var anchor = rows.FindIndex(r => r.BuiltIn == op.MoveAnchor);
                            rows.Insert(op.MoveAbove ? anchor : anchor + 1, moving);
                            moving.RuleLine = op.Line;
                            break;
                        }
                        case RuleOpKind.Prefer:
                            if (op.Plot == null) preferHere = true;
                            else preferPlot = op.Plot;
                            break;
                    }
                }
            }

            var candidates = new List<DepositCandidate>();
            foreach (var chest in Scope.Chests(ctx.World, ctx.Scope))
            {
                var kind = chest.Deposit.Special;
                if (kind == SpecialKind.Skip) continue;
                if (NameMatch.IsBanned(chest, item, catalog)) continue;

                var match = NameMatch.Classify(chest, item, catalog);
                var holds = ctx.World.Count(chest.Id, item) > 0;
                var priority = NameMatch.EffectivePriority(chest, item, catalog);

                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (!Qualifies(ctx, row, chest, match, holds, priority)) continue;
                    if (nevers.Any(n => (n.When == null || n.When == row.Rank) && TargetMatches(ctx, n.Target, chest)))
                    {
                        if (nevers.Any(n => n.When == null && TargetMatches(ctx, n.Target, chest))) break;
                        continue;
                    }
                    candidates.Add(new DepositCandidate(chest, i, row.Label, priority, holds) { RuleLine = row.RuleLine });
                    break;
                }
            }

            int PlotRank(ContainerInfo c)
            {
                if (preferPlot != null && NameFold.Fold(ctx.PlotLabel(c.Plot)) == NameFold.Fold(preferPlot)) return 0;
                if (c.Plot == ctx.Standing) return preferHere || preferPlot == null ? 0 : 1;
                return 2;
            }

            float Distance(ContainerInfo c) =>
                ctx.Player is { } p ? (c.X - p.X) * (c.X - p.X) + (c.Z - p.Z) * (c.Z - p.Z) : 0f;

            var ordered = candidates
                .OrderBy(c => c.RowIndex)
                .ThenByDescending(c => c.Priority)
                .ThenBy(c => c.Holds ? 0 : 1)
                .ThenBy(c => PlotRank(c.Chest))
                .ThenBy(c => Distance(c.Chest))
                .ThenBy(c => c.Chest.Id.Value)
                .ToList();

            var ranking = new DepositRanking { Item = item };
            DepositTier? tier = null;
            DepositCandidate? prev = null;
            foreach (var c in ordered)
            {
                var split = rows[c.RowIndex].BuiltIn == BuiltInRow.PriorityChests;
                if (tier == null || prev!.RowIndex != c.RowIndex || prev.Priority != c.Priority || !split)
                {
                    tier = new DepositTier { Split = split };
                    ranking.Tiers.Add(tier);
                }
                tier.Members.Add(c);
                prev = c;
            }
            return ranking;
        }

        static bool Qualifies(DepositContext ctx, EffRow row, ContainerInfo chest, NameMatchKind match, bool holds, int priority)
        {
            if (row.Target is { } t) return TargetMatches(ctx, t, chest);

            var kind = chest.Deposit.Special;
            // Special-purpose chests are homes only when a row or rule names them.
            if (SpecialKinds.IsSpecialPurpose(kind)) return false;
            if (kind == SpecialKind.Overflow) return row.BuiltIn == BuiltInRow.Overflow;

            return row.BuiltIn switch
            {
                BuiltInRow.PriorityChests => priority > 0 && (match != NameMatchKind.Custom || holds),
                BuiltInRow.AlreadyHolding => holds && match is NameMatchKind.Exact or NameMatchKind.Category,
                BuiltInRow.ExactNames => match == NameMatchKind.Exact,
                BuiltInRow.CategoryNames => match == NameMatchKind.Category,
                BuiltInRow.General => match == NameMatchKind.Generic,
                BuiltInRow.HoldingAnyName => match == NameMatchKind.Custom && holds,
                _ => false,
            };
        }

        internal static bool TargetMatches(DepositContext ctx, Target t, ContainerInfo chest)
        {
            switch (t.Kind)
            {
                case TargetKind.Chest:
                    return NameFold.Fold(chest.DisplayName) == NameFold.Fold(t.Name)
                        && (t.Plot == null || NameFold.Fold(ctx.PlotLabel(chest.Plot)) == NameFold.Fold(t.Plot));
                case TargetKind.Belt:
                    return ctx.Belts.TryGetValue(NameFold.Fold(t.Name), out var members) && members.Contains(chest.Id);
                case TargetKind.Plot:
                    return NameFold.Fold(ctx.PlotLabel(chest.Plot)) == NameFold.Fold(t.Name);
                case TargetKind.Overflow:
                    return chest.Deposit.Special == SpecialKind.Overflow;
                case TargetKind.Special:
                    return chest.Deposit.Special == t.Special;
                case TargetKind.General:
                    return chest.Deposit.Special == SpecialKind.None
                        && NameMatch.Classify(chest, default, ctx.Catalog) == NameMatchKind.Generic;
                default:
                    return false;
            }
        }
    }
}
