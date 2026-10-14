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

Which buttons a building shows follows what it can do with items:

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

A **belt** is a named, ordered set of containers and stations, island-wide under castle share or clan share. It replaces a 1.x line (`s3` / `r3`). Example: "Ore in" is the three chests beside the furnaces.

- Defined and managed on the **Belts and categories** screen (`14-belts-categories.md`). A belt can also be created on the spot from any list editor ("New belt…").
- A member is added by picking it (`02-io-lists.md`, "Pickers"). Order inside a belt matters only when a list says "respect order".
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
| `s#`, `r#` | Belts and input entries |
| An item name or alias ("Iron Ore") | Takes: only listed · Iron Ore |
| A category word anywhere in the name ("ore", "East gems") | Takes: only listed · those categories |
| Blank, a generic word ("misc", "chest"), or the furniture name | Takes: anything |
| Any other name | Takes: only listed · the items it holds now (1.x sent a named chest only what it already held) |
| Trailing `+`, `++`, … | Base priority 1, 2, … |
| `--word` | A ban on the item or category *word* matched |
| `overflow`, `salvage`, `spoils`, `trash`, `spawner`, `brazier`, `night`, `prox` | Special kind |
| `NS`, or `''` at start or end | Special kind Skip |

After import, nothing is parsed from names. A player who types `--copper` on a chest later just has a chest named that.

## Deposit settings (per chest)

A third control on the container panel, a small gear beside the rename field, separate from the Input and Output buttons. Tooltip "Deposit settings". It opens a side panel:

```
┌ Deposit settings · Gem vault · here ─────────────┐
│ Takes        ( ) Anything  (•) Only what's listed │
│              ( ) Nothing                          │
│ Base priority        [  2  ] (−10 … +10, 0)       │
│                                                   │
│ Items and categories                              │
│   Category: gems             [ +3 ]   ✕           │
│   Onyx Tear                  [ −2 ]   ✕           │
│   [+ Add item or category…]                       │
│                                                   │
│ Never deposit here                                │
│   Category: weapons                    ✕          │
│   [+ Add item or category…]                       │
│                                                   │
│ Special kind   [ None ▾ ]                         │
│ On belts       Gem racks ×   [+ belt]             │
│                                                   │
│ Try it: [ 200 ] [ Ruby ▾ ]  → would land here 1st │
└───────────────────────────────────────────────────┘
```

- **Takes:**
  - **Anything** is the default for a new chest, so quick stash works with no setup. It takes every item not banned, at the base priority.
  - **Only what's listed** takes just the items and categories in its list.
  - **Nothing** means the chest is never a Mass Deposit home. Use it for a station's input chest or a display chest.
- **Base priority:** an integer from −10 to +10, default 0. Higher wins.
- **Items and categories:** each entry adds its number to the base for those items. An item's own entry beats a category entry; between categories, the larger number wins. On an "Only what's listed" chest, the list is also what the chest takes.
- **Never deposit here** (bans): items or categories Mass Deposit never puts in this chest, whatever overrides or rules say. An explicit Output row pointing at this chest still delivers, with a warning.
- **Special kind:** the same dropdown as `09-special-chests.md`.
- **On belts:** belt chips, as on the Belts and categories screen.
- **Try it:** a one-line evaluator for this chest. "200 Ruby → this chest would be 1st (Category gems +3 on base 2), taking 200" or "→ not used: takes only listed items" (`03-mass-deposit.md`, "Evaluator").
