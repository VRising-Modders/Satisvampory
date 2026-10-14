# 13 — Ops and roadmap

## Dev loop

Progenitor's dev loop ([VRising-Modders/Progenitor](https://github.com/VRising-Modders/Progenitor), `docs/dev-loop.md`): build with `dotnet build`, deploy to a test server and client, hot reload over the debug bridge.

**Never test against a live server with real players.** Use a dedicated test server, and find processes by full path, never by name.

## Test save

Test on a **copy** of a real, well-used save (several castles, full chests) running on a separate test server: its own data folder, its own ports, not listed. Before a destructive test, copy it again rather than reusing it. Machine-specific paths for the maintainer's setup live in the git-ignored `LOCAL.md`.

Satisvampory 1.x must not be loaded on the test server alongside 2.0.

## Headless admin commands

For testing without a client only, admin-gated, one group: `.sv version`, `.sv dump <container>` (lists, groups, last moves), `.sv tick`, `.sv import`. Players never need chat.

## Milestones

| # | Delivers | Acceptance on the test save |
|---|---|---|
| **P-a** | Progenitor P1 inventory, P4 castles and clans, P5 ids, with samples and tests | `.items`-style sample moves 50 Iron Ore between two chests on the test save, atomically; counts match the vanilla panel |
| **P-b** | Progenitor P3 material availability spike | Crafting at a tailoring bench on plot A shows and spends cloth sitting in a bare-floor chest on plot B |
| **S1** | Engine: lists, groups, Mass Deposit Simple, stations whole-craft and leftovers, reserve, cap, fair share, diagnostics, persistence, headless commands, 1.x import | Imported 1.x lines from the test save run; a furnace pulls only full crafts and returns leftovers when a recipe is switched off |
| **P-c** | Progenitor P6 panel sites, P7 widgets, P8 messages | Buttons appear on a chest and a furnace; list reorders round-trip |
| **S2** | Input and output panels, pickers, pick in world, plot labels | Link a chest on plot 2 into a furnace on plot 1 from the panel without typing a name |
| **S3** | Mass Deposit screen, Simple and Advanced, preview | An Advanced rule moves gems to a named vault; preview names the rule |
| **S4** | Castle share and clan share, crafting hook live | Castle share: crafting on plot A uses a chest on the same owner's plot B; clan share off hides a clanmate's plots; both off sees only the standing plot, any floor |
| **S5** | Loops, both modes | A four-chest loop cycles once and sleeps; adding an item wakes it |
| **S6** | Stash, pull, find, tidy hotkey | Tidy key with confirm moves strays home; find locates across plots |
| **S7** | Servants, special chests, heart fuel | Hunt returns, loot stashed, repeat resends |
| **S8** | Settings pages complete, admin port, localization, package | Every 1.x `.sg` allow has a Server row; package installs clean |

Autoloot is a separate mod with its own milestones; it starts once Progenitor's settings and keybinds are live-verified.
