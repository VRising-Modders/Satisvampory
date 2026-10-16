# 13 — Ops and roadmap

## Dev loop

Progenitor's dev loop ([VRising-Modders/Progenitor](https://github.com/VRising-Modders/Progenitor), `docs/dev-loop.md`): build with `dotnet build`, deploy to a test server and client, hot reload over the debug bridge.

**Never test against a live server with real players.** Use a dedicated test server, and find processes by full path, never by name.

## Test save

Test on a **copy** of a real, well-used save (several castles, full chests) running on a separate test server: its own data folder, its own ports, not listed. Before a destructive test, copy it again rather than reusing it. Machine-specific paths for the maintainer's setup live in the git-ignored `LOCAL.md`.

Satisvampory 1.x must not be loaded on the test server alongside 2.0.

## No chat commands

Satisvampory 2.0 has **no** `.` commands, not even for admins. Every admin action is a row, button or info line in the Server section of the settings page (`10-settings.md`); the server checks admin rights on each request. Headless testing uses Progenitor's dev bridge (snippet exec, keybind presses), never chat.

## Milestones

| # | Delivers | Acceptance on the test save |
|---|---|---|
| **P-a** | Progenitor P1 inventory, P4 castles and clans, P5 ids, with samples and tests | `.items`-style sample moves 50 Iron Ore between two chests on the test save, atomically; counts match the vanilla panel |
| **P-b** | Progenitor P3 material availability spike | Crafting at a tailoring bench on plot A shows and spends cloth sitting in a bare-floor chest on plot B |
| **S1** | Engine: lists, belts, categories, Mass Deposit Simple, stations whole-craft and leftovers, reserve, cap, fair share, diagnostics, persistence, 1.x import | Imported 1.x lines from the test save run; a furnace pulls only full crafts and returns leftovers when a recipe is switched off |
| **P-c** | Progenitor P6 panel sites, P7 widgets, P8 messages | Buttons appear on a chest and a furnace; list reorders round-trip |
| **S2** | Input and output panels, pickers, pick in world, plot labels, Belts and categories screen | Link a chest on plot 2 into a furnace on plot 1 from the panel without typing a name; rename a belt used by three lists and all three follow |
| **S3** | Mass Deposit screen, Simple and Advanced, preview; Inventory page | An Advanced rule moves gems to a named vault; preview names the rule |
| **S4** | Castle share and clan share, crafting hook live | Castle share: crafting on plot A uses a chest on the same owner's plot B; clan share off hides a clanmate's plots; both off sees only the standing plot, any floor |
| **S5** | Loops, both modes | A four-chest loop cycles once and sleeps; adding an item wakes it |
| **S6** | Stash, pull, find, tidy hotkey | Tidy key with confirm moves strays home; find locates across plots |
| **S7** | Servants, special chests, heart fuel | Hunt returns, loot stashed, repeat resends |
| **S8** | Settings pages complete, admin port, localization, package | Every 1.x `.sg` allow has a Server row; package installs clean |

Autoloot is a separate mod with its own milestones; it starts once Progenitor's settings and keybinds are live-verified.

## Implementation baseline (2026-10-14)

The table records the pre-tab implementation baseline. Current UI and live qualification are described in the revision section below.

| Area | State |
|---|---|
| Engine (lists, Mass Deposit by settings, sharing, stations, loops, specials, servant logic, heart feed, evaluator, import) | done, unit-tested |
| Plugin: settings page (Me, Keys, Server, Help), player settings sync, admin import Check / Import | done |
| Plugin: conveyor tick, special chests, heart blood feed on visit and login, stash from anywhere | done |
| Plugin: Input / Output / Deposit settings panels, picker, Try it, recipe switches, loop mode, Empty trash | done |
| Plugin: hub (Castle, Mass Deposit, Belts and categories, Inventory, Find), Stash button, tooltip line | done |
| Item catalog from the game | done (Progenitor item catalog) |
| Crafting and building from shared storage | done (Progenitor material sources, opt-in `[Materials] NativeSeams` on server and clients); build, craft, repair and heart upgrades |
| Double-tap sort to stash | done (Progenitor sort event) |
| Servants (loot stash, repeat hunts, notices, hunt log, throne targeting) | done (Progenitor servants API) |
| Braziers fed from their Input list | done |
| Pick in world, Locate | done (Progenitor network-id resolver) |
| Data-table inventory, term tooltips, tip cards | done (Progenitor P7b widgets): sortable, hideable table with inline Reserve / Cap edits; ⓘ terms; tips for the hub, Mass Deposit, Belts, Inventory, Input/Output and Deposit settings. Belts and categories as master-detail with repeat pick in world; category chips filter the inventory. Live inventory (snapshot then deltas every 2 s while the tab is open), sort and hidden columns remembered per player, shared-island tip |
| Stash skipping the hotbar exactly; stash/pull/tidy refused while downed, dead, in PvP combat | done (Progenitor slot filters and player state) |

## Not done yet (spec audit, 2026-10-14)

