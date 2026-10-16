// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Lists
{
    /// <summary>The built-in supply connection. Output advertises stock; Input requests it.</summary>
    public static class DefaultBelt
    {
        public static readonly BeltId Id = new(new Guid("438bdd67-7499-4fba-9d16-2510adc98d35"));
        public const string Name = "Default Belt";

        /// <summary>Only new containers receive defaults. Existing and deliberately empty lists survive.</summary>
        public static void Initialize(LogisticsGraph graph, IEnumerable<ContainerInfo> containers)
        {
            if (!graph.Belts.ContainsKey(Id))
            {
                var label = Name;
                var suffix = 1;
                while (graph.FindBelt(label) != null) label = Name + " (automatic " + suffix++ + ")";
                graph.Belts[Id] = new Belt(Id, label) { Graph = graph };
            }
            foreach (var c in containers)
            {
                if (graph.Lists.ContainsKey(c.Id) || c.Kind == NodeKind.Heart || !c.DefaultBeltEligible) continue;
                var lists = graph.For(c.Id);
                lists.KeepEmpty = true;
                if (c.Deposit.Special != SpecialKind.None) continue;
                if (c.Kind == NodeKind.Chest)
                    lists.Output.Add(ListEntry.OfBelt(Id, graph.NextSequence()));
                else if (c.Kind == NodeKind.Station && c.Role is StationRole.None or StationRole.Refinement or StationRole.Workstation)
                {
                    // Input only: a station sends nothing out until the player sets an Output.
                    lists.Input.Add(ListEntry.OfBelt(Id, graph.NextSequence()));
                    lists.PullOrder = PullOrder.ReverseMassDeposit;
                }
            }
        }

        /// <summary>
        /// Save version 4: stations used to get "Push: Mass Deposit" as a default Output, which took
        /// products out (a prison cell's Blood Merlot) though the player set no Output. Remove it
        /// where the station's lists are still exactly that default. Returns what changed.
        /// </summary>
        public static List<ContainerId> RemoveDefaultStationOutputs(LogisticsGraph graph, IEnumerable<ContainerInfo> containers)
        {
            var changed = new List<ContainerId>();
            foreach (var c in containers)
            {
                if (c.Kind != NodeKind.Station || !graph.Lists.TryGetValue(c.Id, out var lists)) continue;
                if (lists.Input.Count != 1 || !IsEntry(lists.Input[0])) continue;
                if (lists.Output.Count != 1 || lists.Output[0].Kind != EntryKind.MassDeposit || !lists.Output[0].Filter.IsAnything) continue;
                lists.Output.Clear();
                changed.Add(c.Id);
            }
            return changed;
        }

        /// <summary>
        /// Once per island, for saves from before the Default Belt: containers that already had lists
        /// never got the defaults, so stations crafted from no storage. Ordinary chests join the belt's
        /// supply (their Output), and stations with no Input draw from it. Returns what changed.
        /// </summary>
        public static List<ContainerId> MigrateExisting(LogisticsGraph graph, IEnumerable<ContainerInfo> containers)
        {
            var changed = new List<ContainerId>();
            foreach (var c in containers)
            {
                if (!graph.Lists.TryGetValue(c.Id, out var lists) || c.Kind == NodeKind.Heart || !c.DefaultBeltEligible) continue;
                if (c.Deposit.Special != SpecialKind.None) continue;
                if (c.Kind == NodeKind.Chest && !lists.Output.Any(IsEntry))
                {
                    lists.Output.Add(ListEntry.OfBelt(Id, graph.NextSequence()));
                    changed.Add(c.Id);
                }
                else if (c.Kind == NodeKind.Station && c.Role is StationRole.None or StationRole.Refinement or StationRole.Workstation && lists.Input.Count == 0)
                {
                    lists.Input.Add(ListEntry.OfBelt(Id, graph.NextSequence()));
                    lists.PullOrder = PullOrder.ReverseMassDeposit;
                    changed.Add(c.Id);
                }
            }
            return changed;
        }

        public static IEnumerable<ContainerId> Suppliers(LogisticsGraph graph) =>
            graph.Suppliers(Id);

        public static bool IsEntry(ListEntry entry) => entry.Kind == EntryKind.Belt && entry.Belt == Id;
    }
}
