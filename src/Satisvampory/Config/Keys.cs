// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Progenitor.Plugins;
using Progenitor.Ui;

namespace Satisvampory.Config
{
    /// <summary>
    /// Keybinds (spec 10). All unbound by default so none collides with vanilla; players pick
    /// keys on the settings page or in Mods › Keybinds. Pressing one raises <see cref="Pressed"/>
    /// on the client.
    /// </summary>
    public sealed class Keys
    {
        public const string Stash = "Stash";
        public const string Tidy = "Tidy";
        public const string MassDeposit = "MassDeposit";
        public const string BeltsAndCategories = "BeltsAndCategories";
        public const string Inventory = "Inventory";
        public const string Find = "Find";
        public const string HuntLog = "HuntLog";

        public static readonly (string Id, string Label)[] All =
        {
            (Stash, "Quick stash"),
            (Tidy, "Tidy this island"),
            (MassDeposit, "Mass Deposit screen"),
            (BeltsAndCategories, "Belts and categories"),
            (Inventory, "Inventory page"),
            (Find, "Find"),
            (HuntLog, "Hunt log"),
        };

        public event Action<string>? Pressed;

        public IReadOnlyList<(string Id, string Label)> Registered { get; private set; } = Array.Empty<(string, string)>();

        Keys()
        {
        }

        public static Keys Register(PluginContext ctx)
        {
            var k = new Keys();
            if (!ctx.IsClient) return k;
            foreach (var (id, label) in All)
            {
                var captured = id;
                ctx.Keybinds.Register(id, KeyChord.None, label, () => k.Pressed?.Invoke(captured));
            }
            k.Registered = All;
            return k;
        }
    }
}
