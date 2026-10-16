# 10 — Settings

Where each part lives:

- **Me, Keys, Server, Help** are sections of Satisvampory's page in Progenitor's Mods menu. Server rows are synced from the server and editable by admins in-game; admin actions (import) are buttons there whose requests the server checks.
- **Castle, Mass Deposit, Belts and categories, Inventory, Find** are tabs of the Satisvampory hub panel, opened from keybinds and from the Stash / Satisvampory buttons beside the inventory's sort button. They need the server's per-castle and per-island state, which a config page cannot hold.

Built on Progenitor's settings pages (`ctx.Settings.Page`) and keybinds (`ctx.Keybinds`), reached from Progenitor's Mods menu and from Satisvampory's own panels. Every value is server-owned: the client shows what the server last sent and sends a change request. Rows the player may not change are shown locked with who can change them.

## Me

| Row | Default |
|---|---|
| Quick stash on double-tap sort | ON |
| Share between my castles (my plots act as one) | ON (locked when the admin forbids opting out) |
| Take part in clan share (my castles join the clan island) | ON (locked when the admin forbids opting out) |
| Stash from anywhere | OFF |
| Silent stash | OFF |
| Craft from storage (missing ingredients) | ON |
| Never pull the last stack | ON |
| Silent pull | OFF |
| Servant loot auto-stash (my castles) | ON |
| Tidy needs a second press to confirm | ON |
| Top up hearts when I visit | ON |
| Top up my island's hearts when I log in | OFF |
| Also top up hearts outside my island | OFF |
| Keybinds: Mass Deposit, Belts and categories, Inventory, Find, Tidy, Stash, Hunt log | all unbound; the player picks keys, checked against vanilla bindings |
| Show vanilla redistribution tab | OFF; transfer ownership is separate (`04-stations.md`) |
| Auto-pin Find selections | ON; pins the selected chest or station, replacing the previous Find pin |
| Hunt log | button |
| Show term hints (ⓘ) again | button |

## Help

The glossary and "How it fits together" (`16-help.md`).

## Castle

Header: plot label, level, owner, which sharing applies here (none, castle share, clan share). Off every plot: "Stand on a castle to see its settings".

| Row | Default |
|---|---|
| Logistics (lists run) | ON |
| Plot label | castle name, else "Plot n" |
| Default reserve | 10 (0 disables) |
| Inventory: reserves and caps per item and category | opens the Inventory page (`15-inventory.md`) |
| Belts and categories | opens that screen (`14-belts-categories.md`) |
| Item aliases (castle) | empty |
| Mass Deposit | opens `03-mass-deposit.md` screen |
| Feed salvage to the devourer | OFF |
| Heart fuel auto-feed | ON |
| Repeat hunts on this castle | ON when allowed |
| Exclude this castle from sharing | OFF, owner only |
| Tidy now | button |
| Need: top ten station inputs short on the island | read-only; **Show all** opens the Inventory page filtered to Short |

## Clan

| Row | Default |
|---|---|
| Castles: label, level, owner, excluded, repeat hunts, occupied | table |
| Repeat hunts: all on | button |

## Server (admin)

Same switches as 1.x's server toggles, rewritten. Every allow defaults ON except where noted.

| Row | Default |
|---|---|
| Castle share | ON |
| Players may turn castle share off | ON |
| Clan share | ON |
| Players may turn clan share off | ON |
| Allow quick stash | ON |
| Allow stash from anywhere | ON |
| Allow pull | ON |
| Allow craft from storage | ON |
| Allow servant loot auto-stash | ON |
| Allow logistics (input and output lists) | ON |
| Allow loops | ON |
| Loop move budget per tick | 12 |
| Allow salvage | ON |
| Allow spawner chests | ON |
| Allow trash | ON |
| Allow heart blood feed | ON |
| Allow repeat hunts | **OFF** |
| Repeat hunts only while a clan member is online | OFF |
| Repeat hunt success cap (display) | 99 |
| Move budget: moves per plot per tick / per tick total | 12 / 120 |
| Item aliases (server) | built-ins `be gbe pbe abe gss sgs dsi ot`, locked; admin extras |
| Empty all trash on this plot | button |
| Give to storage (item, amount → Mass Deposit on this plot) | 1.x `.adminstash` |
| Diagnostics: work-queue depth, last tick cost, item catalog summary | info rows, refreshed while the page is open |
| Allow unmodded clients | OFF |
| Satisvampory version and save version | info |
| Import from Satisvampory 1.x or KindredLogistics: **Check** (dry run: what would carry over, what would not) and **Import** | buttons; result shown in an info row |

## Who honors reserve

| Feature | Honors reserve |
|---|---|
| Input and output lists, loops | yes |
| Craft from storage, building costs | yes |
| Heart fuel | yes |
| Pull | no |
| Tidy | no |
