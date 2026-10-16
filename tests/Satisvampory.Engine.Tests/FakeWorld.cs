// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.World;

namespace Satisvampory.Engine.Tests
{
    /// <summary>In-memory world: slot-less inventories with a per-container item capacity.</summary>
    sealed class FakeWorld : IWorld
    {
        readonly List<ContainerInfo> _all = new();
        readonly Dictionary<ContainerId, ContainerInfo> _byId = new();
        readonly Dictionary<(ContainerId, InvPart), Dictionary<ItemId, int>> _inv = new();
        readonly Dictionary<ContainerId, int> _capacity = new();
        readonly Dictionary<ContainerId, HashSet<ItemId>> _refuses = new();
        public HashSet<PlotId> Raided { get; } = new();

        public ContainerInfo Chest(PlotId plot, string name, int capacity = 1000, NodeKind kind = NodeKind.Chest)
        {
            var c = new ContainerInfo(ContainerId.New(), plot, kind, name) { FurnitureName = "Large Chest", NativeRoutingKnown = true };
            _all.Add(c);
            _byId[c.Id] = c;
            _capacity[c.Id] = capacity;
            return c;
        }

        public FakeWorld Put(ContainerInfo c, ItemId item, int amount, InvPart part = InvPart.Main)
        {
            var inv = Inv(c.Id, part);
            inv[item] = (inv.TryGetValue(item, out var n) ? n : 0) + amount;
            return this;
        }

        public void Refuse(ContainerInfo c, ItemId item)
        {
            if (!_refuses.TryGetValue(c.Id, out var s)) _refuses[c.Id] = s = new HashSet<ItemId>();
            s.Add(item);
        }

        Dictionary<ItemId, int> Inv(ContainerId id, InvPart part)
        {
            if (!_inv.TryGetValue((id, part), out var d)) _inv[(id, part)] = d = new Dictionary<ItemId, int>();
            return d;
        }

        public IReadOnlyList<ContainerInfo> ContainersOn(PlotId plot) => _all.Where(c => c.Plot == plot).ToList();

        public ContainerInfo? Find(ContainerId id) => _byId.TryGetValue(id, out var c) ? c : null;

        public IReadOnlyList<ItemStack> Contents(ContainerId id, InvPart part = InvPart.Main) =>
            Inv(id, part).Where(kv => kv.Value > 0).Select(kv => new ItemStack(kv.Key, kv.Value)).ToList();

        public int Count(ContainerId id, ItemId item, InvPart part = InvPart.Main) =>
            Inv(id, part).TryGetValue(item, out var n) ? n : 0;

        public int FreeSpace(ContainerId id, ItemId item)
        {
            if (_refuses.TryGetValue(id, out var r) && r.Contains(item)) return 0;
            return Math.Max(0, _capacity[id] - Inv(id, InvPart.Main).Values.Sum());
        }

        public bool IsRaided(PlotId plot) => Raided.Contains(plot);

        public Dictionary<ContainerId, List<Stations.Recipe>> StationRecipes { get; } = new();

        public Dictionary<ContainerId, Stations.StationInputState> InputStates { get; } = new();
        public Stations.StationInputState StationInputs(ContainerId station) =>
            InputStates.TryGetValue(station, out var state) ? state : Stations.StationInputState.Unknown;


        public int Destroy(ContainerId from, ItemId item, int amount)
        {
            var n = Math.Min(amount, Count(from, item));
            if (n > 0) Inv(from, InvPart.Main)[item] -= n;
            return n;
        }


        public IReadOnlyList<Stations.Recipe> Recipes(ContainerId station) =>
            StationRecipes.TryGetValue(station, out var r) ? r : new List<Stations.Recipe>();

        public int Transfer(ContainerId from, InvPart fromPart, ContainerId to, ItemId item, int amount)
        {
            var n = Math.Min(Math.Min(amount, Count(from, item, fromPart)), FreeSpace(to, item));
            if (n <= 0) return 0;
            Inv(from, fromPart)[item] -= n;
            Put(Find(to)!, item, n);
            return n;
        }
    }

    /// <summary>A small invented catalog. Ids are arbitrary test numbers, not game guids.</summary>
    static class TestItems
    {
        public static readonly ItemId IronOre = new(1001);
        public static readonly ItemId CopperOre = new(1002);
        public static readonly ItemId IronIngot = new(1003);
        public static readonly ItemId Plank = new(1004);
        public static readonly ItemId Lumber = new(1005);
        public static readonly ItemId Ruby = new(1006);
        public static readonly ItemId BloodJewel = new(1007);
        public static readonly ItemId BloodEssence = new(1008);
        public static readonly ItemId Coal = new(1009);
        public static readonly ItemId Sword = new(1010);

        public static ItemCatalog Catalog()
        {
            var c = new ItemCatalog();
            c.AddItem(IronOre, "Iron Ore");
            c.AddItem(CopperOre, "Copper Ore");
            c.AddItem(IronIngot, "Iron Ingot");
            c.AddItem(Plank, "Plank");
            c.AddItem(Lumber, "Lumber");
            c.AddItem(Ruby, "Ruby");
            c.AddItem(BloodJewel, "Blood Jewel");
            c.AddItem(BloodEssence, "Blood Essence", 500);
            c.AddItem(Coal, "Coal");
            c.AddItem(Sword, "Iron Sword", 1);
            c.AddAlias("be", BloodEssence);
            c.AddCategory("ore", new[] { IronOre, CopperOre }, "ores");
            c.AddCategory("ingots", new[] { IronIngot }, "ingot");
            c.AddCategory("wood", new[] { Plank, Lumber });
            c.AddCategory("gems", new[] { Ruby }, "gem");
            c.AddCategory("jewels", new[] { BloodJewel }, "jewel");
            c.AddCategory("blood", new[] { BloodEssence });
            c.AddCategory("weapons", new[] { Sword }, "weapon");
            return c;
        }
    }
}
