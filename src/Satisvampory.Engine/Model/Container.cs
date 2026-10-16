// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;

namespace Satisvampory.Engine.Model
{
    /// <summary>
    /// What the engine knows about one container or station. Filled by the plugin's adapter from
    /// Progenitor's castle and inventory reads; never by the engine itself.
    /// </summary>
    public sealed class ContainerInfo
    {
        public ContainerInfo(ContainerId id, PlotId plot, NodeKind kind, string name)
        {
            Id = id;
            Plot = plot;
            Kind = kind;
            Name = name ?? string.Empty;
        }

        public ContainerId Id { get; }
        public PlotId Plot { get; }
        public NodeKind Kind { get; }

        /// <summary>The player-typed nameplate. Blank is allowed.</summary>
        public string Name { get; set; }

        /// <summary>Vanilla furniture name ("Large Chest"), used when <see cref="Name"/> is blank.</summary>
        public string FurnitureName { get; set; } = string.Empty;

        /// <summary>
        /// Category a dest-typed furniture stands for (Jewel Storage → "jewels"), or null for
        /// generic furniture.
        /// </summary>
        public string? FurnitureGroup { get; set; }

        /// <summary>World position (x, z) in metres, for distance hints and tie-breaks.</summary>
        public float X { get; set; }
        public float Z { get; set; }

        public DepositOptions Deposit { get; } = new DepositOptions();

        /// <summary>May receive automatic Default Belt connections. Does not remove or override configured lists.</summary>
        public bool DefaultBeltEligible { get; set; } = true;

        /// <summary>What kind of station this is, for the special jobs (spec 09). None for chests and hearts.</summary>
        public StationRole Role { get; set; } = StationRole.None;

        /// <summary>Whether native redistribution ownership was successfully read for this station.</summary>
        public bool NativeRoutingKnown { get; set; }
        /// <summary>The game's InventoryRouteParent.AutoRouteItems flag; do not change native routes.</summary>
        public bool NativeAutoRouteItems { get; set; }
        /// <summary>Why native ownership is unknown, for diagnostics.</summary>
        public string NativeRoutingReason { get; set; } = string.Empty;
        /// <summary>Competing conveyor automation must pause on native-owned or unreadable stations.</summary>
        public bool AutomationSuspended => Kind == NodeKind.Station && (!NativeRoutingKnown || NativeAutoRouteItems);

        public string DisplayName => Name.Length > 0 ? Name : FurnitureName;
    }

    /// <summary>What a chest takes when someone uses Mass Deposit (spec 01, "Deposit settings").</summary>
    public enum Takes
    {
        /// <summary>Anything not banned, at the base priority. The default, so stash works with no setup.</summary>
        Anything,
        /// <summary>Only the items and categories listed in its priorities.</summary>
        OnlyListed,
        /// <summary>Never a Mass Deposit home.</summary>
        Nothing,
    }

    /// <summary>
    /// Per-chest deposit settings (spec 01). These, not the chest's name, decide where Mass Deposit
    /// puts an item.
    /// </summary>
    public sealed class DepositOptions
    {
        public const int MinPriority = -10;
        public const int MaxPriority = 10;

        int _base;

        public int BasePriority
        {
            get => _base;
            set => _base = value < MinPriority ? MinPriority : value > MaxPriority ? MaxPriority : value;
        }

        public Takes Takes { get; set; } = Takes.Anything;

        /// <summary>Per item: added to the base priority. Listing an item also makes an OnlyListed chest take it.</summary>
        public Dictionary<ItemId, int> ItemPriority { get; } = new();

        /// <summary>Per category, keyed by folded name; same meaning as <see cref="ItemPriority"/>.</summary>
        public Dictionary<string, int> CategoryPriority { get; } = new();

        public HashSet<ItemId> BannedItems { get; } = new();

        /// <summary>Folded category names.</summary>
        public HashSet<string> BannedCategories { get; } = new();

        public SpecialKind Special { get; set; } = SpecialKind.None;
    }
}
