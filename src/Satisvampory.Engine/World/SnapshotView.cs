// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Stations;

namespace Satisvampory.Engine.World
{
    /// <summary>
    /// A read-through cache over a world view: each inventory is read once, then counts come from
    /// memory. For hot paths that ask about many items over the same chests within one moment (the
    /// material provider's per-item answers). Never use it across moves: it does not see them.
    /// </summary>
    public sealed class SnapshotView : IWorldView
    {
        readonly IWorldView _inner;
        readonly Dictionary<PlotId, IReadOnlyList<ContainerInfo>> _plots = new();
        readonly Dictionary<(ContainerId, InvPart), IReadOnlyList<ItemStack>> _contents = new();
        readonly Dictionary<(ContainerId, InvPart), Dictionary<ItemId, int>> _counts = new();
        readonly Dictionary<(ContainerId, ItemId), int> _free = new();
        readonly Dictionary<PlotId, bool> _raided = new();

        public SnapshotView(IWorldView inner) => _inner = inner;

        /// <summary>Inventory reads made through to the inner view (for tests and diagnostics).</summary>
        public int Reads { get; private set; }

        public IReadOnlyList<ContainerInfo> ContainersOn(PlotId plot)
        {
            if (!_plots.TryGetValue(plot, out var list)) _plots[plot] = list = _inner.ContainersOn(plot);
            return list;
        }

        public ContainerInfo? Find(ContainerId id) => _inner.Find(id);

        public IReadOnlyList<ItemStack> Contents(ContainerId id, InvPart part = InvPart.Main)
        {
            var key = (id, part);
            if (_contents.TryGetValue(key, out var stacks)) return stacks;
            Reads++;
            stacks = _inner.Contents(id, part);
            _contents[key] = stacks;
            var counts = new Dictionary<ItemId, int>();
            foreach (var s in stacks) counts[s.Item] = (counts.TryGetValue(s.Item, out var n) ? n : 0) + s.Amount;
            _counts[key] = counts;
            return stacks;
        }

        public int Count(ContainerId id, ItemId item, InvPart part = InvPart.Main)
        {
            if (!_counts.ContainsKey((id, part))) Contents(id, part);
            return _counts[(id, part)].TryGetValue(item, out var n) ? n : 0;
        }

        public int FreeSpace(ContainerId id, ItemId item)
        {
            if (!_free.TryGetValue((id, item), out var n)) _free[(id, item)] = n = _inner.FreeSpace(id, item);
            return n;
        }

        public bool IsRaided(PlotId plot)
        {
            if (!_raided.TryGetValue(plot, out var r)) _raided[plot] = r = _inner.IsRaided(plot);
            return r;
        }

        public IReadOnlyList<Recipe> Recipes(ContainerId station) => _inner.Recipes(station);

        /// <summary>Every item held in a main inventory on these plots' chests, read once each.</summary>
        public HashSet<ItemId> ItemsIn(IEnumerable<PlotId> plots) =>
            new(plots.SelectMany(ContainersOn).Where(c => c.Shared).SelectMany(c => Contents(c.Id)).Select(s => s.Item));
    }
}
