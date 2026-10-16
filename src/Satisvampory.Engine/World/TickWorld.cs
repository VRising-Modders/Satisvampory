// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Stations;

namespace Satisvampory.Engine.World
{
    /// <summary>Read-through snapshots for one synchronous conveyor pass. Never retain across game ticks.</summary>
    public sealed class TickWorld : IWorld
    {
        readonly IWorld _live;
        readonly Dictionary<(ContainerId, InvPart), IReadOnlyList<ItemStack>> _contents = new();
        readonly Dictionary<ContainerId, Dictionary<ItemId, int>> _space = new();
        readonly Dictionary<ContainerId, IReadOnlyList<Recipe>> _recipes = new();
        readonly Dictionary<ContainerId, StationInputState> _inputs = new();
        readonly Dictionary<PlotId, bool> _raided = new();
        public TickWorld(IWorld live) => _live = live;
        public void Clear() { _contents.Clear(); _space.Clear(); _recipes.Clear(); _inputs.Clear(); _raided.Clear(); }
        public IReadOnlyList<ContainerInfo> ContainersOn(PlotId plot) => _live.ContainersOn(plot);
        public ContainerInfo? Find(ContainerId id) => _live.Find(id);
        public IReadOnlyList<ItemStack> Contents(ContainerId id, InvPart part = InvPart.Main)
        {
            if (!_contents.TryGetValue((id, part), out var value))
                _contents[(id, part)] = value = _live.Contents(id, part).ToArray();
            return value;
        }
        public int Count(ContainerId id, ItemId item, InvPart part = InvPart.Main) =>
            Contents(id, part).Where(s => s.Item == item).Sum(s => s.Amount);
        public int FreeSpace(ContainerId id, ItemId item)
        {
            if (!_space.TryGetValue(id, out var items)) _space[id] = items = new();
            if (!items.TryGetValue(item, out var value)) items[item] = value = _live.FreeSpace(id, item);
            return value;
        }
        public IReadOnlyList<Recipe> Recipes(ContainerId id)
        {
            if (!_recipes.TryGetValue(id, out var value)) _recipes[id] = value = _live.Recipes(id);
            return value;
        }
        public StationInputState StationInputs(ContainerId id)
        {
            if (!_inputs.TryGetValue(id, out var value)) _inputs[id] = value = _live.StationInputs(id);
            return value;
        }
        public bool IsRaided(PlotId plot)
        {
            if (!_raided.TryGetValue(plot, out var value)) _raided[plot] = value = _live.IsRaided(plot);
            return value;
        }
        void Invalidate(ContainerId id)
        {
            // Workstations can alias main/output; a move also changes capacity for other items.
            _contents.Remove((id, InvPart.Main)); _contents.Remove((id, InvPart.Output));
            _space.Remove(id); _inputs.Remove(id);
        }
        public int Transfer(ContainerId from, InvPart part, ContainerId to, ItemId item, int amount)
        {
            try { return _live.Transfer(from, part, to, item, amount); }
            finally { Invalidate(from); Invalidate(to); }
        }
        public int Destroy(ContainerId from, ItemId item, int amount)
        {
            try { return _live.Destroy(from, item, amount); }
            finally { Invalidate(from); }
        }
    }
}
