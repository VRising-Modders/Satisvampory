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
                    lists.Input.Add(AutoInput(graph));
                    // A factory (it runs while a recipe is on) sends its products to storage by Mass Deposit;
                    // a queue station or the prison keeps them until the player takes them or sets an Output.
                    if (IsFactory(c)) lists.Output.Add(AutoOutput(graph));
                    lists.PullOrder = PullOrder.ReverseMassDeposit;
                }
                // The Devourer takes what the player feeds it; only its products need a way out.
                else if (IsFactory(c)) lists.Output.Add(AutoOutput(graph));
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
                    lists.Input.Add(AutoInput(graph));
                    lists.PullOrder = PullOrder.ReverseMassDeposit;
                    changed.Add(c.Id);
                }
            }
            return changed;
        }

        /// <summary>
        /// A station that keeps working on its own (furnace, sawmill, Fabricator, the Devourer), not the prison:
        /// its products go to storage by default.
        /// </summary>
        public static bool IsFactory(ContainerInfo c) =>
            c.Kind == NodeKind.Station && c.Role is StationRole.Refinement or StationRole.Devourer
            && c.FurnitureName.IndexOf("Prison", StringComparison.OrdinalIgnoreCase) < 0;

        static ListEntry AutoOutput(LogisticsGraph graph) =>
            new() { Kind = EntryKind.MassDeposit, Sequence = graph.NextSequence(), Auto = true };

        /// <summary>
        /// Save version 9: factories whose Output is empty get the default products-to-storage Output (version 4
        /// had taken it off every station). An Output the player set stays as it is. Returns what changed.
        /// </summary>
        public static List<ContainerId> AddFactoryOutputs(LogisticsGraph graph, IEnumerable<ContainerInfo> containers)
        {
            var changed = new List<ContainerId>();
            foreach (var c in containers)
            {
                if (!IsFactory(c) || !graph.Lists.TryGetValue(c.Id, out var lists)) continue;
                // A Devourer once taken for a chest still has the chest's Default Belt Output: that is a leftover, not a choice.
                var leftover = c.Role == StationRole.Devourer && lists.Output.Count == 1 && IsEntry(lists.Output[0])
                    && lists.Output[0].Filter.IsAnything && lists.Output[0].BeltPeers == null;
                if (leftover) lists.Output.Clear();
                if (lists.Output.Count > 0) continue;
                lists.Output.Add(AutoOutput(graph));
                changed.Add(c.Id);
            }
            return changed;
        }

        static ListEntry AutoInput(LogisticsGraph graph) =>
            new() { Kind = EntryKind.Belt, Belt = Id, Sequence = graph.NextSequence(), Auto = true };

        /// <summary>
        /// Save version 5: a station's unfiltered Default Belt Input was the automatic one; mark it so
        /// it stops feeding the station. Returns the factories (not benches, coffins or braziers) that
        /// were fed only that way, whose contents Satisvampory put there and now sends back.
        /// </summary>
        public static List<ContainerId> MarkAutoInputs(LogisticsGraph graph, IEnumerable<ContainerInfo> containers)
        {
            var fedOnlyByDefault = new List<ContainerId>();
            foreach (var c in containers)
            {
                if (c.Kind != NodeKind.Station || !graph.Lists.TryGetValue(c.Id, out var lists)) continue;
                var marked = false;
                for (var i = 0; i < lists.Input.Count; i++)
                {
                    var e = lists.Input[i];
                    if (!IsEntry(e) || e.Auto || !e.Filter.IsAnything || e.BeltPeers != null || e.CounterpartOnly) continue;
                    lists.Input[i] = new ListEntry { Kind = e.Kind, Belt = e.Belt, Sequence = e.Sequence, BeltOrder = e.BeltOrder, Auto = true };
                    marked = true;
                }
                if (marked && c.Role is StationRole.None or StationRole.Refinement && lists.Input.All(e => e.Auto)
                    && !graph.Lists.Any(kv => kv.Key != c.Id && kv.Value.Output.Any(o => o.Kind == EntryKind.Container && o.Container == c.Id)))
                    fedOnlyByDefault.Add(c.Id);
            }
            return fedOnlyByDefault;
        }

        public static IEnumerable<ContainerId> Suppliers(LogisticsGraph graph) =>
            graph.Suppliers(Id);

        public static bool IsEntry(ListEntry entry) => entry.Kind == EntryKind.Belt && entry.Belt == Id;
    }
}
