// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.World;

namespace Satisvampory.Engine.Loops
{
    /// <summary>Per-loop run state (spec 05). Kept by the caller across ticks, keyed by <see cref="Loop.Key"/>.</summary>
    public sealed class LoopState
    {
        public LoopMode Mode { get; set; } = LoopMode.CycleOnce;
        public ulong Fingerprint { get; set; }
        public int PassesLeft { get; set; }
        public double AwakeSince { get; set; }
        /// <summary>Set when the 60 s guard stopped the loop; cleared by an outside change.</summary>
        public bool Stalled { get; set; }
    }

    public enum LoopDecision
    {
        /// <summary>Run every loop edge once this tick.</summary>
        Run,
        /// <summary>Run balancing moves only (even distribution).</summary>
        Balance,
        /// <summary>Loop edges do not run this tick.</summary>
        Sleep,
        /// <summary>Everything but the closing edge runs.</summary>
        OpenAtClosingEdge,
    }

    public static class LoopController
    {
        public const double StallSeconds = 60;

        /// <summary>
        /// Fingerprint of what the loop holds and how it is wired. Moves inside the loop do not
        /// change it (totals are summed across members), so only an outside change wakes a loop.
        /// </summary>
        public static ulong Fingerprint(Loop loop, IWorldView world)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }

                foreach (var e in loop.Edges.OrderBy(e => e.From.Value).ThenBy(e => e.To.Value))
                {
                    Mix((ulong)e.From.Value.GetHashCode());
                    Mix((ulong)e.To.Value.GetHashCode());
                    Mix((ulong)e.Sequence);
                }

                var totals = new SortedDictionary<int, long>();
                foreach (var m in loop.Members)
                    foreach (var s in world.Contents(m))
                        totals[s.Item.Guid] = (totals.TryGetValue(s.Item.Guid, out var t) ? t : 0) + s.Amount;
                foreach (var kv in totals)
                {
                    Mix((ulong)kv.Key);
                    Mix((ulong)kv.Value);
                }
                return h;
            }
        }

        public static LoopDecision Decide(Loop loop, LoopState state, IWorldView world, double now, bool loopsAllowed)
        {
            if (!loopsAllowed || state.Mode == LoopMode.Off) return LoopDecision.OpenAtClosingEdge;

            var fp = Fingerprint(loop, world);
            if (fp != state.Fingerprint)
            {
                state.Fingerprint = fp;
                state.PassesLeft = loop.Members.Count;
                state.AwakeSince = now;
                state.Stalled = false;
            }

            if (state.Stalled) return LoopDecision.Sleep;

            if (state.Mode == LoopMode.CycleOnce)
            {
                if (state.PassesLeft <= 0) return LoopDecision.Sleep;
                state.PassesLeft--;
                return LoopDecision.Run;
            }

            // Even distribution: run until balanced; the guard stops a loop that never settles.
            if (IsBalanced(loop, world)) return LoopDecision.Sleep;
            if (now - state.AwakeSince > StallSeconds)
            {
                state.Stalled = true;
                return LoopDecision.Sleep;
            }
            return LoopDecision.Balance;
        }

        /// <summary>Balanced when no item differs by more than one stack between any two members.</summary>
        public static bool IsBalanced(Loop loop, IWorldView world, int stack = 100)
        {
            var items = loop.Members.SelectMany(m => world.Contents(m)).Select(s => s.Item).Distinct();
            foreach (var item in items)
            {
                var counts = loop.Members.Select(m => world.Count(m, item)).ToList();
                if (counts.Max() - counts.Min() > stack) return false;
            }
            return true;
        }

        /// <summary>For even distribution: how much of <paramref name="item"/> to push along one edge (half the difference).</summary>
        public static int BalanceAmount(int fromCount, int toCount, int stack) =>
            fromCount - toCount > stack ? (fromCount - toCount) / 2 : 0;
    }
}
