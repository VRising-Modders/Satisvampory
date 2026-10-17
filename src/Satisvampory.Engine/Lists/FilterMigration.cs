// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Lists
{
    /// <summary>
    /// Save version 7: what a connection may carry is no longer set on the connection. A filter on a link becomes
    /// the receiving container's Accepts (listed items and categories, exclusions as bans), and the link carries
    /// anything again. A filter that limits what a sender gives (an Output to a belt or to Mass Deposit) has no
    /// receiver to hold it; it is dropped and reported.
    /// </summary>
    public static class FilterMigration
    {
        public sealed record Result(List<ContainerId> Changed, List<string> Dropped);

        public static Result MoveToAccepts(LogisticsGraph graph, IDictionary<ContainerId, DepositOptions> deposit,
            Func<ContainerId, string> name, Func<ItemId, string> itemName)
        {
            var changed = new HashSet<ContainerId>();
            var dropped = new List<string>();
            foreach (var (owner, lists) in graph.Lists.ToList())
            {
                for (var i = 0; i < lists.Input.Count; i++)
                {
                    var e = lists.Input[i];
                    if (e.Filter.IsAnything) continue;
                    Apply(deposit, owner, e.Filter);
                    lists.Input[i] = Unfiltered(e);
                    changed.Add(owner);
                }
                for (var i = 0; i < lists.Output.Count; i++)
                {
                    var e = lists.Output[i];
                    if (e.Filter.IsAnything) continue;
                    if (e.Kind == EntryKind.Container)
                    {
                        Apply(deposit, e.Container, e.Filter);
                        changed.Add(e.Container);
                    }
                    else
                    {
                        var to = e.Kind == EntryKind.Belt && graph.Belts.TryGetValue(e.Belt, out var belt) ? belt.Name : "Mass Deposit";
                        dropped.Add($"{name(owner)} → {to}: only {Describe(e.Filter, itemName)}");
                    }
                    lists.Output[i] = Unfiltered(e);
                    changed.Add(owner);
                }
            }
            return new Result(changed.ToList(), dropped);
        }

        static void Apply(IDictionary<ContainerId, DepositOptions> deposit, ContainerId receiver, ItemFilter f)
        {
            if (!deposit.TryGetValue(receiver, out var d)) deposit[receiver] = d = new DepositOptions();
            var lists = f.Items.Count + f.Categories.Count > 0;
            // A receiver that took anything now takes only what its links named.
            if (lists && d.Takes == Takes.Anything && d.ItemPriority.Count == 0 && d.CategoryPriority.Count == 0) d.Takes = Takes.OnlyListed;
            foreach (var item in f.Items) d.ItemPriority.TryAdd(item, 0);
            foreach (var cat in f.Categories) d.CategoryPriority.TryAdd(cat, 0);
            foreach (var item in f.ExcludeItems) d.BannedItems.Add(item);
            foreach (var cat in f.ExcludeCategories) d.BannedCategories.Add(cat);
        }

        static ListEntry Unfiltered(ListEntry e) => new()
        {
            Kind = e.Kind, Container = e.Container, Belt = e.Belt, Sequence = e.Sequence, BeltOrder = e.BeltOrder,
            BeltPeers = e.BeltPeers == null ? null : new HashSet<ContainerId>(e.BeltPeers), CounterpartOnly = e.CounterpartOnly, Auto = e.Auto,
        };

        static string Describe(ItemFilter f, Func<ItemId, string> itemName) => string.Join(", ",
            f.Items.Select(itemName).Concat(f.Categories.Select(c => "category " + c))
                .Concat(f.ExcludeItems.Select(i => "not " + itemName(i))).Concat(f.ExcludeCategories.Select(c => "not category " + c)));
    }
}
