// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Stations;
using Xunit;

namespace Satisvampory.Engine.Tests
{
    public class HeartFeedTests
    {
        static readonly PlotId A = new(1), B = new(2), C = new(3);
        static HeartInfo Heart(PlotId p, int level = 3, int fuel = 0, bool on = true, bool mine = true) => new(p, level, fuel, on, mine);

        [Fact]
        public void Target_counts_unlocked_pads()
        {
            Assert.Equal(1500, HeartFeed.Target(3));
            Assert.Equal(1200, HeartFeed.Shortfall(Heart(A, fuel: 300)));
        }

        [Fact]
        public void Visit_tops_up_an_island_plot_by_default()
        {
            var r = HeartFeed.Which(HeartTrigger.Visit, new HeartFeedPrefs(), true, false, Heart(A), new[] { A, B }, new HeartInfo[0]);
            Assert.Equal(new[] { A }, r);
        }

        [Fact]
        public void Visit_outside_the_island_needs_the_option()
        {
            var prefs = new HeartFeedPrefs();
            Assert.Empty(HeartFeed.Which(HeartTrigger.Visit, prefs, true, false, Heart(C), new[] { A }, new HeartInfo[0]));
            prefs.OutsideIsland = true;
            Assert.Equal(new[] { C }, HeartFeed.Which(HeartTrigger.Visit, prefs, true, false, Heart(C), new[] { A }, new HeartInfo[0]));
            Assert.Empty(HeartFeed.Which(HeartTrigger.Visit, prefs, true, false, Heart(C, mine: false), new[] { A }, new HeartInfo[0]));
        }

        [Fact]
        public void Login_feeds_every_island_heart_only_when_chosen()
        {
            var hearts = new[] { Heart(A), Heart(B, on: false), Heart(C) };
            var prefs = new HeartFeedPrefs();
            Assert.Empty(HeartFeed.Which(HeartTrigger.Login, prefs, true, false, null, new[] { A, B }, hearts));
            prefs.OnLoginIsland = true;
            Assert.Equal(new[] { A }, HeartFeed.Which(HeartTrigger.Login, prefs, true, false, null, new[] { A, B }, hearts));
        }

        [Fact]
        public void Guests_and_disallowed_servers_never_feed()
        {
            Assert.Empty(HeartFeed.Which(HeartTrigger.Visit, new HeartFeedPrefs(), true, true, Heart(A), new[] { A }, new HeartInfo[0]));
            Assert.Empty(HeartFeed.Which(HeartTrigger.Visit, new HeartFeedPrefs(), false, false, Heart(A), new[] { A }, new HeartInfo[0]));
        }

        [Fact]
        public void Plan_takes_in_source_order_and_keeps_reserve()
        {
            var s1 = ContainerId.New();
            var s2 = ContainerId.New();
            var moves = HeartFeed.Plan(Heart(A, level: 1, fuel: 100), new List<(ContainerId, int)> { (s1, 150), (s2, 1000) }, islandTotal: 1150, reserve: 1000);
            Assert.Equal(new[] { (s1, 150) }, moves);

            var full = HeartFeed.Plan(Heart(A, level: 1, fuel: 100), new List<(ContainerId, int)> { (s1, 150), (s2, 1000) }, islandTotal: 1150, reserve: 0);
            Assert.Equal(new[] { (s1, 150), (s2, 250) }, full);
        }
    }
}
