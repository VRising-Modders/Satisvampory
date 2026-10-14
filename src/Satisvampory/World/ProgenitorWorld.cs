// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Inventory;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Stations;
using Satisvampory.Engine.World;
using EngineId = Satisvampory.Engine.Model.ContainerId;
using PgId = Progenitor.Inventory.ContainerId;

namespace Satisvampory.World
{
    /// <summary>
    /// The engine's <see cref="IWorld"/> over Progenitor's inventory and castle APIs (spec 11,
    /// P1/P4/P5). Built per tick or request for the plots in scope; it reads containers once and
    /// asks Progenitor for counts, space and every move, so nothing here touches ECS.
    /// </summary>
    internal sealed class ProgenitorWorld : IWorld
    {
        sealed class Node
        {
            public ContainerInfo Info = null!;
            public InventoryRef Main;
            public InventoryRef Output;
            public ICastleHandle Castle = null!;
            public Unity.Entities.Entity Entity;
            /// <summary>Which source slots a move may draw from; a player's bag skips the action bar.</summary>
            public InventorySlotFilter SourceSlots;
        }

        readonly PluginInventory _inv;
        readonly Progenitor.Stations.PluginStations? _stations;
        readonly Dictionary<EngineId, Node> _nodes = new();
        readonly Dictionary<PlotId, List<ContainerInfo>> _byPlot = new();
        readonly Dictionary<PlotId, ICastleHandle> _castles = new();
        readonly Func<EngineId, DepositOptions?> _deposit;

        /// <summary>Satisvampory's switches for crafting-table recipes; null means all off.</summary>
        public Func<EngineId, ISet<int>?>? TableRecipesOn { get; set; }

        // ---------------- container rows ----------------

        /// <summary>Background work (ticker, material answers) accepts rows this old; player requests read fresh.</summary>
        public const long BackgroundRowsMs = 30_000;

        static readonly Dictionary<int, (long At, ulong Heart, List<CastleContainerInfo> Rows)> RowCache = new();

        /// <summary>
        /// A castle's containers and stations. Progenitor walks every building of the castle for
        /// each list (about 100 ms for a large castle), so rows up to <paramref name="maxAgeMs"/>
        /// old are reused; a fresh read refreshes the cache for everyone. Main thread only.
        /// </summary>
        public static List<CastleContainerInfo> Rows(ICastleHandle castle, long maxAgeMs = 0)
        {
            var now = Environment.TickCount64;
            var key = castle.Territory.Index;
            var heart = castle.Heart.Index == 0 ? 0UL : ((ulong)(uint)castle.Heart.Index << 32) | (uint)castle.Heart.Version;
            if (maxAgeMs > 0 && RowCache.TryGetValue(key, out var hit) && hit.Heart == heart && now - hit.At <= maxAgeMs) return hit.Rows;
            var rows = castle.Containers().Concat(castle.Stations()).ToList();
            RowCache[key] = (now, heart, rows);
            return rows;
        }

        /// <summary>Drop every cached row (plugin unload).</summary>
        public static void ForgetRows() => RowCache.Clear();

        public ProgenitorWorld(PluginInventory inventory, Progenitor.Stations.PluginStations? stations, IEnumerable<ICastleHandle> castles, Func<EngineId, DepositOptions?> deposit, long maxRowAgeMs = 0)
        {
            _inv = inventory;
            _stations = stations;
            _deposit = deposit;
            foreach (var castle in castles)
            {
                if (!castle.Enabled) continue;
                var plot = PlotOf(castle);
                _castles[plot] = castle;
                var list = new List<ContainerInfo>();
                _byPlot[plot] = list;
                // The heart: lists and fuel, never a Mass Deposit home or default source (spec 09).
                if (!castle.HeartId.IsNone)
                {
                    var heartId = new EngineId(castle.HeartId.Value);
                    var heartEntity = castle.Heart.ToEntity();
                    var heartInfo = new ContainerInfo(heartId, plot, NodeKind.Heart, "Castle heart") { FurnitureName = "Castle heart" };
                    _nodes[heartId] = new Node { Info = heartInfo, Main = inventory.Of(heartEntity), Output = default, Castle = castle, Entity = heartEntity };
                    list.Add(heartInfo);
                }

                foreach (var row in Rows(castle, maxRowAgeMs))
                {
                    if (row.Id.IsNone) continue;
                    var id = new EngineId(row.Id.Value);
                    var info = new ContainerInfo(id, plot, row.IsStation ? NodeKind.Station : NodeKind.Chest, row.Nameplate)
                    {
                        FurnitureName = Pretty(row.Building.PrefabName),
                        X = CastleGeometry.TileToWorld(row.Building.TileX),
                        Z = CastleGeometry.TileToWorld(row.Building.TileY),
                        Role = RoleOf(row),
                    };
                    if (_deposit(id) is { } d) Copy(d, info.Deposit);
                    _nodes[id] = new Node { Info = info, Main = row.Main, Output = row.Output, Castle = castle, Entity = row.Entity.ToEntity() };
                    list.Add(info);
                }
            }
        }

