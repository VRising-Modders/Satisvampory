// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Loops;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Persistence;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class PersistenceTests
    {
        [Fact]
        public void Island_round_trips_through_json()
        {
            var state = new IslandState();
            var g = state.Graph;
            var (a, b, c) = (ContainerId.New(), ContainerId.New(), ContainerId.New());
            var belt = g.BeltOrCreate("Ore in");
            belt.Members.AddRange(new[] { b, c });
            var filtered = ListEntry.Of(b, g.NextSequence());
            filtered.Filter.Categories.Add("ore");
            filtered.Filter.ExcludeItems.Add(CopperOre);
            g.For(a).Output.Add(filtered);
            g.For(a).Input.Add(ListEntry.OfBelt(belt.Id, g.NextSequence()));
            g.For(a).Output.Add(ListEntry.MassDeposit(g.NextSequence()));
            g.For(a).PullOrder = PullOrder.ReverseMassDeposit;

            state.MassDeposit.ItemRules.Add((ItemSelector.Any, new Target(TargetKind.Belt, "Ore in")));
            state.MassDeposit.ItemRules.Add((new ItemSelector(ItemSelectorKind.Item, BloodEssence), new Target(TargetKind.Chest, "Alchemy", "Plot 2")));
            state.MassDeposit.AdvancedScript = "for category gems: first belt \"Ore in\"";
            state.Rules.DefaultReserve = 7;
            state.Rules.ItemCap[Ruby] = 300;
            state.Rules.CategoryReserve["wood"] = 50;
            var d = new DepositOptions { BasePriority = 3, Special = SpecialKind.Overflow, Takes = Takes.OnlyListed };
            d.ItemPriority[IronOre] = -2;
            d.BannedCategories.Add("weapons");
            state.Deposit[c] = d;
            state.LoopModes["k"] = LoopMode.EvenDistribution;

            var json = SaveCodec.ToJson(SaveCodec.Capture(state, g.Sequence));
            var back = SaveCodec.Restore(SaveCodec.FromJson(json), out var seq);

            Assert.Equal(3, seq);
            Assert.True(back.Graph.NextSequence() > 3);
            var belt2 = back.Graph.FindBelt("ore in")!;
            Assert.Equal(belt.Id, belt2.Id);
            Assert.Equal(new[] { b, c }, belt2.Members);
            var la = back.Graph.Lists[a];
            Assert.Equal(PullOrder.ReverseMassDeposit, la.PullOrder);
            Assert.Equal(EntryKind.Belt, la.Input.Single().Kind);
            Assert.Equal(belt.Id, la.Input.Single().Belt);
            Assert.Contains("ore", la.Output[0].Filter.Categories);
            Assert.Contains(CopperOre, la.Output[0].Filter.ExcludeItems);
            Assert.Equal(EntryKind.MassDeposit, la.Output[1].Kind);

            Assert.Equal(TargetKind.Belt, back.MassDeposit.ItemRules[0].Target.Kind);
            Assert.Equal("Plot 2", back.MassDeposit.ItemRules[1].Target.Plot);
            Assert.Equal(state.MassDeposit.AdvancedScript, back.MassDeposit.AdvancedScript);
            Assert.Equal(7, back.Rules.DefaultReserve);
            Assert.Equal(300, back.Rules.ItemCap[Ruby]);
            Assert.Equal(3, back.Deposit[c].BasePriority);
            Assert.Equal(Takes.OnlyListed, back.Deposit[c].Takes);
            Assert.Equal(SpecialKind.Overflow, back.Deposit[c].Special);
            Assert.Contains("weapons", back.Deposit[c].BannedCategories);
            Assert.Equal(LoopMode.EvenDistribution, back.LoopModes["k"]);
        }

        [Fact]
        public void Newer_version_is_refused_and_bad_entries_are_skipped()
        {
            Assert.Throws<InvalidOperationException>(() => SaveCodec.FromJson("{\"Version\": 99}"));
            var save = SaveCodec.FromJson("{\"Version\":1,\"Containers\":{\"not-a-guid\":{},\"" + Guid.NewGuid().ToString("N") +
                "\":{\"Input\":[{\"Kind\":\"container\",\"Container\":\"junk\"}],\"Special\":\"NoSuchKind\"}}}");
            var state = SaveCodec.Restore(save, out _);
            var only = Assert.Single(state.Graph.Lists);
            Assert.Empty(only.Value.Input);
            Assert.Equal(SpecialKind.None, state.Deposit[only.Key].Special);
            Assert.Empty(state.MassDeposit.ItemRules);
        }

        [Fact]
        public void Settings_defaults_match_the_spec()
        {
            var me = new PlayerSettings();
            Assert.True(me.ShareBetweenMyCastles && me.TakePartInClanShare && me.HeartTopUpOnVisit && me.CraftFromStorage);
            Assert.False(me.HeartTopUpIslandOnLogin || me.StashFromAnywhere);
            var server = new ServerSettings();
            Assert.True(server.CastleShare && server.ClanShare && server.AllowLoops && server.AllowHeartBloodFeed);
            Assert.False(server.AllowRepeatHunts || server.AllowUnmoddedClients || server.CleanNamesAfterImport);
            Assert.Equal(99, server.RepeatHuntSuccessCap);
        }
    }
}
