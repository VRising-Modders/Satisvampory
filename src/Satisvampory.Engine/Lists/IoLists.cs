// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Lists
{
    public enum EntryKind
    {
        Container,
        Belt,
        MassDeposit,
    }

    /// <summary>Optional per-row filter (spec 02). Empty include sets mean "anything".</summary>
    public sealed class ItemFilter
    {
        public HashSet<ItemId> Items { get; } = new();
        /// <summary>Folded category names.</summary>
        public HashSet<string> Categories { get; } = new();
        public HashSet<ItemId> ExcludeItems { get; } = new();
        public HashSet<string> ExcludeCategories { get; } = new();

        public bool IsAnything => Items.Count == 0 && Categories.Count == 0 && ExcludeItems.Count == 0 && ExcludeCategories.Count == 0;

        public bool Allows(ItemId item, ItemCatalog catalog)
        {
            if (ExcludeItems.Contains(item)) return false;
            if (ExcludeCategories.Any(g => catalog.InCategory(item, g))) return false;
            if (Items.Count == 0 && Categories.Count == 0) return true;
            return Items.Contains(item) || Categories.Any(g => catalog.InCategory(item, g));
        }
    }

    public sealed class ListEntry
    {
        public EntryKind Kind { get; init; }
        public ContainerId Container { get; init; }
        /// <summary>The belt for <see cref="EntryKind.Belt"/>. By id, so a rename never breaks the entry.</summary>
        public BeltId Belt { get; init; }
        public ItemFilter Filter { get; init; } = new();
        /// <summary>Creation order across the whole graph; the newest edge of a loop is the one that closes it.</summary>
        public long Sequence { get; init; }

        public static ListEntry Of(ContainerId id, long seq = 0) => new() { Kind = EntryKind.Container, Container = id, Sequence = seq };
        public static ListEntry OfBelt(BeltId belt, long seq = 0) => new() { Kind = EntryKind.Belt, Belt = belt, Sequence = seq };
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

    public readonly record struct BeltId(Guid Value)
    {
        public static BeltId New() => new(Guid.NewGuid());
        public override string ToString() => "g-" + Value.ToString("N");
    }

    public sealed class Belt
    {
        public Belt(BeltId id, string name)
        {
            Id = id;
            Name = name;
        }

        public BeltId Id { get; }

        /// <summary>Unique on the island, compared after <see cref="NameFold"/>. Change it with <see cref="BeltOps.Rename"/>.</summary>
        public string Name { get; internal set; }
        public List<ContainerId> Members { get; } = new();
    }

    /// <summary>A directed edge after lists and groups are expanded.</summary>
    public sealed record Edge(ContainerId From, ContainerId To, ItemFilter Filter, ContainerId DefinedOn, bool DefinedAsOutput, long Sequence);

    /// <summary>All lists and belts in one scope, and the edges they make.</summary>
    public sealed class LogisticsGraph
    {
        public Dictionary<ContainerId, IoLists> Lists { get; } = new();
        public Dictionary<BeltId, Belt> Belts { get; } = new();

        long _sequence;

        public long NextSequence() => ++_sequence;

        /// <summary>The last sequence handed out; saved so new edges keep sorting after old ones.</summary>
        public long Sequence
        {
            get => _sequence;
            set => _sequence = value > _sequence ? value : _sequence;
        }

        public IoLists For(ContainerId id)
        {
            if (!Lists.TryGetValue(id, out var l)) Lists[id] = l = new IoLists();
            return l;
        }

        public Belt? FindBelt(string name)
        {
            var key = NameFold.Fold(name);
            return Belts.Values.FirstOrDefault(g => NameFold.Fold(g.Name) == key);
        }

        public Belt BeltOrCreate(string name)
        {
            var g = FindBelt(name);
            if (g != null) return g;
            g = new Belt(BeltId.New(), name.Trim());
            Belts[g.Id] = g;
            return g;
        }

        /// <summary>Members by folded group name, for Mass Deposit targets and scripts (which name groups).</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<ContainerId>> BeltMembers() =>
            Belts.Values.ToDictionary(g => NameFold.Fold(g.Name), g => (IReadOnlyList<ContainerId>)g.Members);

        /// <summary>Containers an entry stands for (a group expands to its members in order).</summary>
        public IEnumerable<ContainerId> Expand(ListEntry e) => e.Kind switch
        {
            EntryKind.Container => new[] { e.Container },
            EntryKind.Belt => Belts.TryGetValue(e.Belt, out var g) ? g.Members : Enumerable.Empty<ContainerId>(),
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

        /// <summary>
        /// Remove a destroyed container from every list and belt. Returns entries removed;
        /// <paramref name="byContainer"/>, when given, collects how many each other container's lists lost.
        /// </summary>
        public int Forget(ContainerId id, IDictionary<ContainerId, int>? byContainer = null)
        {
            var removed = 0;
            Lists.Remove(id);
            foreach (var (owner, l) in Lists)
            {
                var n = l.Input.RemoveAll(e => e.Kind == EntryKind.Container && e.Container == id)
                    + l.Output.RemoveAll(e => e.Kind == EntryKind.Container && e.Container == id);
                if (n > 0 && byContainer != null) byContainer[owner] = (byContainer.TryGetValue(owner, out var had) ? had : 0) + n;
                removed += n;
            }
            foreach (var g in Belts.Values) removed += g.Members.RemoveAll(m => m == id);
            return removed;
        }

        /// <summary>Every container a list entry or belt names (not the list owners themselves).</summary>
        public HashSet<ContainerId> Referenced()
        {
            var set = new HashSet<ContainerId>();
            foreach (var l in Lists.Values)
                foreach (var e in l.Input.Concat(l.Output))
                    if (e.Kind == EntryKind.Container && e.Container is { } c) set.Add(c);
            foreach (var g in Belts.Values) set.UnionWith(g.Members);
            return set;
        }
    }
}
