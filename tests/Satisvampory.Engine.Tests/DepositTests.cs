// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Model;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    /// <summary>Deposit-settings helpers for tests.</summary>
    static class Homes
    {
        public static ContainerInfo OnlyItem(this ContainerInfo c, ItemId item, int adjust = 0)
        {
            c.Deposit.Takes = Takes.OnlyListed;
            c.Deposit.ItemPriority[item] = adjust;
            return c;
        }

        public static ContainerInfo OnlyCategory(this ContainerInfo c, string category, int adjust = 0)
        {
            c.Deposit.Takes = Takes.OnlyListed;
            c.Deposit.CategoryPriority[category] = adjust;
            return c;
        }

        public static ContainerInfo Base(this ContainerInfo c, int priority)
        {
            c.Deposit.BasePriority = priority;
            return c;
        }

        public static ContainerInfo Kind(this ContainerInfo c, SpecialKind kind)
        {
            c.Deposit.Special = kind;
            return c;
        }
    }

    public class DepositTests
    {
        static readonly PlotId Home = new(1);
        static readonly PlotId Other = new(2);

        readonly ItemCatalog _cat = Catalog();
        readonly FakeWorld _w = new();

        DepositContext Ctx(MassDepositSettings? s = null, CompiledScript? script = null, IReadOnlyDictionary<string, IReadOnlyList<ContainerId>>? belts = null) =>
            new(_cat, _w, new[] { Home, Other }, Home)
            {
                Settings = s ?? new MassDepositSettings(),
                Script = script ?? CompiledScript.Empty,
                Belts = belts ?? new Dictionary<string, IReadOnlyList<ContainerId>>(),
                PlotLabel = p => p == Home ? "Hollow Keep" : "Plot 2",
            };

        [Fact]
        public void Names_play_no_part_only_settings_do()
        {
            var named = _w.Chest(Home, "Iron Ore").OnlyItem(Plank);
            var listed = _w.Chest(Home, "misc").OnlyItem(IronOre);
            var ranking = DepositRanker.Rank(Ctx(), IronOre).InOrder.Select(c => c.Chest).ToList();
            Assert.Equal(listed, ranking[0]);
            Assert.DoesNotContain(named, ranking);
        }

        [Fact]
        public void Higher_priority_wins_then_holding_then_here_and_overflow_is_last()
        {
            var overflow = _w.Chest(Home, "o").Kind(SpecialKind.Overflow);
            var general = _w.Chest(Home, "g");
            var ores = _w.Chest(Home, "x").OnlyCategory("ore", 3);
            var iron = _w.Chest(Home, "y").OnlyItem(IronOre, 1).Base(1);
            var holding = _w.Chest(Other, "z").OnlyItem(IronOre, 1).Base(1);
            _w.Put(holding, IronOre, 5);
            var there = _w.Chest(Other, "w").OnlyItem(IronOre, 1).Base(1);

            var order = DepositRanker.Rank(Ctx(), IronOre).InOrder.Select(c => c.Chest).ToList();
            Assert.Equal(new[] { ores, holding, iron, there, general, overflow }, order);
        }

        [Fact]
        public void Item_entry_wins_over_category_entry_and_bans_remove_a_home()
        {
            var a = _w.Chest(Home, "a").OnlyCategory("ore", 5).OnlyItem(IronOre, -1);
            Assert.Equal(-1, DepositScore.Score(a, IronOre, _cat));
            Assert.Equal(5, DepositScore.Score(a, CopperOre, _cat));
            Assert.Equal("Iron Ore −1 on base 0", DepositScore.Explain(a, IronOre, _cat));

            var b = _w.Chest(Home, "b");
            b.Deposit.BannedCategories.Add("ore");
            Assert.DoesNotContain(DepositRanker.Rank(Ctx(), IronOre).InOrder, c => c.Chest == b);

            var none = _w.Chest(Home, "n");
            none.Deposit.Takes = Takes.Nothing;
            Assert.DoesNotContain(DepositRanker.Rank(Ctx(), Coal).InOrder, c => c.Chest == none);
        }

        [Fact]
        public void Skip_and_special_purpose_chests_are_homes_only_when_named()
        {
            _w.Chest(Home, "s").Kind(SpecialKind.Skip);
            var trash = _w.Chest(Home, "t").Kind(SpecialKind.Trash);
            Assert.Empty(DepositRanker.Rank(Ctx(), Sword).InOrder);

            var s = new MassDepositSettings();
            s.ItemRules.Add((new ItemSelector(ItemSelectorKind.Category, Category: "weapons"), new Target(TargetKind.Special, Special: SpecialKind.Trash)));
            var best = DepositRanker.Rank(Ctx(s), Sword).Best!;
            Assert.Equal(trash, best.Chest);
            Assert.Equal("Mass Deposit override", best.Why);
        }

        [Fact]
        public void Planner_splits_equal_listed_priority_and_respects_free_space()
        {
            var a = _w.Chest(Home, "a", capacity: 1000).OnlyCategory("ore", 1);
            var b = _w.Chest(Home, "b", capacity: 1000).OnlyCategory("ore", 1);
            var small = _w.Chest(Home, "c", capacity: 50).OnlyItem(IronOre);

            var plan = DepositPlanner.Plan(Ctx(), IronOre, 400);
            Assert.Equal(200, plan.Single(m => m.To == a.Id).Amount);
            Assert.Equal(200, plan.Single(m => m.To == b.Id).Amount);
            Assert.DoesNotContain(plan, m => m.To == small.Id);

            var overflowPlan = DepositPlanner.Plan(Ctx(), IronOre, 2030);
            Assert.Equal(30, overflowPlan.Single(m => m.To == small.Id).Amount);
        }

        [Fact]
        public void Treasury_refusing_blood_essence_is_skipped_by_free_space()
        {
            var treasury = _w.Chest(Home, "t").OnlyCategory("blood", 5);
            _w.Refuse(treasury, BloodEssence);
            var general = _w.Chest(Home, "g");
            Assert.Equal(general.Id, DepositPlanner.Plan(Ctx(), BloodEssence, 100).Single().To);
        }

        [Fact]
        public void Reverse_order_drains_non_homes_then_worst_home_first()
        {
            var best = _w.Chest(Home, "a").OnlyItem(IronOre, 5);
            var general = _w.Chest(Home, "b");
            var stray = _w.Chest(Home, "c").OnlyItem(Plank);
            var skip = _w.Chest(Home, "d").Kind(SpecialKind.Skip);
            foreach (var c in new[] { best, general, stray, skip }) _w.Put(c, IronOre, 10);
            Assert.Equal(new[] { stray.Id, general.Id, best.Id }, DepositPlanner.ReverseOrder(Ctx(), IronOre));
        }

        [Fact]
        public void Advanced_only_never_and_pinned_rule_lines()
        {
            var vault = _w.Chest(Home, "Gem vault").OnlyItem(Plank);
            var gems = _w.Chest(Home, "g1").OnlyCategory("gems", 9);
            var general = _w.Chest(Home, "g2");
            var generalThere = _w.Chest(Other, "g3");

            var script = AdvancedScript.Compile(@"
                # gems to the vault first
                for category gems: first chest ""Gem vault""
                for ""Coal"": only general, then overflow
                for ""Coal"": never plot ""Plot 2""
            ", _cat);
            Assert.True(script.Ok, string.Join("; ", script.Messages.Select(m => $"{m.Line}: {m.Text}")));

            var ruby = DepositRanker.Rank(Ctx(script: script), Ruby).InOrder.ToList();
            Assert.Equal(vault, ruby[0].Chest);
            Assert.Equal("Rule on line 3", ruby[0].Why);
            Assert.Equal(gems, ruby[1].Chest);

            var coal = DepositRanker.Rank(Ctx(script: script), Coal).InOrder.Select(c => c.Chest).ToList();
            Assert.Equal(new[] { general }, coal);
            Assert.DoesNotContain(generalThere, coal);
        }

        [Fact]
        public void Belt_targets_resolve_through_the_belt_map()
        {
            var a = _w.Chest(Home, "a").OnlyItem(Plank);
            var belts = new Dictionary<string, IReadOnlyList<ContainerId>> { ["vault"] = new[] { a.Id } };
            var s = new MassDepositSettings();
            s.ItemRules.Add((new ItemSelector(ItemSelectorKind.Item, Ruby), new Target(TargetKind.Belt, "Vault")));
            Assert.Equal(a, DepositRanker.Rank(Ctx(s, belts: belts), Ruby).Best!.Chest);
        }

        [Theory]
        [InlineData("first chest \"A\"", 1, "must start with")]
        [InlineData("for \"Unobtainium\": first general", 1, "Unknown item")]
        [InlineData("for any: launch general", 1, "Unknown clause")]
        [InlineData("for any: never chest \"Nope\"", 1, "No chest named")]
        [InlineData("for any: first general\n  then \"oops", 2, "Unclosed quote")]
        [InlineData("for any: move \"a\" above \"b\"", 1, "Unknown clause")]
        public void Advanced_reports_errors_with_line_numbers(string source, int line, string text)
        {
            var names = new ScriptNames { ChestNames = new HashSet<string> { "a" } };
            var result = AdvancedScript.Compile(source, _cat, names);
            Assert.False(result.Ok);
            var err = result.Messages.First(m => m.IsError);
            Assert.Equal(line, err.Line);
            Assert.Contains(text, err.Text);
        }
    }
}