        /// <summary>
        /// Register the actor's bag for one action. It gets a throwaway id, is never on a plot, so it
        /// is never a home or a source for anything but the action that registered it.
        /// </summary>
        /// <param name="player">A player's bag: moves out of it never touch the action bar (slots 0-7).</param>
        public EngineId AddBag(InventoryRef bag, bool player = false)
        {
            var id = EngineId.New();
            _nodes[id] = new Node
            {
                Info = new ContainerInfo(id, default, NodeKind.Chest, "bag"),
                Main = bag,
                Output = default,
                SourceSlots = player ? InventorySlotFilter.SkipActionBar : InventorySlotFilter.All,
            };
            return id;
        }

        /// <summary>Every container, station and heart id on these castles, for loading their saved records.</summary>
        public static IEnumerable<EngineId> ContainerIds(IEnumerable<ICastleHandle> castles, long maxRowAgeMs = 0)
        {
            foreach (var c in castles)
            {
                if (!c.Enabled) continue;
                if (!c.HeartId.IsNone) yield return new EngineId(c.HeartId.Value);
                foreach (var row in Rows(c, maxRowAgeMs))
                    if (!row.Id.IsNone) yield return new EngineId(row.Id.Value);
            }
        }

        /// <summary>A plot is identified by its territory index, which survives restarts.</summary>
        public static PlotId PlotOf(ICastleHandle castle) => new((ulong)(uint)castle.Territory.Index);

        public IReadOnlyList<ContainerInfo> ContainersOn(PlotId plot) =>
            _byPlot.TryGetValue(plot, out var l) ? l : (IReadOnlyList<ContainerInfo>)Array.Empty<ContainerInfo>();

        public ContainerInfo? Find(EngineId id) => _nodes.TryGetValue(id, out var n) ? n.Info : null;

        public IReadOnlyList<ItemStack> Contents(EngineId id, InvPart part = InvPart.Main)
        {
            if (!TryRef(id, part, out var r)) return Array.Empty<ItemStack>();
            var snap = _inv.Read(r);
            var sums = new Dictionary<int, int>();
            foreach (var s in snap.Slots)
                if (s.Item != 0 && s.Amount > 0)
                    sums[s.Item] = (sums.TryGetValue(s.Item, out var n) ? n : 0) + s.Amount;
            return sums.Select(kv => new ItemStack(new ItemId(kv.Key), kv.Value)).ToList();
        }

        public int Count(EngineId id, ItemId item, InvPart part = InvPart.Main) =>
            TryRef(id, part, out var r) ? _inv.Count(r, item.Guid) : 0;

        public int FreeSpace(EngineId id, ItemId item) =>
            TryRef(id, InvPart.Main, out var r) ? _inv.FreeSpace(r, item.Guid) : 0;

        public bool IsRaided(PlotId plot) => _castles.TryGetValue(plot, out var c) && c.Raided;

        /// <summary>
        /// A station's recipes from ctx.Stations. Refinement stations use the game's own per-recipe
        /// switch. Crafting tables have none, so their recipes count as off until Satisvampory's
        /// table switches exist (spec 04: default off).
        /// </summary>
        /// <summary>Raw rows for the recipe switches panel: guid, name, inputs, whether the game toggles it, and on.</summary>
        public IReadOnlyList<(int Guid, string Name, IReadOnlyList<ItemStack> Inputs, bool GameToggle, bool On)> RecipeRows(EngineId station)
        {
            if (_stations == null || !_nodes.TryGetValue(station, out var n) || n.Info.Kind != NodeKind.Station)
                return Array.Empty<(int, string, IReadOnlyList<ItemStack>, bool, bool)>();
            var mine = TableRecipesOn?.Invoke(station);
            return _stations.Recipes(n.Entity)
                .Where(r => !r.HiddenInStation)
                .Select(r => (r.RecipeGuid, r.Name, (IReadOnlyList<ItemStack>)r.Inputs.Select(x => new ItemStack(new ItemId(x.ItemGuid), x.Amount)).ToList(),
                    r.CanToggle, r.CanToggle ? r.Enabled : mine?.Contains(r.RecipeGuid) == true))
                .ToList();
        }

        /// <summary>The placed entity behind a container id, for nameplate writes.</summary>
        public Unity.Entities.Entity EntityOf(EngineId id) => _nodes.TryGetValue(id, out var n) ? n.Entity : Unity.Entities.Entity.Null;

