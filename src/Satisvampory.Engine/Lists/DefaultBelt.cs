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
                    lists.Input.Add(ListEntry.OfBelt(Id, graph.NextSequence()));
                    lists.PullOrder = PullOrder.ReverseMassDeposit;
                    lists.Output.Add(ListEntry.MassDeposit(graph.NextSequence()));
                }
            }
        }

        public static IEnumerable<ContainerId> Suppliers(LogisticsGraph graph) =>
            graph.Suppliers(Id);

        public static bool IsEntry(ListEntry entry) => entry.Kind == EntryKind.Belt && entry.Belt == Id;
    }
}
