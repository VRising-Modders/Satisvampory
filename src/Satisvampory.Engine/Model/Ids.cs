// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace Satisvampory.Engine.Model
{
    /// <summary>Stable id of a container or station (spec 11, P5). Mirrors Progenitor's 128-bit id.</summary>
    public readonly record struct ContainerId(Guid Value)
    {
        public static ContainerId New() => new(Guid.NewGuid());
        public override string ToString() => "c-" + Value.ToString("N");
    }

    /// <summary>Which inventory of a container. Chests use Main; stations have Main (input) and Output.</summary>
    public enum InvPart
    {
        Main,
        Output,
    }

    /// <summary>A castle plot, identified by its heart.</summary>
    public readonly record struct PlotId(ulong Value)
    {
        public override string ToString() => "p" + Value.ToString("x");
    }

    public readonly record struct ClanId(ulong Value);

    /// <summary>An item prefab guid.</summary>
    public readonly record struct ItemId(int Guid)
    {
        public override string ToString() => Guid.ToString();
    }

    public readonly record struct ItemStack(ItemId Item, int Amount);

    public enum NodeKind
    {
        Chest,
        Station,
        /// <summary>A castle heart: has lists, but is never a Mass Deposit home or a default source.</summary>
        Heart,
    }

    /// <summary>Special kinds from spec 09. Set from Deposit options, or imported from 1.x names.</summary>
    public enum SpecialKind
    {
        None,
        Overflow,
        Skip,
        Salvage,
        Spawner,
        Brazier,
        BrazierNight,
        BrazierProximity,
        Trash,
        Spoils,
    }

    public static class SpecialKinds
    {
        /// <summary>Kinds that are never a Mass Deposit home unless a row or rule names them.</summary>
        public static bool IsSpecialPurpose(SpecialKind kind) =>
            kind is SpecialKind.Salvage or SpecialKind.Spawner or SpecialKind.Brazier
                or SpecialKind.BrazierNight or SpecialKind.BrazierProximity
                or SpecialKind.Trash or SpecialKind.Spoils;

        public static bool IsBrazier(SpecialKind kind) =>
            kind is SpecialKind.Brazier or SpecialKind.BrazierNight or SpecialKind.BrazierProximity;
    }

    internal static class Guard
    {
        public static void NonNegative(int value, string name)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(name, value, "must be zero or more");
        }
    }
}
