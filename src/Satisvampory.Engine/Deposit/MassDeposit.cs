// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Deposit
{
    /// <summary>The seven built-in Simple rows (spec 03), in default order.</summary>
    public enum BuiltInRow
    {
        PriorityChests,
        AlreadyHolding,
        ExactNames,
        ItemGroupNames,
        General,
        HoldingAnyName,
        Overflow,
    }

    /// <summary>A row in the Simple list: a built-in, a chest group, one chest, or a target from a rule.</summary>
    public sealed class DepositRow
    {
        DepositRow(BuiltInRow? builtIn, Target? target)
        {
            BuiltIn = builtIn;
            Target = target;
        }

        public BuiltInRow? BuiltIn { get; }
        public Target? Target { get; }
        public bool Enabled { get; set; } = true;

        public static DepositRow Of(BuiltInRow row) => new(row, null);
        public static DepositRow Of(Target target) => new(null, target);

        public override string ToString() => BuiltIn?.ToString() ?? Target!.ToString();
    }

    public enum TargetKind
    {
        Chest,
        ChestGroup,
        General,
        Overflow,
        Plot,
        Special,
    }

    /// <summary>Something a rule or row can point at.</summary>
    public sealed record Target(TargetKind Kind, string Name = "", string? Plot = null, SpecialKind Special = SpecialKind.None)
    {
        public override string ToString() => Kind switch
        {
            TargetKind.Chest => Plot == null ? $"chest \"{Name}\"" : $"chest \"{Name}\" @ \"{Plot}\"",
            TargetKind.ChestGroup => $"group \"{Name}\"",
            TargetKind.Plot => $"plot \"{Name}\"",
            TargetKind.Special => $"special {Special}",
            _ => Kind.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>Per-castle Mass Deposit settings: the Simple list plus an optional Advanced script.</summary>
    public sealed class MassDepositSettings
    {
        public List<DepositRow> Rows { get; } = Defaults();

        /// <summary>Simple-mode item rules ("Blood Essence → Alchemy group"); always above the rows.</summary>
        public List<(ItemSelector Selector, Target Target)> ItemRules { get; } = new();

        public string AdvancedScript { get; set; } = string.Empty;

        public static List<DepositRow> Defaults() =>
            Enum.GetValues(typeof(BuiltInRow)).Cast<BuiltInRow>().Select(DepositRow.Of).ToList();

        public void RestoreDefaults()
        {
            Rows.Clear();
            Rows.AddRange(Defaults());
        }
    }

    /// <summary>Which items a rule applies to.</summary>
    public sealed record ItemSelector(ItemSelectorKind Kind, ItemId Item = default, string Group = "")
    {
        public static ItemSelector Any { get; } = new(ItemSelectorKind.Any);

        public bool Matches(ItemId item, ItemCatalog catalog) => Kind switch
        {
            ItemSelectorKind.Any => true,
            ItemSelectorKind.Item => Item == item,
            ItemSelectorKind.Group => catalog.InGroup(item, Group),
            _ => false,
        };
    }

    public enum ItemSelectorKind
    {
        Any,
        Item,
        Group,
    }
}
