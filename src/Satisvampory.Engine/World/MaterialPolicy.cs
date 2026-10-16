// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.World
{
    /// <summary>Mirrors Progenitor's MaterialOperation.</summary>
    public enum MaterialUse
    {
        Build,
        Craft,
        Repair,
        Upgrade,
        Research,
        Other,
    }

    /// <summary>One item's answer to Progenitor's material source provider (spec 06, 11 P3).</summary>
    public readonly record struct MaterialAnswer(IReadOnlyList<ContainerId> SpendOrder, int PoolReserve);

    /// <summary>
    /// Policy half of crafting and building from shared storage. Progenitor owns counting,
    /// payment, atomicity and replication; this decides which containers, in what order, and what
    /// reserve, for one actor and one item. The same answer serves the displayed count and the
    /// payment, so the two can never disagree.
    /// </summary>
    public sealed class MaterialPolicy
    {
        /// <summary>Apply a bench's configured inputs to permitted storage. Repairs bypass this.</summary>
        public static IReadOnlyList<ContainerId> ForInput(LogisticsGraph graph, ContainerId station,
            IReadOnlyList<ContainerId> reverseOrder, ItemId item, ItemCatalog catalog)
        {
            if (!graph.Lists.TryGetValue(station, out var lists)) return reverseOrder;
            var allowed = reverseOrder.ToHashSet();
            var result = new List<ContainerId>();
            foreach (var entry in lists.Input.Where(e => e.Filter.Allows(item, catalog)))
            {
                var sources = entry.Kind == EntryKind.MassDeposit ? reverseOrder : graph.Expand(entry, output: false, owner: station);
                foreach (var id in sources)
                {
                    if (id == station || !allowed.Contains(id) || result.Contains(id)) continue;
                    if (entry.Kind != EntryKind.MassDeposit && !graph.Allows(entry, station, false, id, item, catalog)) continue;
                    result.Add(id);
                }
            }
            return lists.PullOrder == PullOrder.ReverseMassDeposit
                ? reverseOrder.Where(result.Contains).ToList() : result;
        }

        long _revision;

        /// <summary>Operations Satisvampory governs. Research and Other stay vanilla.</summary>
        public static IReadOnlyCollection<MaterialUse> Governed { get; } =
            new[] { MaterialUse.Build, MaterialUse.Craft, MaterialUse.Repair, MaterialUse.Upgrade };

        /// <summary>Bumped on any change to scope, membership, raid state, exclusions, settings or lists.</summary>
        public long Revision => Interlocked.Read(ref _revision);

        /// <summary>Raised after every <see cref="Invalidate"/>, so the host can drop cached answers.</summary>
        public event System.Action? Changed;

        public void Invalidate()
        {
            Interlocked.Increment(ref _revision);
            Changed?.Invoke();
        }

        /// <summary>
        /// Sources for one item. Returns null for "vanilla for this item", which happens when the
        /// operation is not governed, the server disallows craft-from-storage, or the player turned
        /// it off. Otherwise the spend order is the item's reverse Mass Deposit order over the
        /// actor's island (built by the caller with <see cref="Scope.Resolve"/>), so skip chests,
        /// special-purpose chests, hearts, raided and excluded plots never appear, and a guest's
        /// island is just the standing plot.
        /// </summary>
        public MaterialAnswer? Sources(MaterialUse use, bool serverAllows, bool playerAllows, DepositContext islandCtx, CastleRules rules, ItemId item)
        {
            if (!serverAllows || !playerAllows) return null;
            if (!IsGoverned(use)) return null;
            var order = DepositPlanner.ReverseOrder(islandCtx, item);
            return new MaterialAnswer(order, rules.Reserve(item, islandCtx.Catalog));
        }

        static bool IsGoverned(MaterialUse use)
        {
            foreach (var g in Governed)
                if (g == use) return true;
            return false;
        }
    }
}
