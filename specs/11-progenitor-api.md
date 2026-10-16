# 11 — What Progenitor must provide

Rule: when Satisvampory needs to read or change an inventory, a station, a castle, a clan, or a vanilla panel, the capability is added to Progenitor as public, documented API with a sample and a test, and Satisvampory calls it. Satisvampory carries no ECS item-moving code of its own.

State on 2026-10-13: Progenitor has `ctx.Items.Grant` / `Equip` (player bag only), `ctx.Castles` (plots, tiles, buildings with kind, floor census, raided, level), a UI toolkit (panels and controls, one vanilla attach site: `MapWarEventInfo`), settings pages, keybinds, and config sync. Everything below is new.

## P1 Inventory (server) — `ctx.Inventory`

Any entity with an inventory: chests, stations (input and output separately), player bags, servants, hearts' fuel.

| Call | Notes |
|---|---|
| `Read(container)` → slots (item, amount, slot index, max stack) | Snapshot, no allocation per slot in the hot path |
| `Count(container, item)` / `CountMany(containers, item)` | |
| `FreeSpace(container, item)` → how many more fit | Respects stack size and slot filters (treasury refusing Blood Essence) |
| `TryAdd(container, item, amount)` → added | Partial adds allowed, reports the remainder |
| `TryRemove(container, item, amount)` → removed | |
| `Transfer(from, to, item, amount)` → moved | Atomic: never destroys or duplicates on partial failure |
| `TransferAll(from, to, filter)` | For stash and tidy |
| `Watch(container)` → change events | Drives loop wake-up and lazy rescans |
| Station inventories: `InputOf(station)`, `OutputOf(station)` | |

## P2 Stations (server) — `ctx.Stations`

| Call | Notes |
|---|---|
| `Recipes(station)` → recipe id, inputs, outputs, enabled | Refinement and crafting stations |
| `SetRecipeEnabled(station, recipe, on)` | Vanilla toggle where it exists |
| `CraftsPossible(recipe, sources)` → n | Pure count over a source set |
| `IsWorking(station)`, `Progress(station)` | |

## P3 Station material availability (server + client) — `ctx.Stations.MaterialSource`

The seamless-sharing hook. A mod registers a provider that, for a player and a station, returns the set of containers the station may draw from. Progenitor patches the vanilla merged-storage read so that:

- The client's displayed "available" counts for a recipe or build cost reflect that set (sent by the server).
- The server's consumption takes from that set, in an order the provider supplies.

One provider at a time, with a clear error if two mods register. Building placement costs and repair use the same hook.

## P4 Castles and clans

| Call | Notes |
|---|---|
| `ctx.Clans.Of(user)`, `Members(clan)`, `Castles(clan)` | New module |
| `ICastleHandle.Containers` / `Stations` with stable ids | Extends `Buildings` |
| `ICastleHandle.Name` and Satisvampory's plot label store | |
| Container nameplate read and write | |
| Allied / guest test for a user and a heart |
| Heart fuel: inventory of the heart, level, unlocked pad count from the level-indexed data | For heart blood feed |
| Events: player entered a plot (territory change), player finished loading in after login | Heart feed triggers |

## P5 Stable container identity

An id that survives restart and does not reuse a destroyed chest's id. Satisvampory stores lists against it.

## P6 Vanilla panel attachment (client) — new `VanillaUiSite` values

| Site | Use |
|---|---|
| `ContainerPanel` (left edge, right edge, beside rename) | Native in-window Inventory/Input/Output/Accepts tabs; game acceptance constraints |
| `StationPanel` (refinement and workstation, both edges) | Native Production/Input/Output tabs; queue/active demand; redistribution ownership |
| `InventoryPanel` (beside sort) | Stash button, double-click sort detection |
| `ThronePanel` | Castle dropdown |
| `InventoryTooltip` | "In storage" line |

Each site reports open, close, and which entity the panel is showing. Missing after a game update: the site hides and logs one line.

## P7 UI widgets (client)

- **Ordered list** with drag reorder, up and down buttons, per-row expand, per-row remove, scroll.
- **Entity and item picker** with search, autofill, sections, greyed rows with reasons, icons, and multi-select.
- **Pick in world** repeat mode (stay in pick mode until Esc) for adding several belt members.
- **Master-detail layout** with a tab strip (Belts and categories screen).
- **Data table**: sortable columns (multi-key), hideable columns, row expand, multi-select, inline number edit with pending state, grouped rows with subtotals, virtualised scrolling for hundreds of rows (Inventory page).
- **Pick in world** mode: capture the next interact on a container or station, suppress its panel, Esc to cancel.
- **Code text area** with line numbers, colour spans, and line errors (Mass Deposit Advanced).
- **Notifications** in the vanilla style, and **Locate** ping on a world entity.
- Tabs, tooltips, count badges.

## P8 Networking

Typed, chunked request and reply messages above the packet cap, plus subscriptions (snapshot then deltas) for an open panel.

## Order

P1, P4, P5 first (the server engine needs them). P6 and P7 next (they gate every visible feature). P3 is the riskiest seam and gets an early spike on the test save. P2 and P8 as the engine needs them.

### Native tab hosting

Progenitor's `AttachPanelTabs` mounts Satisvampory content inside the native Container and Station windows: **Inventory / Input / Output / Accepts** for containers and **Production / Input / Output** for stations. Progenitor owns native tab styling and hitboxes, content mounting, selection, target changes, layout alongside redistribution, and restoration of vanilla content on close/unload. Inventory and Production retain the game controls; Satisvampory supplies the custom content and routing policy.

Game acceptance restrictions are displayed read-only. The native redistribution preference defaults off; `AutomationSuspended` guards prevent competing engine transfers while native redistribution owns them. Station queue protection is conservative: any nonempty or unknown queue protects all inputs, while known idle stations may return leftovers after the grace period. This does not provide per-ingredient reservation accounting.

Chest and furnace native/custom tabs have been tested live at 3840 x 1080 and 1920 x 1080, including hitboxes. Crafting-bench validation remains pending; these checks are not full station or product qualification.
