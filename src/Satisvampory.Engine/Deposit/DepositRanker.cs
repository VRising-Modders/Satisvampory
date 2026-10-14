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

        /// <summary>The island's Mass Deposit overrides (spec 03).</summary>
        public MassDepositSettings Settings { get; init; } = new();

        public CompiledScript Script { get; init; } = CompiledScript.Empty;

        /// <summary>Belts by folded name, members in belt order.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<ContainerId>> Belts { get; init; } =
            new Dictionary<string, IReadOnlyList<ContainerId>>();

        public Func<PlotId, string> PlotLabel { get; init; } = p => p.ToString();

        /// <summary>Player position for distance tie-breaks, if known.</summary>
        public (float X, float Z)? Player { get; init; }
    }

    public sealed class DepositCandidate
    {
        public DepositCandidate(ContainerInfo chest, int score, bool holds, string why)
        {
            Chest = chest;
            Score = score;
            Holds = holds;
            Why = why;
        }

        public ContainerInfo Chest { get; }

        /// <summary>The chest's priority for this item: base plus the applying entry.</summary>
        public int Score { get; }

        public bool Holds { get; }

        /// <summary>Plain words for the preview: "Category ore +3 on base 2", "Rule on line 4", "Overflow".</summary>
        public string Why { get; internal set; }

        /// <summary>Line of the Advanced rule that placed it, or 0.</summary>
        public int RuleLine { get; internal set; }

        /// <summary>Position among override targets (0 = first); int.MaxValue when not pinned.</summary>
        public int Pinned { get; internal set; } = int.MaxValue;

        public bool IsOverflow => Chest.Deposit.Special == SpecialKind.Overflow && Pinned == int.MaxValue;

        /// <summary>Index of this candidate's tier in the ranking; 0 is the best home.</summary>
        public int Tier { get; internal set; }
    }

    public sealed class DepositTier
    {
        public List<DepositCandidate> Members { get; } = new();

        /// <summary>Chests listing the item at the same positive priority split deposits; other tiers fill in order.</summary>
        public bool Split { get; init; }
    }

    public sealed class DepositRanking
    {
        public ItemId Item { get; init; }
        public List<DepositTier> Tiers { get; } = new();

        public IEnumerable<DepositCandidate> InOrder => Tiers.SelectMany(t => t.Members);

        public DepositCandidate? Best => InOrder.FirstOrDefault();
    }

    /// <summary>
    /// The one deposit order (spec 03). Each chest's deposit settings give it a priority for an
    /// item; overrides and rules can pin targets first, restrict to targets, or exclude them.
    /// Overflow chests are always the last resort. Chest names play no part.
    /// </summary>
    public static class DepositRanker
    {
        public static DepositRanking Rank(DepositContext ctx, ItemId item)
        {
            var catalog = ctx.Catalog;

            var pinned = new List<(Target Target, int Line)>();
            foreach (var (sel, target) in ctx.Settings.ItemRules)
                if (sel.Matches(item, catalog)) pinned.Add((target, 0));

            List<(Target Target, int Line)>? only = null;
            var nevers = new List<Target>();
            string? preferPlot = null;
            var preferHere = false;

            foreach (var rule in ctx.Script.Rules)
            {
                if (!rule.Selector.Matches(item, catalog)) continue;
                foreach (var op in rule.Ops)
                {
                    switch (op.Kind)
                    {
                        case RuleOpKind.First:
                            pinned.AddRange(op.Targets.Select(t => (t, op.Line)));
                            break;
                        case RuleOpKind.Only:
                            only = op.Targets.Select(t => (t, op.Line)).ToList();
                            break;
                        case RuleOpKind.Never:
                            nevers.AddRange(op.Targets);
                            break;
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
                if (DepositScore.IsBanned(chest, item, catalog)) continue;
                if (nevers.Any(t => TargetMatches(ctx, t, chest))) continue;

                var holds = ctx.World.Count(chest.Id, item) > 0;

                if (only != null)
                {
                    // An "only" rule names the homes outright, in its order.
                    var oi = IndexOf(only, ctx, chest);
                    if (oi < 0) continue;
                    candidates.Add(new DepositCandidate(chest, 0, holds, $"Rule on line {only[oi].Line}") { Pinned = oi, RuleLine = only[oi].Line });
                    continue;
                }

                var pi = IndexOf(pinned, ctx, chest);
                if (pi >= 0)
                {
                    var line = pinned[pi].Line;
                    candidates.Add(new DepositCandidate(chest, DepositScore.Score(chest, item, catalog), holds,
                        line > 0 ? $"Rule on line {line}" : "Mass Deposit override") { Pinned = pi, RuleLine = line });
                    continue;
                }

                // Special-purpose chests are homes only when an override or rule names them.
                if (SpecialKinds.IsSpecialPurpose(kind)) continue;
                if (kind == SpecialKind.Overflow)
                {
                    candidates.Add(new DepositCandidate(chest, 0, holds, "Overflow"));
                    continue;
                }
                if (!DepositScore.Takes(chest, item, catalog)) continue;
                candidates.Add(new DepositCandidate(chest, DepositScore.Score(chest, item, catalog), holds, DepositScore.Explain(chest, item, catalog)));
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
                .OrderBy(c => c.Pinned)
                .ThenBy(c => c.IsOverflow ? 1 : 0)
                .ThenByDescending(c => c.Score)
                .ThenBy(c => c.Holds ? 0 : 1)
                .ThenBy(c => PlotRank(c.Chest))
                .ThenBy(c => Distance(c.Chest))
                .ThenBy(c => c.Chest.Id.Value)
                .ToList();

            bool Splits(DepositCandidate c) =>
                c.Pinned == int.MaxValue && !c.IsOverflow && c.Score > 0 && DepositScore.Entry(c.Chest, item, catalog) != null;

            var ranking = new DepositRanking { Item = item };
            DepositTier? tier = null;
            DepositCandidate? prev = null;
            foreach (var c in ordered)
            {
                var split = Splits(c);
                var join = tier != null && split && prev != null && Splits(prev) && prev.Score == c.Score;
                if (!join)
                {
                    tier = new DepositTier { Split = split };
                    ranking.Tiers.Add(tier);
                }
                c.Tier = ranking.Tiers.Count - 1;
                tier!.Members.Add(c);
                prev = c;
            }
            return ranking;
        }

        static int IndexOf(List<(Target Target, int Line)> targets, DepositContext ctx, ContainerInfo chest)
        {
            for (var i = 0; i < targets.Count; i++)
                if (TargetMatches(ctx, targets[i].Target, chest)) return i;
            return -1;
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
                    return chest.Deposit.Special == SpecialKind.None && chest.Deposit.Takes == Takes.Anything;
                default:
                    return false;
            }
        }
    }
}