        /// <summary>The game's own switch, for refinement stations.</summary>
        public bool SetGameRecipe(EngineId station, int recipe, bool on) =>
            _stations != null && _nodes.TryGetValue(station, out var n) && _stations.SetRecipeEnabled(n.Entity, recipe, on).Ok;

        public IReadOnlyList<Recipe> Recipes(EngineId station)
        {
            if (_stations == null || !_nodes.TryGetValue(station, out var n) || n.Info.Kind != NodeKind.Station) return Array.Empty<Recipe>();
            var list = new List<Recipe>();
            foreach (var r in _stations.Recipes(n.Entity))
            {
                var on = r.CanToggle ? r.Enabled : TableRecipesOn?.Invoke(station)?.Contains(r.RecipeGuid) == true;
                list.Add(new Recipe(
                    r.RecipeGuid,
                    r.Inputs.Select(x => new ItemStack(new ItemId(x.ItemGuid), x.Amount)).ToList(),
                    r.Outputs.Select(x => new ItemStack(new ItemId(x.ItemGuid), x.Amount)).ToList(),
                    on));
            }
            return list;
        }

        public int Transfer(EngineId from, InvPart fromPart, EngineId to, ItemId item, int amount)
        {
            if (amount <= 0 || !TryRef(from, fromPart, out var a) || !TryRef(to, InvPart.Main, out var b)) return 0;
            var slots = fromPart == InvPart.Main && _nodes.TryGetValue(from, out var n) ? n.SourceSlots : InventorySlotFilter.All;
            var r = _inv.Transfer(a, b, item.Guid, amount, slots);
            if (r.Done == 0) NoteFailure($"transfer {item.Guid} x{amount}: {r.Outcome} {r.Reason}");
            return r.Ok ? r.Done : 0;
        }

        /// <summary>A few failed moves per minute go to the log so a list that never moves can be explained.</summary>
        public static System.Action<string>? FailureLog { get; set; }
        static int _failures;
        static System.DateTime _failureWindow;

        static void NoteFailure(string text)
        {
            if (FailureLog == null) return;
            var now = System.DateTime.UtcNow;
            if (now - _failureWindow > System.TimeSpan.FromMinutes(1)) { _failureWindow = now; _failures = 0; }
            if (++_failures <= 3) FailureLog("Satisvampory: " + text);
        }

        /// <summary>Creates items in a container (admin Give to storage). Returns how many went in.</summary>
        public int Create(EngineId to, ItemId item, int amount)
        {
            if (amount <= 0 || !TryRef(to, InvPart.Main, out var r)) return 0;
            var res = _inv.TryAdd(r, item.Guid, amount);
            return res.Ok ? res.Done : 0;
        }

        public int Destroy(EngineId from, ItemId item, int amount)
        {
            if (amount <= 0 || !TryRef(from, InvPart.Main, out var r)) return 0;
            var res = _inv.TryRemove(r, item.Guid, amount);
            return res.Ok ? res.Done : 0;
        }


        bool TryRef(EngineId id, InvPart part, out InventoryRef r)
        {
            r = default;
            if (!_nodes.TryGetValue(id, out var n)) return false;
            r = part == InvPart.Output && n.Info.Kind == NodeKind.Station ? n.Output : n.Main;
            return !r.IsNone;
        }

        static StationRole RoleOf(CastleContainerInfo row)
        {
            if (!row.IsStation) return StationRole.None;
            var name = row.Building.PrefabName ?? string.Empty;
            if (Has(name, "Devour")) return StationRole.Devourer;
            if (Has(name, "Brazier") || Has(name, "Bonfire")) return StationRole.Brazier;
            if (Has(name, "Stable")) return StationRole.Stable;
            if (Has(name, "Tomb") || Has(name, "Nest") || Has(name, "Spawner")) return StationRole.UnitSpawner;
            return row.IsRefinement ? StationRole.Refinement : StationRole.Workstation;
        }

        static bool Has(string s, string part) => s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>"TM_Stash_Chest_Large" → "Stash Chest Large"; good enough until localized names are wired.</summary>
        static string Pretty(string? prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return "Container";
            var p = prefab!;
            var cut = p.IndexOf('_');
            if (cut > 0 && cut <= 4) p = p.Substring(cut + 1);
            return p.Replace('_', ' ');
        }

        static void Copy(DepositOptions from, DepositOptions to)
        {
            to.Takes = from.Takes;
            to.BasePriority = from.BasePriority;
            to.Special = from.Special;
            foreach (var kv in from.ItemPriority) to.ItemPriority[kv.Key] = kv.Value;
            foreach (var kv in from.CategoryPriority) to.CategoryPriority[kv.Key] = kv.Value;
            to.BannedItems.UnionWith(from.BannedItems);
            to.BannedCategories.UnionWith(from.BannedCategories);
        }
    }
}
