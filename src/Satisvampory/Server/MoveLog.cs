// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;

namespace Satisvampory.Server
{
    /// <summary>
    /// What Satisvampory moved, kept per castle for 24 hours (server memory only). Each entry is written once,
    /// filed under the castle at each end, and carries the names and plot labels as they were then, so a castle
    /// leaving the clan, a rename or a destroyed chest never makes old entries wrong or needs them redone. The
    /// island's log is read by collecting the entries of the castles in it now; who may read is decided then.
    /// Identical moves within a minute merge into one entry ("×6 moves").
    /// </summary>
    internal sealed class MoveLog
    {
        internal sealed class Entry
        {
            public long Minute;
            public DateTime Last;
            public int Item;
            public int Amount;
            public int Moves;
            public Guid From;
            public string FromName = "";
            public string FromPlot = "";
            public Guid To;
            public string ToName = "";
            public string ToPlot = "";
            /// <summary>Plain words: "Station feed", "Input/Output", "Quick stash"...</summary>
            public string Why = "";
            /// <summary>For a station feed: the product the recipe makes; 0 otherwise.</summary>
            public int For;
            /// <summary>The player, for stash, tidy, Take and settings; empty for the conveyor.</summary>
            public string Actor = "";
            /// <summary>A settings change ("Filter saved: 3 allowed, 1 blocked"); empty for a move.</summary>
            public string Detail = "";
            public bool IsChange => Detail.Length > 0;
        }

        const int PerCastle = 5000;
        static readonly TimeSpan Keep = TimeSpan.FromHours(24);

        readonly Dictionary<PlotId, List<Entry>> _byCastle = new();
        readonly Dictionary<(long, int, Guid, Guid, string, int, string), Entry> _open = new();
        long _openMinute = -1;

        /// <summary>One move. <paramref name="fromPlot"/> or <paramref name="toPlot"/> is null for a player's or servant's bag.</summary>
        internal void Add(PlotId? fromPlot, PlotId? toPlot, int item, int amount, Guid from, string fromName, string fromPlotLabel,
            Guid to, string toName, string toPlotLabel, string why, int forItem = 0, string actor = "")
        {
            if (amount <= 0 || (fromPlot == null && toPlot == null)) return;
            var now = DateTime.UtcNow;
            var minute = now.Ticks / TimeSpan.TicksPerMinute;
            if (minute != _openMinute) { _open.Clear(); _openMinute = minute; }
            var key = (minute, item, from, to, why, forItem, actor);
            if (_open.TryGetValue(key, out var e))
            {
                e.Amount += amount;
                e.Moves++;
                e.Last = now;
                return;
            }
            e = new Entry
            {
                Minute = minute, Last = now, Item = item, Amount = amount, Moves = 1,
                From = from, FromName = fromName, FromPlot = fromPlotLabel, To = to, ToName = toName, ToPlot = toPlotLabel,
                Why = why, For = forItem, Actor = actor,
            };
            _open[key] = e;
            if (fromPlot is { } a) File(a, e);
            if (toPlot is { } b && (fromPlot == null || !b.Equals(fromPlot.Value))) File(b, e);
        }

        void File(PlotId castle, Entry e)
        {
            if (!_byCastle.TryGetValue(castle, out var list)) _byCastle[castle] = list = new List<Entry>();
            list.Add(e);
            if (list.Count > PerCastle) list.RemoveRange(0, PerCastle / 10);
        }

        /// <summary>A player's settings change on one castle: a container's (Filter, Input, Output...) or the island's (belts, categories).</summary>
        internal void Change(PlotId castle, Guid container, string containerName, string plotLabel, string detail, string actor)
        {
            var now = DateTime.UtcNow;
            File(castle, new Entry
            {
                Minute = now.Ticks / TimeSpan.TicksPerMinute, Last = now, Moves = 1,
                From = container, FromName = containerName, FromPlot = plotLabel, Why = "Settings", Detail = detail, Actor = actor,
            });
        }

        /// <summary>Newest first, from the given castles, last <paramref name="hours"/> hours, filtered.</summary>
        internal List<Entry> Read(IEnumerable<PlotId> castles, double hours, Func<Entry, bool> keep, int max)
        {
            var since = DateTime.UtcNow - TimeSpan.FromHours(Math.Min(hours, Keep.TotalHours));
            var cutoff = DateTime.UtcNow - Keep;
            var seen = new HashSet<Entry>();
            var result = new List<Entry>();
            foreach (var castle in castles)
            {
                if (!_byCastle.TryGetValue(castle, out var list)) continue;
                var old = list.FindIndex(x => x.Last >= cutoff);
                if (old > 0) list.RemoveRange(0, old);
                else if (old < 0) list.Clear();
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var e = list[i];
                    if (e.Last < since) break;
                    if (seen.Add(e) && keep(e)) result.Add(e);
                }
            }
            return result.OrderByDescending(e => e.Last).Take(max).ToList();
        }

        /// <summary>The conveyor's own words as a reader sees them.</summary>
        internal static string Why(string conveyorWhy) => conveyorWhy switch
        {
            "station feed" => "Station feed",
            "edge" => "Input/Output",
            "mass deposit" => "Push: Mass Deposit",
            "leftover return" or "leftover home" => "Leftovers back",
            "station cleanup" => "Station cleanup",
            "loop balance" => "Loop balance",
            "brazier fuel" => "Brazier fuel",
            _ => conveyorWhy,
        };
    }
}
