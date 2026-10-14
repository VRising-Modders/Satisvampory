// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Deposit
{
    public enum TargetKind
    {
        Chest,
        Belt,
        /// <summary>Chests that take anything.</summary>
        General,
        Overflow,
        Plot,
        Special,
    }

    /// <summary>Something an override or rule can point at.</summary>
    public sealed record Target(TargetKind Kind, string Name = "", string? Plot = null, SpecialKind Special = SpecialKind.None)
    {
        public override string ToString() => Kind switch
        {
            TargetKind.Chest => Plot == null ? $"chest \"{Name}\"" : $"chest \"{Name}\" @ \"{Plot}\"",
            TargetKind.Belt => $"belt \"{Name}\"",
            TargetKind.Plot => $"plot \"{Name}\"",
            TargetKind.Special => $"special {Special}",
            _ => Kind.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>
    /// Island-wide Mass Deposit settings (spec 03). The chests' own deposit settings decide where
    /// items go; these only override them: Simple overrides ("Blood Essence → Alchemy belt first")
    /// and an optional Advanced rule script.
    /// </summary>
    public sealed class MassDepositSettings
    {
        /// <summary>Simple overrides, placed before the chests' own priorities, in order.</summary>
        public List<(ItemSelector Selector, Target Target)> ItemRules { get; } = new();

        public string AdvancedScript { get; set; } = string.Empty;
    }

    /// <summary>Which items a rule applies to.</summary>
    public sealed record ItemSelector(ItemSelectorKind Kind, ItemId Item = default, string Category = "")
    {
        public static ItemSelector Any { get; } = new(ItemSelectorKind.Any);

        public bool Matches(ItemId item, ItemCatalog catalog) => Kind switch
        {
            ItemSelectorKind.Any => true,
            ItemSelectorKind.Item => Item == item,
            ItemSelectorKind.Category => catalog.InCategory(item, Category),
            _ => false,
        };
    }

    public enum ItemSelectorKind
    {
        Any,
        Item,
        Category,
    }
}
