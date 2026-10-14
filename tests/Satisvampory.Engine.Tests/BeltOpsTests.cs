// SPDX-License-Identifier: AGPL-3.0-only
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Xunit;

namespace Satisvampory.Engine.Tests
{
    public class BeltOpsTests
    {
        static readonly PlotId Home = new(1);

        [Fact]
        public void Names_are_unique_case_insensitive_and_checked()
        {
            var g = new LogisticsGraph();
            Assert.True(BeltOps.Create(g, "Ore in", out var a).Ok);
            Assert.False(BeltOps.Create(g, "ORE  in", out _).Ok);
            Assert.False(BeltOps.Create(g, "  ", out _).Ok);
            Assert.False(BeltOps.Create(g, "say \"hi\"", out _).Ok);
            Assert.True(BeltOps.Rename(g, a!.Id, "ore IN", new BeltReferences()).Ok); // own name, new case
        }

        [Fact]
        public void Rename_keeps_list_entries_and_updates_mass_deposit_and_script()
        {
            var g = new LogisticsGraph();
            BeltOps.Create(g, "Jewel cabinets", out var grp);
            var chest = ContainerId.New();
            g.For(chest).Output.Add(ListEntry.OfBelt(grp!.Id));

            var md = new MassDepositSettings();
            md.ItemRules.Add((ItemSelector.Any, new Target(TargetKind.Belt, "Jewel cabinets")));
            md.AdvancedScript = "for category gems: first belt \"jewel CABINETS\"  # keep \"Jewel cabinets\" here\n";

            Assert.True(BeltOps.Rename(g, grp.Id, "Gem racks", new BeltReferences { MassDeposit = md }).Ok);

            Assert.Equal(grp.Id, g.For(chest).Output.Single().Belt);
            Assert.Equal("Gem racks", md.ItemRules[0].Target.Name);
            Assert.Contains("belt \"Gem racks\"", md.AdvancedScript);
            Assert.Contains("# keep \"Jewel cabinets\" here", md.AdvancedScript); // comments are prose, not references
        }

        [Fact]
        public void Usages_and_delete()
        {
            var g = new LogisticsGraph();
            BeltOps.Create(g, "Line 1", out var grp);
            var a = ContainerId.New();
            var b = ContainerId.New();
            g.For(a).Input.Add(ListEntry.OfBelt(grp!.Id));
            g.For(b).Output.Add(ListEntry.OfBelt(grp.Id));
            var md = new MassDepositSettings();
            md.ItemRules.Add((ItemSelector.Any, new Target(TargetKind.Belt, "line 1")));
            md.AdvancedScript = "for any:\n  first belt \"Line 1\"";
            var refs = new BeltReferences { MassDeposit = md };

            var uses = BeltOps.Usages(g, grp.Id, refs);
            Assert.Equal(new[] { BeltUseKind.InputEntry, BeltUseKind.OutputEntry, BeltUseKind.MassDepositItemRule, BeltUseKind.ScriptLine },
                uses.Select(u => u.Kind).OrderBy(k => k).ToArray());
            Assert.Equal(2, uses.Single(u => u.Kind == BeltUseKind.ScriptLine).Index);

            BeltOps.Delete(g, grp.Id, refs);
            Assert.Empty(g.Belts);
            Assert.Empty(g.For(a).Input);
            Assert.Empty(g.For(b).Output);
            Assert.Empty(md.ItemRules);
            Assert.Contains("Line 1", md.AdvancedScript); // reported, not edited
        }

        [Fact]
        public void Match_by_name_uses_whole_words_and_skips_hearts()
        {
            var w = new FakeWorld();
            var ore = w.Chest(Home, "Ore");
            var ore2 = w.Chest(Home, "Ore 2");
            var copper = w.Chest(Home, "Copper ore");
            w.Chest(Home, "Core samples");
            w.Chest(Home, "ore", kind: NodeKind.Heart);

            var matches = BeltOps.MatchByName(w, new[] { Home }, "ORE");
            Assert.Equal(new[] { ore, ore2, copper }, matches);
        }

        [Fact]
        public void Add_members_dedupes_and_move_reorders()
        {
            var g = new LogisticsGraph();
            BeltOps.Create(g, "G", out var grp);
            var (a, b, c) = (ContainerId.New(), ContainerId.New(), ContainerId.New());
            Assert.Equal(3, BeltOps.AddMembers(g, grp!.Id, new[] { a, b, c, a }));
            Assert.Equal(0, BeltOps.AddMembers(g, grp.Id, new[] { b }));
            Assert.True(BeltOps.Move(g, grp.Id, 2, 0));
            Assert.Equal(new[] { c, a, b }, grp.Members);
            Assert.False(BeltOps.Move(g, grp.Id, 5, 0));
        }

        [Fact]
        public void Duplicate_copies_members_under_a_new_name()
        {
            var g = new LogisticsGraph();
            BeltOps.Create(g, "G", out var grp);
            var a = ContainerId.New();
            grp!.Members.Add(a);
            Assert.True(BeltOps.Duplicate(g, grp.Id, "G copy", out var copy).Ok);
            Assert.Equal(new[] { a }, copy!.Members);
            Assert.NotEqual(grp.Id, copy.Id);
        }
    
        [Fact]
        public void Forget_removes_a_destroyed_chest_everywhere_and_reports_per_list()
        {
            var g = new LogisticsGraph();
            var a = ContainerId.New();
            var b = ContainerId.New();
            var gone = ContainerId.New();
            g.For(a).Input.Add(ListEntry.Of(gone));
            g.For(b).Output.Add(ListEntry.Of(gone));
            g.For(b).Output.Add(ListEntry.Of(a));
            var belt = g.BeltOrCreate("Ore in");
            belt.Members.Add(gone);
            Assert.Contains(gone, g.Referenced());

            var by = new System.Collections.Generic.Dictionary<ContainerId, int>();
            Assert.Equal(3, g.Forget(gone, by));
            Assert.Equal(1, by[a]);
            Assert.Equal(1, by[b]);
            Assert.DoesNotContain(gone, g.Referenced());
            Assert.Single(g.For(b).Output);
        }
    }
}
