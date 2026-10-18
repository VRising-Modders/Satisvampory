// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using Satisvampory.Engine.Logging;
using Satisvampory.Engine.Model;
using Entry = Satisvampory.Engine.Logging.MoveLog.Entry;
using Xunit;

namespace Satisvampory.Engine.Tests
{
    public class MoveLogTests
    {
        static readonly PlotId A = new(1), B = new(2);
        DateTime _now = new(2026, 10, 18, 8, 0, 30, DateTimeKind.Utc);
        readonly Guid _chest = Guid.NewGuid(), _press = Guid.NewGuid();

        MoveLog Log() => new(() => _now);

        void Feed(MoveLog log, int amount, PlotId from, PlotId to) =>
            log.Add(from, to, 7, amount, _chest, "Fish Storage", "Plot 6", _press, "Blood Press", "Plot 7", "Station feed", 9);

        [Fact]
        public void Identical_moves_in_a_minute_merge_and_a_move_is_filed_under_both_castles()
        {
            var log = Log();
            Feed(log, 10, A, B);
            Feed(log, 30, A, B);
            var e = Assert.Single(log.Read(new[] { A }, 24, _ => true, 50));
            Assert.Equal(40, e.Amount);
            Assert.Equal(2, e.Moves);
            Assert.Same(e, Assert.Single(log.Read(new[] { B }, 24, _ => true, 50)));
            Assert.Single(log.Read(new[] { A, B }, 24, _ => true, 50)); // once, though filed twice

            _now = _now.AddMinutes(1);
            Feed(log, 5, A, B);
            Assert.Equal(2, log.Read(new[] { A }, 24, _ => true, 50).Count);
        }

        [Fact]
        public void A_castle_leaving_the_island_keeps_its_history_and_nothing_is_redone()
        {
            var log = Log();
            Feed(log, 10, A, B);
            log.Change(B, _press, "Blood Press", "Plot 7", "Recipes switched: 3 on", "Fangly");
            // The island without B: only what touched A.
            var mine = log.Read(new[] { A }, 24, _ => true, 50);
            Assert.Single(mine);
            Assert.False(mine[0].IsChange);
            // B, read later by itself (it rejoined, or its own owner): everything it had, as written then.
            var theirs = log.Read(new[] { B }, 24, _ => true, 50);
            Assert.Equal(2, theirs.Count);
            Assert.Equal("Blood Press", theirs.First(x => x.IsChange).FromName);
        }

        [Fact]
        public void Entries_older_than_a_day_go_and_bags_are_filed_under_the_castle_end_only()
        {
            var log = Log();
            log.Add(null, A, 7, 3, Guid.NewGuid(), "Fangly's bag", "", _chest, "Fish Storage", "Plot 6", "Quick stash", 0, "Fangly");
            log.Add(null, null, 7, 3, Guid.NewGuid(), "bag", "", Guid.NewGuid(), "bag", "", "nothing", 0);
            Assert.Equal("Fangly", Assert.Single(log.Read(new[] { A, B }, 24, _ => true, 50)).Actor);
            _now = _now.AddHours(25);
            Assert.Empty(log.Read(new[] { A }, 24, _ => true, 50));
        }

        [Fact]
        public void Saved_castles_restore_and_a_move_filed_under_two_castles_reads_once()
        {
            var log = Log();
            Feed(log, 10, A, B);
            Assert.Equal(2, log.TakeDirty().Count);
            var savedA = log.CastleEntries(A);
            var savedB = log.CastleEntries(B);

            // A reload: a new log from what was saved (as copies, the way JSON gives them back).
            Entry Copy(Entry e) => new Entry { Minute = e.Minute, Last = e.Last, Item = e.Item, Amount = e.Amount, Moves = e.Moves,
                From = e.From, FromName = e.FromName, To = e.To, ToName = e.ToName, Why = e.Why, For = e.For };
            var back = Log();
            back.ImportCastle(A, savedA.Select(Copy));
            back.ImportCastle(B, savedB.Select(Copy));
            var e = Assert.Single(back.Read(new[] { A, B }, 24, _ => true, 50));
            Assert.Equal(10, e.Amount);
            Assert.Empty(back.TakeDirty());
        }
    }
}
