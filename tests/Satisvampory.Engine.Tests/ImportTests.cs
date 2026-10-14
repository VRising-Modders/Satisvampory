// SPDX-License-Identifier: AGPL-3.0-only
using System.Linq;
using Satisvampory.Engine.Import;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class ImportTests
    {
        [Theory]
        [InlineData("plank s1", "plank", SpecialKind.None, 0)]
        [InlineData("Ore r3 S4", "Ore", SpecialKind.None, 0)]
        [InlineData("Gems++", "Gems", SpecialKind.None, 2)]
        [InlineData("Weapons --copper --iron", "Weapons", SpecialKind.None, 0)]
        [InlineData("brazier night", "", SpecialKind.None, 0)]
        [InlineData("night shift", "night shift", SpecialKind.None, 0)]
        [InlineData("''Hidden", "Hidden", SpecialKind.Skip, 0)]
        [InlineData("stuff NS", "stuff", SpecialKind.Skip, 0)]
        [InlineData("overflow", "", SpecialKind.Overflow, 0)]
        [InlineData("s1s2", "s1s2", SpecialKind.None, 0)]
        public void Parses_legacy_codes(string name, string clean, SpecialKind special, int priority)
        {
            var n = LegacyNames.Parse(name);
            Assert.Equal(clean, n.CleanName);
            Assert.Equal(special, n.Special);
            Assert.Equal(priority, n.Priority);
        }

        [Fact]
        public void A_brazier_chest_becomes_an_input_of_its_plots_braziers()
        {
            var w = new FakeWorld();
            var p = new PlotId(1);
            var chest = w.Chest(p, "brazier prox");
            w.Put(chest, Coal, 40);
            var b1 = w.Chest(p, "Brazier", kind: NodeKind.Station);
            b1.Role = StationRole.Brazier;
            var far = w.Chest(new PlotId(2), "Brazier", kind: NodeKind.Station);
            far.Role = StationRole.Brazier;
            var g = new LogisticsGraph();

            var report = LegacyImporter.Apply(new[] { chest, b1, far }, Catalog(), g, w);
            Assert.Equal(chest.Id, g.For(b1.Id).Input.Single().Container);
            Assert.Empty(g.For(far.Id).Input);
            Assert.Equal(SpecialKind.None, chest.Deposit.Special);
            Assert.Equal(Takes.OnlyListed, chest.Deposit.Takes);
            Assert.True(chest.Deposit.ItemPriority.ContainsKey(Coal));
            Assert.Equal("", report.CleanNames[chest.Id]);

            LegacyImporter.Apply(new[] { chest, b1, far }, Catalog(), g, w);
            Assert.Single(g.For(b1.Id).Input);
        }

        [Fact]
        public void Lines_become_groups_and_receiver_inputs_and_import_is_idempotent()
        {
            var w = new FakeWorld();
            var p = new PlotId(1);
            var s1 = w.Chest(p, "plank s1");
            var s1b = w.Chest(p, "wood s1");
            var r1 = w.Chest(p, "Sawmill r1", kind: NodeKind.Station);
            var banned = w.Chest(p, "Weapons --iron+ --ore --unobtainium");
            var g = new LogisticsGraph();
            var cat = Catalog();

            var report = LegacyImporter.Apply(new[] { s1, s1b, r1, banned }, cat, g);
            Assert.Equal(1, report.GroupsCreated);
            Assert.Equal(1, report.EntriesCreated);
            Assert.Equal(2, g.FindBelt("line 1")!.Members.Count);
            Assert.Equal(EntryKind.Belt, g.For(r1.Id).Input.Single().Kind);
            Assert.Contains(IronSwordFreeCheck(banned), new[] { true });
            Assert.Contains("ore", banned.Deposit.BannedCategories);
            Assert.Contains(report.Unmapped, u => u.Contains("unobtainium"));
            Assert.Equal("plank", report.CleanNames[s1.Id]);

            var again = LegacyImporter.Apply(new[] { s1, s1b, r1, banned }, cat, g);
            Assert.Equal(0, again.GroupsCreated);
            Assert.Equal(0, again.EntriesCreated);
            Assert.Single(g.For(r1.Id).Input);
        }

        // "--iron" matches no item exactly in the test catalog ("Iron Ore" etc.), so it is reported, not guessed.
        static bool IronSwordFreeCheck(ContainerInfo c) => !c.Deposit.BannedItems.Contains(Sword);
    }
}
