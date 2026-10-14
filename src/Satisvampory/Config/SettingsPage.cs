// SPDX-License-Identifier: AGPL-3.0-only
using Progenitor.Plugins;
using Progenitor.Ui;

namespace Satisvampory.Config
{
    /// <summary>
    /// Satisvampory's page in Progenitor's Mods menu (spec 10): Me, Keys, Server (admin-editable,
    /// shown to everyone), and Help (spec 16). Castle and clan settings need per-heart storage and
    /// live in Satisvampory's own panels once Progenitor's panel sites land.
    /// </summary>
    public static class SettingsPage
    {
        public static void Build(PluginContext ctx, ServerConfig server, PlayerConfig player, Keys keys, AdminPanel admin)
        {
            ctx.Settings.Page(page =>
            {
                page.Section("Me");
                foreach (var (entry, label, help) in player.Rows)
                    page.Toggle(entry, label: label, help: help);

                if (keys.Registered.Count > 0)
                {
                    page.Section("Keys");
                    foreach (var (id, label) in keys.Registered)
                        page.Keybind(id, label: label, help: "Unbound until you pick a key.");
                }

                page.Section("Server");
                foreach (var (entry, label, help) in server.Rows)
                {
                    if (entry is BepInEx.Configuration.ConfigEntry<bool> flag)
                        page.Toggle(flag, label: label, help: help, scope: SettingScope.Server);
                    else
                        page.Number(entry, label: label, help: help, scope: SettingScope.Server);
                }

                page.Info("Version", () => $"Satisvampory {MyPluginInfo.PLUGIN_VERSION} · island save v{Engine.Persistence.IslandSave.CurrentVersion}", refreshSeconds: 0);
                page.Button("Check", () => admin.Request?.Invoke(Net.ActionKind.ImportCheck),
                    help: "Reads the server's Satisvampory 1.x settings file and reports what would carry over. Changes nothing. Admins only.",
                    needsServerPart: true, label: "Import from Satisvampory 1.x");
                page.Info("Import result", () => admin.LastResult, refreshSeconds: 0.5);

                page.Section("Help");
                foreach (var (term, text) in Glossary.Terms)
                    page.Info(term, () => text, refreshSeconds: 0);
            });
        }
    }

    /// <summary>
    /// Admin rows on the settings page. Buttons call <see cref="Request"/> (set by the client
    /// half to send the request); the server checks admin rights and replies into
    /// <see cref="LastResult"/>.
    /// </summary>
    public sealed class AdminPanel
    {
        public System.Action<Net.ActionKind>? Request { get; set; }

        public string LastResult { get; set; } = "Not run yet.";
    }

    /// <summary>The glossary from spec 16, one place for every surface.</summary>
    public static class Glossary
    {
        public static readonly (string Term, string Text)[] Terms =
        {
            ("Belt", "A named list of chests and stations you use together, like \"Ore in\" for the chests beside your furnaces. Put a belt in an Input or Output and it acts on every chest on it."),
            ("Category", "A named set of items, like Flowers or Ore. Used in filters, deposit settings, reserves, caps and Mass Deposit rules."),
            ("Input", "Where this chest or station pulls items from."),
            ("Output", "Where this chest or station sends items to."),
            ("Mass Deposit", "Where each item belongs, decided by each chest's deposit settings, never its name. Push: Mass Deposit sends items home; Pull: reverse Mass Deposit lets a station take what it needs from wherever it is stored worst."),
            ("Deposit settings", "A chest's gear: what it takes (anything, only what's listed, nothing), its priority and its bans."),
            ("Island", "All the castles whose chests you can use from where you stand, following castle share and clan share."),
            ("Castle share", "Your own castles act as one: craft at one, use chests at another."),
            ("Clan share", "Every castle of every clan member taking part acts as one."),
            ("Reserve", "How many of an item stations and crafting leave untouched."),
            ("Cap", "Stations stop making an item once the island holds this many."),
            ("Loop", "Chests that feed each other in a circle; it runs once or keeps items evenly spread."),
            ("Try it", "Ask \"what if n of X\" and see where it would go or come from, and which chests would not be used and why. Nothing moves."),
        };
    }
}
