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
        /// Item group a dest-typed furniture stands for (Jewel Storage → "jewels"), or null for
        /// generic furniture.
        /// </summary>
        public string? FurnitureGroup { get; set; }

        /// <summary>World position (x, z) in metres, for distance hints and tie-breaks.</summary>
        public float X { get; set; }
        public float Z { get; set; }

        public DepositOptions Deposit { get; } = new DepositOptions();

        public string DisplayName => Name.Length > 0 ? Name : FurnitureName;
    }

    /// <summary>Per-chest Advanced options (spec 01, "Advanced options").</summary>
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

        public Dictionary<ItemId, int> ItemPriority { get; } = new();

        /// <summary>Keyed by folded item group name.</summary>
        public Dictionary<string, int> GroupPriority { get; } = new();

        public HashSet<ItemId> BannedItems { get; } = new();

        /// <summary>Folded item group names.</summary>
        public HashSet<string> BannedGroups { get; } = new();

        public SpecialKind Special { get; set; } = SpecialKind.None;
    }
}
