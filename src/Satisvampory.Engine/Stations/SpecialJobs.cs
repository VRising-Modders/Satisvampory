// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.World;

namespace Satisvampory.Engine.Stations
{
    /// <summary>Server allows and castle switches for the special jobs (spec 09).</summary>
    public sealed class SpecialSwitches
    {
        public bool AllowSalvage { get; init; } = true;
        public bool CastleFeedsSalvage { get; init; }
        public bool AllowSpawner { get; init; } = true;
        public bool AllowTrash { get; init; } = true;
    }

    public sealed record JobMove(ContainerId From, ContainerId To, ItemId Item, int Amount, string Job);

    public sealed class JobReport
    {
        public List<JobMove> Moves { get; } = new();
        public Dictionary<ItemId, int> Destroyed { get; } = new();
    }

    /// <summary>
    /// Salvage and spawner chests work on their own plot only, never across an island
    /// (spec 09). Trash is emptied on request.
    /// </summary>
    public static class SpecialJobs
    {
        public const int SpawnerFeedCrafts = 2;

        /// <summary>Salvage chests feed every devourer on the same plot.</summary>
        public static void Salvage(IWorld world, PlotId plot, SpecialSwitches sw, JobReport report)
        {
            if (!sw.AllowSalvage || !sw.CastleFeedsSalvage || world.IsRaided(plot)) return;
            var on = world.ContainersOn(plot);
            var devourers = on.Where(c => c.Role == StationRole.Devourer).ToList();
            if (devourers.Count == 0) return;

            foreach (var chest in on.Where(c => c.Kind == NodeKind.Chest && c.Deposit.Special == SpecialKind.Salvage))
                foreach (var stack in world.Contents(chest.Id))
                {
                    var left = stack.Amount;
                    foreach (var d in devourers)
                    {
                        if (left <= 0) break;
                        var moved = world.Transfer(chest.Id, InvPart.Main, d.Id, stack.Item, left);
                        if (moved > 0) report.Moves.Add(new JobMove(chest.Id, d.Id, stack.Item, moved, "salvage"));
                        left -= moved;
                    }
                }
        }

        /// <summary>Spawner chests feed unit spawners on the plot, two whole crafts deep.</summary>
        public static void Spawners(IWorld world, PlotId plot, SpecialSwitches sw, JobReport report)
        {
            if (!sw.AllowSpawner || world.IsRaided(plot)) return;
            var on = world.ContainersOn(plot);
            var sources = on.Where(c => c.Kind == NodeKind.Chest && c.Deposit.Special == SpecialKind.Spawner).ToList();
            if (sources.Count == 0) return;

            foreach (var spawner in on.Where(c => c.Role == StationRole.UnitSpawner))
            {
                var plan = StationPlanner.Plan(world.Recipes(spawner.Id), world.Contents(spawner.Id),
                    item => sources.Sum(s => world.Count(s.Id, item)), SpawnerFeedCrafts);
                foreach (var (item, amount) in plan.Pull)
                {
                    var left = amount;
                    foreach (var s in sources)
                    {
                        if (left <= 0) break;
                        var moved = world.Transfer(s.Id, InvPart.Main, spawner.Id, item, left);
                        if (moved > 0) report.Moves.Add(new JobMove(s.Id, spawner.Id, item, moved, "spawner"));
                        left -= moved;
                    }
                }
            }
        }

        /// <summary>Empty one trash chest (a clan member's Empty button), or every trash chest on a plot (admin).</summary>
        public static JobReport EmptyTrash(IWorld world, IEnumerable<ContainerInfo> chests, SpecialSwitches sw)
        {
            var report = new JobReport();
            if (!sw.AllowTrash) return report;
            foreach (var c in chests.Where(c => c.Kind == NodeKind.Chest && c.Deposit.Special == SpecialKind.Trash))
                foreach (var stack in world.Contents(c.Id))
                {
                    var n = world.Destroy(c.Id, stack.Item, stack.Amount);
                    if (n > 0) report.Destroyed[stack.Item] = (report.Destroyed.TryGetValue(stack.Item, out var d) ? d : 0) + n;
                }
            return report;
        }
    }

    // ---------------- servants ----------------

    public sealed record ServantState(ContainerId Inventory, bool Alive, bool Injured);

    public static class Servants
    {
        /// <summary>
        /// Loot from returning servants goes to Mass Deposit homes, preferring the servant's own plot
        /// (the context's standing plot), then to spoils chests on that plot when nothing else takes it.
        /// </summary>
        public static List<JobMove> StashLoot(IWorld world, DepositContext homeCtx, IEnumerable<ServantState> returning)
        {
            var moves = new List<JobMove>();
            var spoils = world.ContainersOn(homeCtx.Standing)
                .Concat(homeCtx.Scope.Where(p => p != homeCtx.Standing).SelectMany(world.ContainersOn))
                .Where(c => c.Kind == NodeKind.Chest && c.Deposit.Special == SpecialKind.Spoils)
                .ToList();

            foreach (var s in returning)
                foreach (var stack in world.Contents(s.Inventory))
                {
                    var left = stack.Amount;
                    foreach (var plan in DepositPlanner.Plan(homeCtx, stack.Item, left))
                    {
                        var moved = world.Transfer(s.Inventory, InvPart.Main, plan.To, stack.Item, plan.Amount);
                        if (moved > 0) moves.Add(new JobMove(s.Inventory, plan.To, stack.Item, moved, "servant loot"));
                        left -= moved;
                    }
                    foreach (var sp in spoils)
                    {
                        if (left <= 0) break;
                        var moved = world.Transfer(s.Inventory, InvPart.Main, sp.Id, stack.Item, left);
                        if (moved > 0) moves.Add(new JobMove(s.Inventory, sp.Id, stack.Item, moved, "spoils"));
                        left -= moved;
                    }
                }
            return moves;
        }

        public sealed record RepeatRules(bool ServerAllows, bool OnlyWhileClanOnline, bool CastleOn, bool HeartRaided, bool ClanMemberOnline);

        /// <summary>Which returning servants go out again (spec 08): alive and uninjured, when every switch allows it.</summary>
        public static List<ServantState> Resend(IEnumerable<ServantState> returning, RepeatRules r)
        {
            if (!r.ServerAllows || !r.CastleOn || r.HeartRaided) return new List<ServantState>();
            if (r.OnlyWhileClanOnline && !r.ClanMemberOnline) return new List<ServantState>();
            return returning.Where(s => s.Alive && !s.Injured).ToList();
        }
    }
}
