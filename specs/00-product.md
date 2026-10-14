# 00 — Product

Satisvampory 2.0 is castle logistics for V Rising, rebuilt on Progenitor. Every chest and station has an **input list** and an **output list** that players build in the game's own panels. There are no chat commands and chest names carry no routing codes.

Rewritten 2026-10-13. It replaces an earlier, unpublished Flow Map design.

## Shape

- One DLL, `[ProgenitorSide(Side.Both)]`. The server decides every item move; the client draws what the server sent and sends requests back. A client never moves an item.
- Dependencies: BepInEx and Progenitor. Nothing else.
- **License: AGPL-3.0, original work.** 2.0 is a complete rework that owes nothing to 1.x or KindredLogistics, so no attribution to either is expected or included. 1.x may be read to learn *what* it does (behavior, settings, defaults), never copied: no code, comments, data tables (group member lists, prefab-name JSON, alias tables), strings, or doc sentences carry over. Group membership is regenerated from the game's own catalog.
- **Progenitor first.** Any inventory, station, castle, clan, or UI capability the mod needs is added to Progenitor as public API and called from the mod (`11-progenitor-api.md`). The mod does not reach into ECS for something Progenitor should own.
- Settings use Progenitor's settings pages and keybinds (`10-settings.md`).

## What is in

| Feature | Spec |
|---|---|
| Containers, groups, plot labels | `01-containers-groups.md` |
| Input and output lists on chests and stations, pickers | `02-io-lists.md` |
| Mass Deposit (Simple and Advanced) | `03-mass-deposit.md` |
| Stations: recipe toggles, whole-craft pulls, leftover return | `04-stations.md` |
| Loops | `05-loops.md` |
| Sharing: castle share (one player's plots) and clan share (the whole clan) | `06-castle-sharing.md` |
| Quick stash, pull, find, tidy (hotkey) | `07-stash-pull-find-tidy.md` |
| Servants | `08-servants.md` |
| Special chests | `09-special-chests.md` |
| Settings and admin settings | `10-settings.md` |
| What Progenitor must provide | `11-progenitor-api.md` |
| Protocol, persistence, 1.x import | `12-protocol-persistence.md` |
| Build, test save, milestones | `13-ops-roadmap.md` |

## What is out

- **Autoloot / scoop.** It is a separate Progenitor mod, a port of GroundScoop with its settings and scoop key in the Progenitor UI. If both mods are loaded, the autoloot mod may ask Satisvampory for storage counts (bag caps counted against storage) through a small optional interface. Neither mod requires the other.
- **Supply lend** (starter kit, build cover, heart upgrade staging). Sharing makes staging unnecessary: building and crafting read shared storage directly. Heart blood feed stays, now triggered by visiting a plot or logging in (`09-special-chests.md`).
- **Chat commands.** None for players. A minimal admin set exists for headless testing only (`13-ops-roadmap.md`).
- **`s#` / `r#` routing by name.** Read once by the 1.x importer (`12-protocol-persistence.md`), then ignored.
- The Flow Map, helper NPC, and blueprints from the September spec. They are deferred, not cancelled, and may come back on top of the input and output lists.
- Loading beside 1.x, KindredLogistics, or any other logistics plugin.

## Who may do what

| Action | Who |
|---|---|
| See a container's lists | Anyone allied with the heart |
| Change lists, groups, Mass Deposit, castle settings | A clan member of the heart owner's clan, or the owner |
| Clan settings | Any clan member |
| Server settings | Admin (`adminlist.txt` plus in-game admin auth) |

Guests can look but not change, and never cause cross-plot pulls.

## Frozen rules

These carry over from 1.x and are not simplified away.

- **Mass Deposit is the one deposit order.** Quick stash, tidy, servant loot, station leftovers, and "Mass Deposit" outputs all use it. No feature invents a second order.
- Nothing moves on a raided plot.
- Castle hearts are never a Mass Deposit home and never a source by default. They have input and output lists like any container; only an output entry set on the heart itself takes items out of it.
- Skip chests are never a source or a dest.
- A station pulls inputs only when it can complete at least one craft (`04-stations.md`).
- Heart fuel fills every unlocked pad. A level-N heart has N pads; read the level-indexed heart data.
- Never patch the servant mission success-chance methods; their parameter shape breaks the trampoline and kills servants.
