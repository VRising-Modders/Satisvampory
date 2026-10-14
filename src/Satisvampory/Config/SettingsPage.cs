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
                {
                    // Spec 06: when the server does not let players leave a share, the row shows locked at ON.
                    var key = entry.Definition.Key;
                    System.Func<bool>? enable = key switch
                    {
                        "ShareBetweenMyCastles" => () => server.Snapshot().PlayersMayLeaveCastleShare,
                        "TakePartInClanShare" => () => server.Snapshot().PlayersMayLeaveClanShare,
                        _ => null,
                    };
                    var shown = enable == null ? help : help + " Locked on when the server does not let players leave it.";
                    page.Toggle(entry, label: label, help: shown, enableIf: enable);
                }
                page.Button("Open", () => admin.OpenHub?.Invoke(5), label: "Hunt log", help: "Your servants' hunts and what they brought back.");
                page.Button("Show again", () => UiTerms.ShowMarkers(ctx.Guid), label: "Show term hints (ⓘ) again",
                    help: "Brings back the small ⓘ markers you have hovered away.");

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
                    else if (entry is BepInEx.Configuration.ConfigEntry<string> text)
                        page.Text(text, multiline: true, lines: 3, label: label, help: help, scope: SettingScope.Server);
                    else
                        page.Number(entry, label: label, help: help, scope: SettingScope.Server);
                }

                page.Button("Empty", () => admin.Run?.Invoke(1, "", 0), label: "Empty all trash on this plot",
                    confirm: "Destroy everything in every trash chest on the plot you stand on?", needsServerPart: true,
                    help: "Admins only. Uses the plot you are standing on.");
                page.Text(server.GiveItem, label: "Give to storage: item", help: "Admins only. Item name, alias or id.");
                page.Number(server.GiveAmount, label: "Give to storage: amount");
                page.Button("Give", () => admin.Run?.Invoke(2, server.GiveItem.Value, server.GiveAmount.Value), label: "Give to storage",
                    needsServerPart: true, help: "Admins only. Creates the items and sends them to their Mass Deposit homes on the plot you stand on.");
                page.Info("Admin result", () => admin.LastAdminResult, refreshSeconds: 0.5);
                page.Info("Diagnostics", () => { admin.Run?.Invoke(3, "", 0); return admin.Diagnostics; }, refreshSeconds: 3);
                page.Info("Version", () => $"Satisvampory {MyPluginInfo.PLUGIN_VERSION} · island save v{Engine.Persistence.IslandSave.CurrentVersion}", refreshSeconds: 0);
                page.Button("Check", () => admin.Request?.Invoke(Net.ActionKind.ImportCheck),
                    help: "Reads the server's Satisvampory 1.x or KindredLogistics settings file and reports what would carry over. Changes nothing. Admins only.",
                    needsServerPart: true, label: "Import from Satisvampory 1.x or KindredLogistics");
                page.Button("Import", () => admin.Request?.Invoke(Net.ActionKind.ImportApply),
                    confirm: "Import the older mod's settings and chest names into Satisvampory 2.0? Run Check first. Existing 2.0 settings for a player are kept.",
                    help: "Applies the older mod's server allows, players' settings, reserves, caps, categories, per-castle switches, and turns chest names into deposit settings and belts. Admins only.",
                    needsServerPart: true, label: " ");
                page.Info("Import result", () => admin.LastResult, refreshSeconds: 0.5);

                page.Section("Help");
                page.Info("How it fits together", () => Glossary.HowItFits, refreshSeconds: 0);
                page.Button("Open", () => admin.OpenHub?.Invoke(1), label: "Mass Deposit");
                page.Button("Open", () => admin.OpenHub?.Invoke(2), label: "Belts and categories");
                page.Button("Open", () => admin.OpenHub?.Invoke(3), label: "Inventory");
                page.Button("Open", () => admin.OpenHub?.Invoke(4), label: "Find");
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

        /// <summary>Admin requests: 1 empty trash here, 2 give to storage (item, amount), 3 diagnostics.</summary>
        public System.Action<int, string, int>? Run { get; set; }

        public string LastAdminResult { get; set; } = "";

        public string Diagnostics { get; set; } = "Open this page as an admin to see them.";

        /// <summary>Opens the Satisvampory hub on a tab (set by the client half).</summary>
        public System.Action<int>? OpenHub { get; set; }
    }

    /// <summary>The glossary from spec 16, one place for every surface.</summary>
    public static class Glossary
    {
        public static readonly (string Term, string Text)[] Terms =
        {
            ("Belt", "A named list of chests and stations that you use together, like \"Ore in\" for the three chests beside your furnaces. Put a belt in a chest's or station's Input or Output and it acts on every chest on the belt."),
            ("Category", "A named set of items, like Flowers or Ore. Use categories to filter what moves, to set reserves and caps, and in Mass Deposit rules."),
            ("Input", "Where this chest or station pulls items from."),
            ("Output", "Where this chest or station sends items to."),
            ("Mass Deposit", "Where each item belongs, decided by each chest's deposit settings (what it takes, at what priority), never by its name. Quick stash, tidy and servant loot use it. \"Push: Mass Deposit\" in an Output sends items home every tick; \"Pull: reverse Mass Deposit\" in an Input takes them from wherever they are stored worst."),
            ("Deposit settings", "A chest's gear: what it takes (anything, only what's listed, nothing), its priority, its bans."),
            ("Island", "All the castles whose chests you can use from where you stand. It follows castle share and clan share."),
            ("Castle share", "Your own castles act as one: craft at one, use chests at another."),
            ("Clan share", "Every castle of every clan member taking part acts as one."),
            ("Reserve", "How many of an item stations and crafting leave untouched."),
            ("Cap", "Stations stop making an item once the island holds this many."),
            ("Loop", "Chests that feed each other in a circle. You choose whether it runs once or keeps items evenly spread."),
            ("Priority", "A chest's number for an item in its deposit settings: higher wins."),
            ("Try it", "Ask \"what if n of X\" and see where it would go or come from, in order, and which chests would not be used and why. Nothing moves."),
        };

        public const string HowItFits =
            "Every chest has deposit settings that say what belongs in it; quick stash, tidy and servant loot follow them (Mass Deposit). " +
            "Every chest and station also has an Input and an Output: lists of chests and belts it pulls from and sends to, every few seconds. " +
            "Stations pull only whole crafts and keep the island's reserves. Castle share and clan share decide which castles count as one island.";
    }
}
