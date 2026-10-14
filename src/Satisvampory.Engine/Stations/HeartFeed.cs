// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Stations
{
    /// <summary>A player's heart blood feed settings (spec 09).</summary>
    public sealed class HeartFeedPrefs
    {
        public bool OnVisit { get; set; } = true;
        public bool OnLoginIsland { get; set; }
        public bool OutsideIsland { get; set; }
    }

    /// <summary>One heart as the feed needs it.</summary>
    public sealed record HeartInfo(PlotId Plot, int Level, int Fuel, bool AutoFeedOn, bool OwnedByPlayerOrClan);

    public enum HeartTrigger
    {
        Visit,
        Login,
    }

    public static class HeartFeed
    {
        public const int PerPad = 500;
        public const double VisitCooldownSeconds = 60;

        /// <summary>A level-N heart has N unlocked pads.</summary>
        public static int Target(int level) => Math.Max(0, level) * PerPad;

        public static int Shortfall(HeartInfo heart) => Math.Max(0, Target(heart.Level) - heart.Fuel);

        /// <summary>
        /// Which hearts to top up for one event. <paramref name="island"/> is the player's island
        /// from where they stand (or their home view at login); <paramref name="visited"/> is the
        /// plot just entered, for a visit.
        /// </summary>
        public static List<PlotId> Which(
            HeartTrigger trigger,
            HeartFeedPrefs prefs,
            bool serverAllows,
            bool isGuest,
            HeartInfo? visited,
            IReadOnlyCollection<PlotId> island,
            IEnumerable<HeartInfo> islandHearts)
        {
            var result = new List<PlotId>();
            if (!serverAllows || isGuest) return result;

            if (trigger == HeartTrigger.Visit && visited != null && prefs.OnVisit && visited.OwnedByPlayerOrClan && visited.AutoFeedOn)
            {
                if (island.Contains(visited.Plot) || prefs.OutsideIsland) result.Add(visited.Plot);
            }
            else if (trigger == HeartTrigger.Login && prefs.OnLoginIsland)
            {
                result.AddRange(islandHearts.Where(h => h.AutoFeedOn && island.Contains(h.Plot)).Select(h => h.Plot));
            }
            return result;
        }

        /// <summary>
        /// Moves for one heart: take the shortfall from <paramref name="sources"/> in order (worst
        /// home first), leaving <paramref name="reserve"/> on the island.
        /// </summary>
        public static List<(ContainerId From, int Amount)> Plan(HeartInfo heart, IReadOnlyList<(ContainerId Id, int Count)> sources, long islandTotal, int reserve)
        {
            var moves = new List<(ContainerId, int)>();
            var need = (long)Shortfall(heart);
            var spare = Math.Max(0, islandTotal - reserve);
            var left = Math.Min(need, spare);
            foreach (var (id, count) in sources)
            {
                if (left <= 0) break;
                var take = (int)Math.Min(left, count);
                if (take <= 0) continue;
                moves.Add((id, take));
                left -= take;
            }
            return moves;
        }

        /// <summary>Visit cooldown per heart.</summary>
        public static bool CooledDown(IDictionary<PlotId, double> lastFed, PlotId plot, double now) =>
            !lastFed.TryGetValue(plot, out var t) || now - t >= VisitCooldownSeconds;
    }
}
