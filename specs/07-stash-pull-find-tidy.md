# 07 — Quick stash, pull, find, tidy

All four use Mass Deposit (`03-mass-deposit.md`) and the sharing island (`06-castle-sharing.md`). None has its own order.

## Quick stash

Dump the player's inventory, except the hotbar, into Mass Deposit homes.

- Triggers: double-tap the vanilla sort key or double-click the sort button (1.x behavior), a **Stash** button added beside the inventory sort button, and an optional keybind (unbound by default).
- Off-plot stash (standing on no clan plot): player setting **Stash from anywhere** (default OFF) plus admin allow.
- Bat form allowed. Downed, dead, PvP combat, unallied, or raided: refused with a notice.
- Notice "Stashed 14 stacks into 6 chests" with a breakdown on hover, unless **Silent stash** is on. One-second cooldown.

## Pull

Take items from storage into bags.

- From any list or picker row showing "holds n": **Take…** asks for an amount.
- From the **Find** window (below): pull an item, or one stack (or n) of each item in a category.
- Order: reverse Mass Deposit priority. Ignores reserve. Never skip chests or hearts.
- **Never pull the last stack** (player, default ON) leaves each chest's last stack of that item.
- **Silent pull** hides the breakdown.

## Find

A small search window, opened by keybind (default unbound; set it in Settings) or from the inventory panel's Stash button menu.

- Type an item, category, or chest name. Autofill, with icons.
- Item results: every chest holding it, grouped by plot label with heart level, standing plot first as **here**, with counts, **Take…**, and **Locate** (a ping on the chest for ten seconds).
- Chest results: name, plot, what it holds, **Locate**.
- Ambiguous names show a pick list; nothing is guessed.
- The inventory tooltip gains "In storage: 1,240 (3 chests)" when on a plot.

## Tidy (hotkey)

Restack items already in chests toward their Mass Deposit homes.

- Trigger: **keybind** (default unbound until the player picks one in Settings, so it never fires by accident), and a **Tidy now** button on the Castle settings page.
- Confirm: the first press shows "Tidy Hollow Keep? Press again to confirm" for three seconds. A player setting turns the confirm off.
- Scope: standing plot, or the sharing island. Same gates as quick stash. One tidy at a time per scope. Ignores reserve.
- Sources and dests:

| Chest | Source | Dest |
|---|---|---|
| Heart, skip | no | no |
| Overflow, spoils | yes | no |
| Any chest in an input or output list or on a belt | **no** | yes, only when it is the item's best home |
| Special chests (salvage, trash, spawner) | no | no |
| Anything else, any floor | yes | yes |

- Moves an item only to a strictly better home.
- Notice "Tidied 312 items (18 kinds) across 9 chests" or "Nothing to tidy".

## Find: ranked search and room preview

Ctrl+F opens Find (rebindable). Search items, named containers, station types, and stations capable of producing an item, including disabled recipes and items with zero stock. Rank exact names first, then prefixes, words and substrings; availability breaks equal matches.

The search box stays visible above both stages and receives focus when Ctrl+F opens Find. Typing shows up to six ranked suggestions after a short debounce. Enter (or Find) expands the match list; selecting a suggestion can go directly to its locations. Editing the query from either stage returns to suggestions.

The first list selects a matching item or named chest/station. The second list selects a physical chest/station and distinguishes stored stock from production capability. Keep the query and selection when returning from a preview.

Preview starts only through **View Object**; selecting a location does not start it. The plot scope offers **This plot** and **All clan plots**. With clan plots off, search only the current plot. With it on, search the player's and clan's authorized plots and label every location with its plot name. Search visibility does not grant resource-pull permissions or change sharing policy.

Preview is a five-second, read-only camera visit to the selected container's front, with a slow orbit and manual camera rotation. Its invisible observation anchor cannot walk or interact. Escape or timeout returns to the real character and restores Find, where the selected location can be pinned. Preview lifecycle, scene visibility and camera restoration belong in Progenitor. Return and clean up on unload, disconnect, target loss or failure; never leave a temporary observer in a save.

Find uses labeled dropdown filters with stable geometry: Plots (This plot / All clan plots), Match (Partial name by default / Full name), and Show (All results / Containers only / Items only). Filters appear with the match list; the location stage retains their choices and the search box, keeping View Object visible without scrolling. No automatic preview option. Containers includes stations. With partial matching off, match full names or exact aliases. Exclude construction item definitions such as walls and floors. Research stations use their player-facing names (Research Desk, Study, Athenaeum). Append `(here)` to the current plot label.

Pin only the selected location, or the sole location if there is exactly one. The player setting **Auto-pin Find selections** defaults ON: selecting a location pins it immediately. Selecting a match with exactly one location selects and pins that location; multiple locations wait for the player's choice. Turning the setting off leaves the manual **Pin location** action available. Clearing selections or refreshing suggestions does not pin anything. Replace the previous Find pin. Show the pin in the world, map and minimap for up to 60 seconds; remove it within 3 meters in three dimensions, so being underneath an upstairs target does not count as arrival. Preview hides the game HUD, including abilities, health and blood, and restores its prior visibility on return.
