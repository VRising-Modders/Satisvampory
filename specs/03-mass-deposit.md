# 03 — Mass Deposit

Mass Deposit decides where an item "lives". It is the one deposit order: quick stash, tidy, servant loot, station leftovers, and any output list entry of "Mass Deposit" use it. Reversed, it is also a pull order (`02-io-lists.md`).

Scope: the island the player reaches (`06-castle-sharing.md`). One settings set orders the whole island: the standing owner's under castle share, the clan's under clan share, the castle's own when unshared. Ties across plots prefer the plot the player is standing on.

The settings screen opens from the Castle settings page, from the Output panel's Mass Deposit row, and from a keybind. It has two modes, switched by a tab. Both modes edit the same underlying priority list; Simple is a view of the common case and Advanced adds rules on top.

## Simple mode

An ordered priority list. Top wins. Rows reorder by drag, like any list.

Default list, which reproduces 1.x behavior:

| # | Row | Means |
|---|---|---|
| 1 | Priority chests | Chests whose effective priority for the item (base plus item priority, `01-containers-groups.md`) is above 0, and that match or already hold it; higher first |
| 2 | Already holding it | A named or grouped chest that matches the item and already holds some |
| 3 | Exact names | Chest name is the item's name or an exact alias ("Iron Ore", "dsi") |
| 4 | Item group names | Chest name is an item group the item belongs to ("ore", "gems"), or dest-typed furniture of that kind (Jewel Storage) |
| 5 | General chests | Blank or generic names ("chest", "misc", "everything", …) |
| 6 | Holding it, any name | A chest whose name does not match but already holds the item |
| 7 | Overflow | Chests named or marked overflow |

Inside every row, chests are ordered by effective priority (higher first), then already holding the item, then the plot the player stands on.

Two **priority chests** at the same effective priority split deposits between them; other rows fill in order.

Players may also insert:

- **A chest group** row ("Ore in"), which places that group's members at that rank for the items they accept.
- **A single chest** row, which pins one chest at that rank.
- **A rule row** per item or item group ("Blood Essence → Alchemy group"), which in Simple mode always sits at the top.

Each row has an on/off switch. Turning off "General chests" means stash never dumps into blank chests.

Rows 1 to 7 cannot be deleted, only turned off or moved. Restore defaults puts them back.

**Preview**: a search box at the bottom. Typing an item shows where the next stack of it would go, the runner-up, and why ("rank 3, exact name"). This replaces 1.x `.s why`.

## Advanced mode

A short rule script, edited in a text area with syntax colour and line errors. The rules run top to bottom and change the Simple list for the items they match. The script is saved per castle. An empty script means Simple mode only.

```
# Blood Essence never goes to treasury chests
for "Blood Essence":  only group "Alchemy", then general

# Gems: the vault first, then the jewel cabinets, never overflow
for itemgroup gems:   first chest "Gem vault" @ "Hollow Keep"
                      then group "Jewel cabinets"
                      never overflow

# Keep plot 3 out of general dumping
for any:              never plot "Plot 3" when rank = general

# Seeds stay on the plot they were stashed from
for itemgroup seeds:  prefer here

# Reorder the defaults for consumables
for itemgroup consumables: move "Item group names" above "Exact names"
```

Grammar:

| Clause | Effect |
|---|---|
| `for <selector>:` | Which items the rule applies to: `"Item name"`, `itemgroup <name>`, `any`. |
| `first <target>` / `then <target>` | Place targets at the top, in order, for these items. |
| `only <targets>` | Use only these targets for these items. Nothing else is a home. |
| `never <target>` | Remove a target. `never overflow`, `never general`, `never plot "Plot 3"`. |
| `move "<row>" above\|below "<row>"` | Reorder Simple rows for these items. |
| `prefer here` / `prefer plot "<label>"` | Tie-break toward a plot. |
| `when rank = <row>` | Limit a clause to one Simple row. |

Targets: `chest "<name>"` (optionally `@ "<plot label>"`), `group "<chest group>"`, `general`, `overflow`, `plot "<label>"`, `special "<kind>"`.

Rules are checked when saved: unknown chests, groups, and plots are errors with a line number, and the save is refused. A rule that matches nothing is a warning. The Simple tab shows "+ n advanced rules" when a script is active, and the preview names the rule that decided.

The script compiles to the same priority structure the engine runs. There is no loop, variable, or arithmetic; it cannot hang the server.

## Fixed constraints

- Skip chests, castle hearts, stations, and raided plots are never homes.
- Vanilla treasury chests refuse plain Blood Essence. Mass Deposit never targets them with it; Greater Blood Essence is fine.
- Special chests are homes only when a rule or row names them explicitly.
- A chest that bans the item or one of its item groups (Deposit options) is never its home, whatever the rules say.
