// SPDX-License-Identifier: AGPL-3.0-only
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Model;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class DepositTests
    {
        static readonly PlotId Home = new(1);
        static readonly PlotId Other = new(2);

        readonly ItemCatalog _cat = Catalog();
        readonly FakeWorld _w = new();

        DepositContext Ctx(MassDepositSettings? s = null, CompiledScript? script = null) =>
            new(_cat, _w, new[] { Home, Other }, Home)
            {
                Settings = s ?? new MassDepositSettings(),
                Script = script ?? CompiledScript.Empty,
                PlotLabel = p => p == Home ? "Hollow Keep" : "Plot 2",
            };

        [Theory]
        [InlineData("Iron Ore", NameMatchKind.Exact)]
        [InlineData("iron  ORE", NameMatchKind.Exact)]
        [InlineData("ore", NameMatchKind.ItemGroup)]
        [InlineData("ores", NameMatchKind.ItemGroup)]
        [InlineData("East ore", NameMatchKind.ItemGroup)]
        [InlineData("", NameMatchKind.Generic)]
        [InlineData("misc", NameMatchKind.Generic)]
        [InlineData("Large Chest", NameMatchKind.Generic)]
        [InlineData("Copper Ore", NameMatchKind.Custom)]
        [InlineData("Planks please", NameMatchKind.Custom)]
        public void Classifies_names(string name, NameMatchKind expected)
        {
            var c = _w.Chest(Home, name);
            Assert.Equal(expected, NameMatch.Classify(c, IronOre, _cat));
        }

        [Fact]
        public void Group_words_match_by_membership_not_substring()
        {
            var c = _w.Chest(Home, "blood");
            Assert.Equal(NameMatchKind.Custom, NameMatch.Classify(c, BloodJewel, _cat));
            Assert.Equal(NameMatchKind.ItemGroup, NameMatch.Classify(c, BloodEssence, _cat));
        }

        [Fact]
        public void Default_order_is_exact_then_group_then_general_then_overflow()
        {
            var overflow = _w.Chest(Home, "spill");
            overflow.Deposit.Special = SpecialKind.Overflow;
            var general = _w.Chest(Home, "misc");
            var group = _w.Chest(Home, "ore");
            var exact = _w.Chest(Home, "Iron Ore");
            _w.Chest(Home, "Planks");

            var order = DepositRanker.Rank(Ctx(), IronOre).InOrder.Select(c => c.Chest).ToList();
            Assert.Equal(new[] { exact, group, general, overflow }, order);
        }

        [Fact]
        public void Priority_chest_beats_exact_and_holding_beats_empty()
        {
            var exact = _w.Chest(Home, "Iron Ore");
            var vault = _w.Chest(Home, "ore");
            vault.Deposit.BasePriority = 2;
            var holding = _w.Chest(Home, "ore");
            _w.Put(holding, IronOre, 5);

            var ranking = DepositRanker.Rank(Ctx(), IronOre);
            Assert.Equal(vault, ranking.Best!.Chest);
            Assert.Equal("Priority chests", ranking.Best.Row);
            var rest = ranking.InOrder.Skip(1).Select(c => c.Chest).ToList();
            Assert.Equal(new[] { holding, exact }, rest);
        }

        [Fact]
        public void Item_priority_wins_over_group_priority_and_bans_remove_a_home()
        {
            var a = _w.Chest(Home, "misc");
            a.Deposit.GroupPriority["ore"] = 5;
            a.Deposit.ItemPriority[IronOre] = -1;
            Assert.Equal(-1, NameMatch.EffectivePriority(a, IronOre, _cat));
            Assert.Equal(5, NameMatch.EffectivePriority(a, CopperOre, _cat));

            var b = _w.Chest(Home, "misc");
            b.Deposit.BannedGroups.Add("ore");
            Assert.DoesNotContain(DepositRanker.Rank(Ctx(), IronOre).InOrder, c => c.Chest == b);
        }

        [Fact]
        public void Skip_and_special_purpose_chests_are_not_homes_unless_named()
        {
            var skip = _w.Chest(Home, "");
            skip.Deposit.Special = SpecialKind.Skip;
            var trash = _w.Chest(Home, "");
            trash.Deposit.Special = SpecialKind.Trash;
            Assert.Empty(DepositRanker.Rank(Ctx(), Sword).InOrder);

            var s = new MassDepositSettings();
            s.ItemRules.Add((new ItemSelector(ItemSelectorKind.Group, Group: "weapons"), new Target(TargetKind.Special, Special: SpecialKind.Trash)));
            Assert.Equal(trash, DepositRanker.Rank(Ctx(s), Sword).Best!.Chest);
        }

        [Fact]
        public void Disabled_row_is_skipped()
        {
            _w.Chest(Home, "misc");
            var s = new MassDepositSettings();
            s.Rows.Single(r => r.BuiltIn == BuiltInRow.General).Enabled = false;
            Assert.Empty(DepositRanker.Rank(Ctx(s), Coal).InOrder);
        }

        [Fact]
        public void Planner_splits_equal_priority_chests_and_respects_free_space()
        {
            var a = _w.Chest(Home, "ore", capacity: 1000);
            var b = _w.Chest(Home, "ore", capacity: 1000);
            a.Deposit.BasePriority = b.Deposit.BasePriority = 1;
            var small = _w.Chest(Home, "Iron Ore", capacity: 50);

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
            var treasury = _w.Chest(Home, "blood");
            _w.Refuse(treasury, BloodEssence);
            var general = _w.Chest(Home, "");
            var plan = DepositPlanner.Plan(Ctx(), BloodEssence, 100);
            Assert.Equal(general.Id, plan.Single().To);
        }

        [Fact]
        public void Reverse_order_drains_worst_home_first()
        {
            var exact = _w.Chest(Home, "Iron Ore");
            var general = _w.Chest(Home, "");
            var stray = _w.Chest(Home, "Planks");
            var skip = _w.Chest(Home, "x");
            skip.Deposit.Special = SpecialKind.Skip;
            foreach (var c in new[] { exact, general, stray, skip }) _w.Put(c, IronOre, 10);

            var order = DepositPlanner.ReverseOrder(Ctx(), IronOre);
            Assert.Equal(new[] { stray.Id, general.Id, exact.Id }, order);
        }

        [Fact]
        public void Advanced_first_only_never_move_and_prefer()
        {
            var vault = _w.Chest(Other, "Gem vault");
            var gemChest = _w.Chest(Home, "gems");
            var general = _w.Chest(Home, "");
            var generalThere = _w.Chest(Other, "");

            var script = AdvancedScript.Compile(@"
                # gems to the vault first, never overflow
                for itemgroup gems: first chest ""Gem vault"" @ ""Plot 2""
                for any: never plot ""Plot 2"" when rank = general
                for ""Coal"": only general, then overflow
                for ""Iron Ore"": move ""General chests"" above ""Exact names""
            ", _cat);
            Assert.True(script.Ok, string.Join("; ", script.Messages.Select(m => $"{m.Line}: {m.Text}")));

            var gems = DepositRanker.Rank(Ctx(script: script), Ruby).InOrder.ToList();
            Assert.Equal(vault, gems[0].Chest);
            Assert.Equal(gemChest, gems[1].Chest);
            Assert.True(gems[0].RuleLine > 0);
            Assert.DoesNotContain(gems, c => c.Chest == generalThere);

            var coal = DepositRanker.Rank(Ctx(script: script), Coal).InOrder.Select(c => c.Chest).ToList();
            Assert.Equal(new[] { general }, coal);

            var exact = _w.Chest(Home, "Iron Ore");
            var ore = DepositRanker.Rank(Ctx(script: script), IronOre).InOrder.Select(c => c.Chest).ToList();
            Assert.Equal(general, ore[0]);
            Assert.Equal(exact, ore[1]);
        }

        [Theory]
        [InlineData("first chest \"A\"", 1, "must start with")]
        [InlineData("for \"Unobtainium\": first general", 1, "Unknown item")]
        [InlineData("for any: launch general", 1, "Unknown clause")]
        [InlineData("for any: never chest \"Nope\"", 1, "No chest named")]
        [InlineData("for any: first general\n  then \"oops", 2, "Unclosed quote")]
        [InlineData("for any:\n  move \"Exact names\" sideways \"General\"", 2, "above")]
        public void Advanced_reports_errors_with_line_numbers(string source, int line, string text)
        {
            var names = new ScriptNames { ChestNames = new System.Collections.Generic.HashSet<string> { "a" } };
            var result = AdvancedScript.Compile(source, _cat, names);
            Assert.False(result.Ok);
            var err = result.Messages.First(m => m.IsError);
            Assert.Equal(line, err.Line);
            Assert.Contains(text, err.Text);
        }
    }
}
