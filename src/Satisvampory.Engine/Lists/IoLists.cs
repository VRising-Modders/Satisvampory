// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Lists
{
    public enum EntryKind
    {
        Container,
        ChestGroup,
        MassDeposit,
    }

    /// <summary>Optional per-row filter (spec 02). Empty include sets mean "anything".</summary>
    public sealed class ItemFilter
    {
        public HashSet<ItemId> Items { get; } = new();
        /// <summary>Folded item group names.</summary>
        public HashSet<string> Groups { get; } = new();
        public HashSet<ItemId> ExcludeItems { get; } = new();
        public HashSet<string> ExcludeGroups { get; } = new();

        public bool IsAnything => Items.Count == 0 && Groups.Count == 0 && ExcludeItems.Count == 0 && ExcludeGroups.Count == 0;

        public bool Allows(ItemId item, ItemCatalog catalog)
        {
            if (ExcludeItems.Contains(item)) return false;
            if (ExcludeGroups.Any(g => catalog.InGroup(item, g))) return false;
            if (Items.Count == 0 && Groups.Count == 0) return true;
            return Items.Contains(item) || Groups.Any(g => catalog.InGroup(item, g));
        }
    }

    public sealed class ListEntry
    {
        public EntryKind Kind { get; init; }
        public ContainerId Container { get; init; }
        /// <summary>Folded chest group name for <see cref="EntryKind.ChestGroup"/>.</summary>
        public string Group { get; init; } = string.Empty;
        public ItemFilter Filter { get; init; } = new();
        /// <summary>Creation order across the whole graph; the newest edge of a loop is the one that closes it.</summary>
        public long Sequence { get; init; }

        public static ListEntry Of(ContainerId id, long seq = 0) => new() { Kind = EntryKind.Container, Container = id, Sequence = seq };
        public static ListEntry OfGroup(string group, long seq = 0) => new() { Kind = EntryKind.ChestGroup, Group = NameFold.Fold(group), Sequence = seq };
        public static ListEntry MassDeposit(long seq = 0) => new() { Kind = EntryKind.MassDeposit, Sequence = seq };
    }

    public enum PullOrder
    {
        ListOrder,
        ReverseMassDeposit,
    }

    public enum SendOrder
    {
        FillInOrder,
        SpreadEvenly,
    }

    /// <summary>The input and output lists of one container or station.</summary>
    public sealed class IoLists
    {
        public List<ListEntry> Input { get; } = new();
        public List<ListEntry> Output { get; } = new();
        public PullOrder PullOrder { get; set; } = PullOrder.ListOrder;
        public SendOrder SendOrder { get; set; } = SendOrder.FillInOrder;
    }

    public sealed class ChestGroup
    {
        public ChestGroup(string name) => Name = name;

        public string Name { get; set; }
        public List<ContainerId> Members { get; } = new();
    }

    /// <summary>A directed edge after lists and groups are expanded.</summary>
    public sealed record Edge(ContainerId From, ContainerId To, ItemFilter Filter, ContainerId DefinedOn, bool DefinedAsOutput, long Sequence);

    /// <summary>All lists and chest groups in one scope, and the edges they make.</summary>
    public sealed class LogisticsGraph
    {
        public Dictionary<ContainerId, IoLists> Lists { get; } = new();
        /// <summary>Keyed by folded group name.</summary>
        public Dictionary<string, ChestGroup> Groups { get; } = new();

        long _sequence;

        public long NextSequence() => ++_sequence;

        public IoLists For(ContainerId id)
        {
            if (!Lists.TryGetValue(id, out var l)) Lists[id] = l = new IoLists();
            return l;
        }

        public ChestGroup GroupOrCreate(string name)
        {
            var key = NameFold.Fold(name);
            if (!Groups.TryGetValue(key, out var g)) Groups[key] = g = new ChestGroup(name);
            return g;
        }

        public IReadOnlyDictionary<string, IReadOnlyList<ContainerId>> GroupMembers() =>
            Groups.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ContainerId>)kv.Value.Members);

        /// <summary>Containers an entry stands for (a group expands to its members in order).</summary>
        public IEnumerable<ContainerId> Expand(ListEntry e) => e.Kind switch
        {
            EntryKind.Container => new[] { e.Container },
            EntryKind.ChestGroup => Groups.TryGetValue(e.Group, out var g) ? g.Members : Enumerable.Empty<ContainerId>(),
            _ => Enumerable.Empty<ContainerId>(),
        };

        /// <summary>
        /// Every container-to-container edge. An output entry on A naming B and an input entry on
        /// B naming A are the same edge; it is reported once, and the output definition wins so
        /// the source's own list decides (that is what lets a heart give items up).
        /// Mass Deposit entries are not edges; they resolve per item at run time.
        /// </summary>
        public List<Edge> Edges()
        {
            var byPair = new Dictionary<(ContainerId, ContainerId), Edge>();
            var order = new List<(ContainerId, ContainerId)>();
            void Add(Edge e)
            {
                var key = (e.From, e.To);
                if (!byPair.TryGetValue(key, out var existing)) { byPair[key] = e; order.Add(key); }
                else if (e.DefinedAsOutput && !existing.DefinedAsOutput) byPair[key] = e;
            }
            foreach (var (owner, lists) in Lists)
            {
                foreach (var e in lists.Output)
                    foreach (var to in Expand(e))
                        Add(new Edge(owner, to, e.Filter, owner, true, e.Sequence));
                foreach (var e in lists.Input)
                    foreach (var from in Expand(e))
                        Add(new Edge(from, owner, e.Filter, owner, false, e.Sequence));
            }
            return order.Select(k => byPair[k]).ToList();
        }

        /// <summary>Remove a destroyed container from every list and group. Returns entries removed.</summary>
        public int Forget(ContainerId id)
        {
            var removed = 0;
            Lists.Remove(id);
            foreach (var l in Lists.Values)
            {
                removed += l.Input.RemoveAll(e => e.Kind == EntryKind.Container && e.Container == id);
                removed += l.Output.RemoveAll(e => e.Kind == EntryKind.Container && e.Container == id);
            }
            foreach (var g in Groups.Values) removed += g.Members.RemoveAll(m => m == id);
            return removed;
        }
    }
}
