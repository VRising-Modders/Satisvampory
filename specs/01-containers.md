# 01 — Containers, belts, categories, plot labels

## Containers

A **container** is any placed building with an inventory a player can open: chests, cabinets, bureaus, storage furniture, treasury chests. Floor type does not matter; treasury floor is **not** required for anything in 2.0.

Each container has:

| Field | Meaning |
|---|---|
| Identity | Stable id that survives restart (`12-protocol-persistence.md`) |
| Name | What the player typed on the nameplate. For display only; never read for routing. |
| Plot | The castle territory it stands on |
| Input list, output list | `02-io-lists.md` |
| Special kind | None, or one of the kinds in `09-special-chests.md` |
| Belts | Every belt it is on |

**Stations** (refinement and crafting stations, spawners, the devourer, braziers) and **castle hearts** also have input and output lists. Station rules are in `04-stations.md`, hearts in `09-special-chests.md`.

Which routing tabs a building shows follows what it can do with items:

| Building | Input | Output |
|---|---|---|
| Chests and storage | yes | yes |
| Refinement and crafting stations | yes | yes (finished products) |
| Castle heart | yes (where its blood comes from) | yes, explicit only |
| Unit spawners (tombs, nests, …) | yes | no: they make units, not items |
| Stables | yes: feed items (stat upgrades) | yes: only the item the mount generates over time, never feed |
| Servant coffins, prison cells | no lists: they hold no items of their own | |

Stable and spawner inventory layout is read from the game's data but not yet checked live. It is confirmed in Progenitor's P-a live test, and this table is corrected if it differs.

## Plot labels

Every place a container or station is shown in the UI (lists, pickers, search, notices) carries its plot so two chests named "Ore" on different castles can be told apart:

```
Ore · Plot 2 (Hollow Keep) · 14 m
```

- **Plot label**: the castle's name if it has one, else "Plot n", where n is a stable number per clan assigned in the order the hearts were first seen. The heart owner can rename the plot label in the Castle settings. It is a Satisvampory label, not the vanilla castle name.
- The standing plot shows as **here**.
- Distance is shown when the player is on the same plot.
- Unnamed containers show the furniture name: "Large Chest · here · 6 m".

## Belts

A **belt** connects ordered suppliers and receivers, island-wide under castle share or clan share. Output to a belt makes a supplier; Input from it makes a receiver. Membership exists only through these connections. It replaces a 1.x line (`s3` / `r3`). Example: "Ore in" is the three chests beside the furnaces.

- Defined and managed on the **Belts and categories** screen (`14-belts-categories.md`). A belt can also be created on the spot from any list editor ("New belt…").
- Add a supplier or receiver by picking it (`02-io-lists.md`, "Pickers"). Each direction has its own order and filters; both ends must allow an item. Accepts never changes membership.
- A container may be on any number of belts.
- Deleting a belt removes it from every list that used it, and those lists show what was removed.
- Under sharing, a belt may hold members from several plots. Each member row shows its plot label.

## Categories

A **category** is a set of items (ore, gems, planks, flowers, …). Categories are used as filters in lists, in deposit options, as Mass Deposit match terms, and for reserves and caps.

The built-in categories and their aliases (`be`, `gbe`, `dsi`, …) cover the same ground as 1.x. Their membership is generated fresh from the game's item catalog by 2.0's own rules; no 1.x list is copied. Clan members may edit a castle copy, and admins edit server aliases. Built-ins can be restored.

The UI always says **Belt** or **Category**, never just "group". The two are never offered in the same field, so "Ore" the belt and "ore" the category cannot be confused.

## Names are just names

A chest's name is for people: it shows in lists, pickers and notices. It never decides where items go. Mass Deposit reads only the chest's **deposit settings** (below).

The 1.x name codes, and the plain names 1.x read as purposes, are converted **once** by the importer (`12-protocol-persistence.md`):

| 1.x name | Becomes |
|---|---|
| `s#`, `r#` | Belt Output (supplier) and Input (receiver) connections |
| An item name or alias ("Iron Ore") | Takes: only listed · Iron Ore |
| A category word anywhere in the name ("ore", "East gems") | Takes: only listed · those categories |
| Blank, a generic word ("misc", "chest"), or the furniture name | Takes: anything |
| Any other name | Takes: only listed · the items it holds now (1.x sent a named chest only what it already held) |
| Trailing `+`, `++`, … | Base priority 1, 2, … |
| `--word` | A ban on the item or category *word* matched |
| `overflow`, `salvage`, `spoils`, `trash`, `spawner` | Special kind |
| `brazier` (with `night` / `prox`) | No special kind: the chest is added to the Input of every brazier on its plot and takes only the fuel it holds; the words are dropped |
| `NS`, or `''` at start or end | Special kind Skip |

After import, nothing is parsed from names. A player who types `--copper` on a chest later just has a chest named that.

## Accepts (per chest)

Chest windows use **Inventory · Input · Output · Accepts**. Inventory retains the game's inventory view. Accepts governs incoming belts and automatic deposits; manual deposits bypass mod rules but still obey game restrictions.

Basic view: **Anything / Only what's listed / Nothing**, an item/category list, and an add picker. Keep configured special-purpose status visible. Advanced settings, collapsed initially, contains base priority, per-item/category adjustments, exclusions and special kind. Existing advanced rules still apply while collapsed. No Try control or belt membership editor here.

Display game restrictions as a locked constraint, for example **Materials only — game restriction**. Read them through Progenitor, never infer them from a furniture name or make them editable. Player rules may narrow them, never widen them. Transfers revalidate eligibility and capacity.

Names remain display labels under the existing routing contract; accepting this tab design does not silently reinterpret existing names. Use game-owned tab, font, slot, button and tooltip assets through Progenitor. Closing/changing the target closes the editor; stale replies cannot reopen it. Guests can inspect but cannot edit.
