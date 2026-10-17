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
            public float Facing;
            /// <summary>Which source slots a move may draw from; a player's bag skips the action bar.</summary>
            public InventorySlotFilter SourceSlots;
            /// <summary>The game's station API knows it (coffins and unit stations it does not).</summary>
            public bool GameStation;
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

        static readonly Dictionary<int, (long Revision, ulong Heart, List<CastleContainerInfo> Rows)> RowCache = new();

        /// <summary>
        /// A castle's containers and stations. Background work (ticker, servant loot, heart feed,
        /// craft from storage) passes <paramref name="cached"/> and reuses rows while the castle's
        /// <see cref="ICastleHandle.StructureRevision"/> holds, i.e. until a container or station is
        /// built or destroyed. Names can change without the revision moving, so player requests read
        /// fresh (and refresh the cache). Main thread only.
        /// </summary>
        public static List<CastleContainerInfo> Rows(ICastleHandle castle, bool cached = false)
        {
            var key = castle.Territory.Index;
            var revision = castle.StructureRevision;
            var heart = castle.Heart.Index == 0 ? 0UL : ((ulong)(uint)castle.Heart.Index << 32) | (uint)castle.Heart.Version;
            if (cached && revision != 0 && RowCache.TryGetValue(key, out var hit) && hit.Heart == heart && hit.Revision == revision) return hit.Rows;
            var rows = castle.Containers().Concat(castle.Stations()).ToList();
            RowCache[key] = (revision, heart, rows);
            return rows;
        }

        /// <summary>Drop every cached row (plugin unload).</summary>
        public static void ForgetRows() => RowCache.Clear();

        public ProgenitorWorld(PluginInventory inventory, Progenitor.Stations.PluginStations? stations, IEnumerable<ICastleHandle> castles, Func<EngineId, DepositOptions?> deposit, bool cachedRows = false, Engine.Persistence.IslandState? logistics = null)
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

                foreach (var row in Rows(castle, cachedRows))
                {
                    if (row.Id.IsNone) continue;
                    var id = new EngineId(row.Id.Value);
                    var servantKnown = inventory.TryIsServantContainer(row.Entity.ToEntity(), out var servantContainer, out _);
                    var servantSlots = servantKnown && servantContainer;
                    // Coffins hold a servant's things and unit stations (Tomb, Vermin Nest) their spawn
                    // ingredients: stations, never chests that deposits, belts or loops may fill.
                    var station = row.IsStation || servantSlots || IsUnitStation(row);
                    var info = new ContainerInfo(id, plot, station ? NodeKind.Station : NodeKind.Chest, row.Nameplate)
                    {
                        FurnitureName = Pretty(row.Building.PrefabName),
                        BuildingPrefab = row.Building.PrefabGuid,
                        X = CastleGeometry.TileToWorld(row.Building.TileX),
                        Z = CastleGeometry.TileToWorld(row.Building.TileY),
                        Role = servantSlots ? StationRole.Coffin : IsUnitStation(row) && !row.IsStation ? StationRole.UnitSpawner : RoleOf(row),
                        DefaultBeltEligible = servantKnown && !servantContainer,
                    };
                    if (_deposit(id) is { } d) Copy(d, info.Deposit);
                    if (row.IsStation)
                    {
                        var routing = stations?.RedistributionOwnership(row.Entity.ToEntity());
                        info.NativeRoutingKnown = routing?.Known == true;
                        info.NativeAutoRouteItems = routing?.AutoRouteItems == true;
                        info.NativeRoutingReason = routing?.Reason ?? "station API is unavailable";
                    }
                    else if (station)
                    {
                        // Coffins and unit stations have no game redistribution to defer to.
                        info.NativeRoutingKnown = true;
                        info.NativeAutoRouteItems = false;
                    }
                    _nodes[id] = new Node { Info = info, Main = row.Main, Output = row.Output, Castle = castle, Entity = row.Entity.ToEntity(), Facing = row.Building.Rotation * 90f, GameStation = row.IsStation };
                    list.Add(info);
                }
            }
            if (logistics != null) InitializeDefaults(logistics);
        }

        public void InitializeDefaults(Engine.Persistence.IslandState state)
        {
            var infos = _nodes.Values.Select(n => n.Info).ToList();
            if (state.DefaultBeltMigrationDue)
            {
                Engine.Lists.DefaultBelt.Initialize(state.Graph, Array.Empty<ContainerInfo>()); // the belt itself first
                foreach (var id in Engine.Lists.DefaultBelt.MigrateExisting(state.Graph, infos)) state.Touched.Add(id);
                state.DefaultBeltMigrationDue = false;
                state.DefaultBeltMigrated = true;
            }
            Engine.Lists.DefaultBelt.Initialize(state.Graph, infos);
            if (state.DefaultOutputsDue)
            {
                foreach (var id in Engine.Lists.DefaultBelt.RemoveDefaultStationOutputs(state.Graph, infos)) state.Touched.Add(id);
                state.DefaultOutputsDue = false;
                state.DefaultBeltMigrated = true; // so the ticker saves the island
            }
            if (state.AutoInputsDue)
            {
                foreach (var id in Engine.Lists.DefaultBelt.MarkAutoInputs(state.Graph, infos)) state.StationsToClear.Add(id);
                state.AutoInputsDue = false;
                state.DefaultBeltMigrated = true;
            }
            if (state.MisfiledStationsDue)
            {
                // Coffins and unit stations used to count as chests, so stash and loops filled them.
                foreach (var c in infos.Where(c => c.Kind == NodeKind.Station && c.Role is StationRole.Coffin or StationRole.UnitSpawner))
                    state.StationsToClear.Add(c.Id);
                state.MisfiledStationsDue = false;
                state.DefaultBeltMigrated = true;
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
        public static IEnumerable<EngineId> ContainerIds(IEnumerable<ICastleHandle> castles, bool cachedRows = false)
        {
            foreach (var c in castles)
            {
                if (!c.Enabled) continue;
                if (!c.HeartId.IsNone) yield return new EngineId(c.HeartId.Value);
                foreach (var row in Rows(c, cachedRows))
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

        /// <summary>Native item/category acceptance for the panel, independent of furniture names and free slots.</summary>
        public InventoryAcceptance DescribeAcceptance(EngineId id) =>
            TryRef(id, InvPart.Main, out var r) ? _inv.DescribeAcceptance(r)
                : new InventoryAcceptance(default, "container inventory is unavailable");

        /// <summary>Live queue and reservation protection. Unknown reservations require retaining every input item.</summary>
        public Progenitor.Stations.StationDemand StationDemand(EngineId id) =>
            _stations != null && _nodes.TryGetValue(id, out var n) && n.Info.Kind == NodeKind.Station
                ? _stations.Demand(n.Entity)
                : new Progenitor.Stations.StationDemand(Progenitor.Stations.StationKind.None,
                    Array.Empty<Progenitor.Stations.StationQueuedCraft>(), true, "station is unavailable");

        public StationInputState StationInputs(EngineId id)
        {
            var demand = StationDemand(id);
            // Demand is known only for an empty workstation queue or idle refinement with no current recipe.
            return demand.IsKnown && demand.Queue.Count == 0
                ? StationInputState.Idle : StationInputState.Unknown;
        }

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
        public float FacingOf(EngineId id) => _nodes.TryGetValue(id, out var n) ? n.Facing : 0;

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

        public int Transfer(EngineId from, InvPart fromPart, EngineId to, ItemId item, int amount, bool takeBusyInputs = false)
        {
            if (amount <= 0 || !TryRef(from, fromPart, out var a) || !TryRef(to, InvPart.Main, out var b)) return 0;
            // Re-read native ownership at the mutation boundary, including callers outside ConveyorTick (heart feed).
            if (NativeRoutingSuspends(from) || NativeRoutingSuspends(to)) return 0;
            if (_nodes.TryGetValue(from, out var source)
                && (fromPart == InvPart.Main || source.Info.Role == StationRole.Workstation)
                && (source.Info.Role == StationRole.Workstation || source.Info.Role == StationRole.Refinement)
                && !(takeBusyInputs && source.Info.Role == StationRole.Refinement)
                && !StationDemand(from).IsKnown) return 0;
            var slots = fromPart == InvPart.Main && _nodes.TryGetValue(from, out var n) ? n.SourceSlots : InventorySlotFilter.All;
            var r = _inv.Transfer(a, b, item.Guid, amount, slots);
            if (r.Done == 0) NoteFailure($"transfer {item.Guid} x{amount}: {r.Outcome} {r.Reason}");
            return r.Ok ? r.Done : 0;
        }

        bool NativeRoutingSuspends(EngineId id) => _nodes.TryGetValue(id, out var node)
            && node.Info.Kind == NodeKind.Station && node.GameStation
            && (_stations == null || _stations.RedistributionOwnership(node.Entity).SuspendAutomation);

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
            r = part == InvPart.Output && n.Info.Kind == NodeKind.Station && n.Info.Role != StationRole.Workstation ? n.Output : n.Main;
            return !r.IsNone;
        }

        static StationRole RoleOf(CastleContainerInfo row)
        {
            if (!row.IsStation) return StationRole.None;
            var name = row.Building.PrefabName ?? string.Empty;
            if (Has(name, "Devour")) return StationRole.Devourer;
            if (Has(name, "Brazier") || Has(name, "Bonfire")) return StationRole.Brazier;
            if (Has(name, "Stable")) return StationRole.Stable;
            if (Has(name, "Coffin")) return StationRole.Coffin;
            if (Has(name, "Tomb") || Has(name, "Nest") || Has(name, "Spawner")) return StationRole.UnitSpawner;
            return row.IsRefinement ? StationRole.Refinement : StationRole.Workstation;
        }

        static bool IsUnitStation(CastleContainerInfo row) => Has(row.Building.PrefabName ?? string.Empty, "UnitStation");

        static bool Has(string s, string part) => s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>"TM_Stash_Chest_Large" → "Stash Chest Large"; good enough until localized names are wired.</summary>
        static string Pretty(string? prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return "Container";
            if (prefab == "TM_ResearchStation_T01") return "Research Desk";
            if (prefab == "TM_ResearchStation_T02") return "Study";
            if (prefab == "TM_ResearchStation_T03") return "Athenaeum";
            // "TM_SpecialStation_PrisonCell" -> "Prison Cell", "TM_UnitStation_NetherGate" -> "Nether Gate".
            var parts = prefab!.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !NoiseWords.Contains(w) && !(w.Length <= 3 && (w[0] == 'T' || w[0] == 't') && w.Skip(1).All(char.IsDigit)))
                .Select(SplitCamel).ToList();
            return parts.Count == 0 ? prefab!.Replace('_', ' ') : string.Join(" ", parts);
        }

        /// <summary>
        /// "ServantMission_Strongblade_CarversLoggingOutpost" → "Carvers Logging Outpost (Strongblade)": a readable
        /// stand-in until the game's own mission name reaches the server.
        /// </summary>
        internal static string PrettyMission(string? prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return "a hunt";
            var parts = prefab!.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !w.Equals("ServantMission", StringComparison.OrdinalIgnoreCase)
                    && !(w.Length <= 3 && (w[0] == 'T' || w[0] == 't') && w.Skip(1).All(char.IsDigit)))
                .Select(SplitCamel).ToList();
            if (parts.Count == 0) return prefab!.Replace('_', ' ');
            var place = parts[parts.Count - 1];
            return parts.Count == 1 ? place : $"{place} ({string.Join(" ", parts.Take(parts.Count - 1))})";
        }

        static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "TM", "BP", "Castle", "SpecialStation", "CraftingStation", "RefinementStation", "UnitStation",
            "SalvageStation", "ResearchStation", "Workstation", "Stash", "Container",
        };

        static string SplitCamel(string word)
        {
            var sb = new System.Text.StringBuilder(word.Length + 4);
            for (var i = 0; i < word.Length; i++)
            {
                if (i > 0 && char.IsUpper(word[i]) && char.IsLower(word[i - 1])) sb.Append(' ');
                sb.Append(word[i]);
            }
            return sb.ToString();
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
