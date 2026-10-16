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

        internal Func<ItemId, ItemCatalog, bool>? Predicate { get; init; }
        public static ItemFilter Both(ItemFilter a, ItemFilter b) => new() { Predicate = (i, c) => a.Allows(i, c) && b.Allows(i, c) };
        public static ItemFilter Any(IEnumerable<ItemFilter> filters) { var all = filters.ToArray(); return new() { Predicate = (i, c) => all.Any(f => f.Allows(i, c)) }; }

        public bool IsAnything => Predicate == null && Items.Count == 0 && Categories.Count == 0 && ExcludeItems.Count == 0 && ExcludeCategories.Count == 0;

        public bool Allows(ItemId item, ItemCatalog catalog)
        {
            if (Predicate != null && !Predicate(item, catalog)) return false;
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
        public long BeltOrder { get; set; }
        // Migration-only restriction: null means all counterparts; empty means none.
        public HashSet<ContainerId>? BeltPeers { get; init; }
        public bool CounterpartOnly { get; init; }
        public ListEntry Copy(BeltId? belt = null) => new() { Kind = Kind, Container = Container, Belt = belt ?? Belt,
            Filter = CloneFilter(Filter), Sequence = Sequence, BeltOrder = BeltOrder,
            BeltPeers = BeltPeers == null ? null : new(BeltPeers), CounterpartOnly = CounterpartOnly };
        static ItemFilter CloneFilter(ItemFilter f) { var n = new ItemFilter(); n.Items.UnionWith(f.Items); n.Categories.UnionWith(f.Categories);
            n.ExcludeItems.UnionWith(f.ExcludeItems); n.ExcludeCategories.UnionWith(f.ExcludeCategories); return n; }

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
        /// <summary>Preserve an intentionally empty setup rather than reapplying new-container defaults.</summary>
        public bool KeepEmpty { get; set; }
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
        internal LogisticsGraph? Graph { get; set; }
        /// <summary>Compatibility snapshot only. Change connections with BeltOps, never this list.</summary>
        public List<ContainerId> Members => Graph?.Members(Id).ToList() ?? new();
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
            g = new Belt(BeltId.New(), name.Trim()) { Graph = this };
            Belts[g.Id] = g;
            return g;
        }

        public IEnumerable<(ContainerId Owner, ListEntry Entry)> Connections(BeltId belt, bool output) =>
            UnorderedConnections(belt, output)
                .OrderBy(p => p.Entry.BeltOrder == 0 ? p.Entry.Sequence : p.Entry.BeltOrder);

        IEnumerable<(ContainerId Owner, ListEntry Entry)> UnorderedConnections(BeltId belt, bool output)
        {
            foreach (var pair in Lists)
                foreach (var entry in output ? pair.Value.Output : pair.Value.Input)
                    if (entry.Kind == EntryKind.Belt && entry.Belt == belt)
                        yield return (pair.Key, entry);
        }

        // A pairwise check must only visit the other endpoint, not sort the entire belt.
        IEnumerable<ListEntry> Counterparts(ListEntry entry, ContainerId owner, bool output, ContainerId other)
        {
            if (!Belts.ContainsKey(entry.Belt) || !Lists.TryGetValue(other, out var lists)
                || (entry.BeltPeers != null && !entry.BeltPeers.Contains(other))) yield break;
            foreach (var candidate in output ? lists.Input : lists.Output)
                if (candidate.Kind == EntryKind.Belt && candidate.Belt == entry.Belt
                    && (candidate.BeltPeers == null || candidate.BeltPeers.Contains(owner)))
                    yield return candidate;
        }

        long CounterpartSequence(ListEntry entry, bool output, ContainerId other)
        {
            long sequence = long.MinValue;
            if (Lists.TryGetValue(other, out var lists))
                foreach (var candidate in output ? lists.Input : lists.Output)
                    if (candidate.Kind == EntryKind.Belt && candidate.Belt == entry.Belt)
                        sequence = Math.Max(sequence, candidate.Sequence);
            return sequence;
        }

        public IEnumerable<ContainerId> Suppliers(BeltId belt) => Connections(belt, true).Select(p => p.Owner).Distinct();
        public IEnumerable<ContainerId> Receivers(BeltId belt) => Connections(belt, false).Select(p => p.Owner).Distinct();
        public IEnumerable<ContainerId> Members(BeltId belt) => Suppliers(belt).Concat(Receivers(belt)).Distinct();
        public IReadOnlyDictionary<string, IReadOnlyList<ContainerId>> BeltMembers() =>
            Belts.Values.ToDictionary(g => NameFold.Fold(g.Name), g => (IReadOnlyList<ContainerId>)Members(g.Id).ToList());

        /// <summary>Expand an Input by default. Output callers must pass output: true.
        /// Supply owner for migration peer restrictions.</summary>
        public IEnumerable<ContainerId> Expand(ListEntry e, bool output = false, ContainerId? owner = null)
        {
            if (e.Kind == EntryKind.Container) return new[] { e.Container };
            if (e.Kind != EntryKind.Belt || !Belts.ContainsKey(e.Belt)) return Enumerable.Empty<ContainerId>();
            // Old callers can omit direction/owner when passing the actual stored entry.
            if (owner == null)
                foreach (var p in Lists)
                    if (p.Value.Input.Contains(e) || p.Value.Output.Contains(e))
                    { owner = p.Key; output = p.Value.Output.Contains(e); break; }
            var self = owner;
            return Connections(e.Belt, !output)
                .Where(p => (e.BeltPeers == null || e.BeltPeers.Contains(p.Owner))
                    && (p.Entry.BeltPeers == null || self == null || p.Entry.BeltPeers.Contains(self.Value)))
                .Select(p => p.Owner).Distinct();
        }

        /// <summary>Both the supplier's Output filter and receiver's Input filter must allow an item.</summary>
        public bool Allows(ListEntry entry, ContainerId owner, bool output, ContainerId other, ItemId item, ItemCatalog catalog)
        {
            if (!entry.Filter.Allows(item, catalog)) return false;
            if (entry.Kind == EntryKind.Container) return entry.Container == other;
            if (entry.Kind != EntryKind.Belt) return false;
            foreach (var counterpart in Counterparts(entry, owner, output, other))
                if (counterpart.Filter.Allows(item, catalog)) return true;
            return false;
        }

        ItemFilter ConnectionFilter(ListEntry e, ContainerId owner, bool output, ContainerId other) =>
            e.Kind != EntryKind.Belt ? e.Filter : ItemFilter.Both(e.Filter,
                ItemFilter.Any(Counterparts(e, owner, output, other).Select(p => p.Filter)));

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
                {
                    // Normal belt Output advertises stock; receiver Input drives demand.
                    // Migrated Output definitions retain their original edge ownership.
                    if (e.CounterpartOnly) continue;
                    foreach (var to in Expand(e, true, owner))
                    {
                        var demand = e.Kind == EntryKind.Belt && e.BeltPeers == null;
                        var sequence = demand ? Math.Max(e.Sequence, CounterpartSequence(e, true, to)) : e.Sequence;
                        Add(new Edge(owner, to, ConnectionFilter(e, owner, true, to), demand ? to : owner, !demand, sequence));
                    }
                }
                foreach (var e in lists.Input.Where(e => !e.CounterpartOnly))
                    foreach (var from in Expand(e, false, owner))
                    {
                        var sequence = e.Kind == EntryKind.Belt && e.BeltPeers == null
                            ? Math.Max(e.Sequence, CounterpartSequence(e, false, from))
                            : e.Sequence;
                        Add(new Edge(from, owner, ConnectionFilter(e, owner, false, from), owner, false, sequence));
                    }
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
            if (Lists.TryGetValue(id, out var own)) removed += own.Input.Concat(own.Output).Count(e => e.Kind == EntryKind.Belt);
            Lists.Remove(id);
            foreach (var (owner, l) in Lists)
            {
                var n = l.Input.RemoveAll(e => e.Kind == EntryKind.Container && e.Container == id)
                    + l.Output.RemoveAll(e => e.Kind == EntryKind.Container && e.Container == id);
                if (n > 0 && byContainer != null) byContainer[owner] = (byContainer.TryGetValue(owner, out var had) ? had : 0) + n;
                removed += n;
            }
            foreach (var l in Lists.Values)
                foreach (var e in l.Input.Concat(l.Output)) e.BeltPeers?.Remove(id);
            return removed;
        }

        /// <summary>Every container a list entry or belt names (not the list owners themselves).</summary>
        public HashSet<ContainerId> Referenced()
        {
            var set = new HashSet<ContainerId>();
            foreach (var l in Lists.Values)
                foreach (var e in l.Input.Concat(l.Output))
                    if (e.Kind == EntryKind.Container && e.Container is { } c) set.Add(c);
            foreach (var g in Belts.Values) set.UnionWith(Members(g.Id));
            return set;
        }
    }
}
