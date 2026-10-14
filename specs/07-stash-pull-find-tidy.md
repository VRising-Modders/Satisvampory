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
