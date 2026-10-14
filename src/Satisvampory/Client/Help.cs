// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Satisvampory.Config;

namespace Satisvampory.Client
{
    /// <summary>
    /// In-game help (spec 16): the glossary as hover explanations with ⓘ markers, and one-time
    /// tip cards the first time each surface opens. The Help page on the settings screen lists the
    /// same words, so every explanation has one source.
    /// </summary>
    internal static class Help
    {
        public const string HubTip = "sv.tip.hub";
        public const string ListsTip = "sv.tip.lists";
        public const string DepositTip = "sv.tip.deposit";
        public const string InventoryTip = "sv.tip.inventory";
        public const string MassDepositTip = "sv.tip.massdeposit";
        public const string BeltsTip = "sv.tip.belts";

        static readonly (string Id, string Title, string Body)[] Tips =
        {
            (HubTip, "Satisvampory",
                "Castle settings, Mass Deposit, belts and categories, the island inventory and Find live here. Hover a word with a small ⓘ to read what it means."),
            (ListsTip, "Input and Output",
                "Input is where this pulls from; Output is where it sends to. Add chests or belts, drag to reorder. Stations pull only whole crafts."),
            (DepositTip, "Deposit settings",
                "These decide where Mass Deposit puts things: what this chest takes, its priority and its bans. The chest's name never matters."),
            (MassDepositTip, "Mass Deposit",
                "Stash sends each item to the chest whose deposit settings rate it highest. Overrides and rules here change that for the whole island; Try it shows what would happen without moving anything."),
            (BeltsTip, "Belts and categories",
                "A belt is a named list of chests and stations you use together; put it in any Input or Output. A category is a named set of items for filters, reserves and caps."),
            (InventoryTip, "Inventory",
                "Everything on your island. Click a header to sort, Shift-click to add a second key. Edit Reserve or Cap in place; clear a cell to go back to the default."),
        };

        internal static void Attach(SatisContext ctx, PlayerConfig player)
        {
            UiTerms.Define(ctx.Guid, Glossary.Terms.Select(t => new KeyValuePair<string, string>(t.Term, t.Text)));
            foreach (var t in Tips) ctx.Hud.DefineTip(t.Id, t.Title, t.Body);

            void Sync() => UiTerms.SetMarkers(ctx.Guid, player.Rows.First(r => r.Entry.Definition.Key == "ShowTermHints").Entry.Value);
            Sync();
            player.Changed += Sync;
            ctx.Track(new OnDispose(() => player.Changed -= Sync));
        }

        /// <summary>Shows a tip card beside <paramref name="anchor"/> unless the player has dismissed it.</summary>
        internal static void Tip(SatisContext ctx, string id, IUiPanel anchor)
        {
            var t = Tips.FirstOrDefault(x => x.Id == id);
            if (t.Id == null || ctx.Hud.IsTipSeen(id)) return;
            ctx.Hud.Tip(t.Id, t.Title, t.Body, new UiTipOptions { Anchor = anchor });
        }
    }
}