- Stash notice breakdown on hover (Progenitor notices have no hover).
- Localization keys for every string (`00-product.md`, `16-help.md`).
- Hunt log "since last logout" (needs a logout time; the log keeps 72 hours).
- Diagnostics per list row (they are per container today).
- Servant "Sent" notice survival chance (Progenitor does not expose it).
- Belts and categories: sort by recent use, Used by entries that jump to the use, editing a member item's reserve and cap from its chip, adding every item of a vanilla item category.
- Inventory: a category header's own reserve and cap in grouped view (bulk edits set per-item overrides).
- Find: autofill while typing (an ambiguous name shows a pick list with icons instead).


## Tab/default-belt revision (2026-10-15)

Current contract: `01-containers.md`, `02-io-lists.md`, `04-stations.md` and `14-belts-categories.md` supersede the prior edge-button UI and undirected belt membership design.

Implemented:
- Native in-window tabs through Progenitor `AttachPanelTabs`: Container uses Inventory / Input / Output / Accepts; Station uses Production / Input / Output. Inventory and Production retain the game controls.
- Accepts basic/Advanced controls with read-only game restriction descriptions; no Try, recipe picker or belt membership controls in the container editor.
- Native redistribution preference defaults off. `AutomationSuspended` guards prevent engine automation from competing while native redistribution owns transfers.
- Station input protection is conservative: any nonempty queue or unknown queue state protects all inputs. Known idle stations may return leftovers after the grace period; this is not per-ingredient reservation accounting.
- Default Belt supplies new ordinary stations on demand, honors filters/scope, and preserves existing or intentionally empty lists. Bench material-source policy uses configured Input; repairs retain scope-wide storage.
- Chest transfers check Accepts. Refining leftovers prefer configured input homes by forward Mass Deposit rank, then general Mass Deposit.
- Custom and Default Belts use directed Output suppliers and Input receivers. The full-window manager shows both directions with plot labels and supports directional add/remove/reorder and custom-belt duplication. Filters apply at both ends, including material sources and previews; Accepts cannot edit membership.
- Save v2 stores connections without a standalone member list. V1 migration adds restricted counterpart connections, preserving routes, filters, order and loops; cleared/default connections stay removed. Existing 1.x imports create directed connections.

Remaining qualification: wider multi-castle logistics, optional native redistribution and extended play still need live validation. Chest, furnace and Anvil observations do not qualify every station or the full product. A follow-up isolated periodic server stalls to repeated belt scans and inventory reads; endpoint-local lookups and tick-scoped snapshots removed the multi-second stalls. Extended-play qualification remains open.

Validation reported for this revision: **178 engine tests and 294 focused Progenitor tests pass; plugin and Core builds pass**. Chest and furnace native/custom tabs were tested at **3840 x 1080** and **1920 x 1080**. Anvil Production/Input mounting was observed; restricted chest Accepts was also checked at **2560 x 1440**. Tabs preserve the native font and silver baseline. Custom content scrolls within a height bounded above the bottom HUD; the wide belt manager implements the same bottom clearance. The final manager detail view and bottom HUD clearance were observed at 1920 x 1080 after the server-stall fix. Physical controller input and native 4K remain unqualified.

Automatic defaults exclude servant coffins and servant-equipment containers using Progenitor native classification. Existing saved connections are preserved because their origin cannot be distinguished safely from manual configuration.

### Logistics scaling

Belt pair checks inspect the counterpart container directly instead of rebuilding and sorting all connections. Each conveyor pass builds incoming source lists once and uses read-through inventory/recipe snapshots shared with deposit ranking. Transfers and destruction invalidate affected inventory, capacity and reservation snapshots, including aliased station inventories. Snapshots never survive a pass. Large-belt allocation bounds and mutation invalidation have regression coverage.

Live scaling check on the copied multi-castle save: a 60-second capture after the fix averaged 29.37 server frames/s, with a 165.9 ms maximum frame. Chest Input and Default Belt details loaded during the follow-up session without the prior connection warning. This is a short-session result, not extended-play qualification.

### Circular-loop load check

Observed on an isolated copied save: ten unrestricted chests in a directed ring, each starting and finishing at 75% stack capacity (10 full stacks plus one half stack in 14 slots). The 105,000-item stock was conserved. Cycle once became idle; even distribution recovered from a deliberate imbalance and became idle. The test exposed and corrected neighbor-only balancing that could strand uneven shares. Mode changes now wake a sleeping loop, and the idle threshold uses each item's catalog stack size.

A 60-second live capture with the corrected loop averaged 29.47 server FPS, versus 29.45 at baseline; maximum frame time was 168.3 ms versus 166.7 ms, with p99 36.9 ms versus 34.2 ms. These measurements qualify this fixture, not arbitrary loop sizes or long-duration multiplayer load. The temporary fixture plugin was removed after verification.

### Connected Core updates

Hub tab registration calls Progenitor directly, and Find control registration uses concrete interface overloads. Mod-defined generic UI helpers instantiated with Core types create reverse loader dependencies on the embedded .NET runtime and prevent the mod from collecting before Core. Keep future UI helpers concrete or in Progenitor.

Verified with the corrected Progenitor retirement/config cleanup: repeated client and server Core updates completed while connected, including after opening Find. Find reopened, accepted input and returned matches afterward. Bootstrap updates still require a process restart.
