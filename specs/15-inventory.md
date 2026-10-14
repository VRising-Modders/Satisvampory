# 15 — Inventory page

A sortable table of every item on the island, with its reserve and production cap shown and edited in place. This is where a player sees what they have, what is short, and what is capped.

## Opening it

- Keybind (unbound by default, set in Settings).
- An **Inventory** row on the Castle settings page.
- An **Inventory** button added beside the Stash button on the player's inventory panel.
- From the Belts and categories screen: **Show in inventory** on a category.

Scope is the island the player reaches (`06-castle-sharing.md`). The header says which, for example "Hollow Keep + 2 plots (clan share)". Off every plot it shows the last island the player stood on, read-only, with the time of the snapshot.

## Columns

| Column | Shows | Sort |
|---|---|---|
| **Item** | Icon and name | A–Z |
| **Category** | The item's categories as chips. Clicking one filters the table to it. | A–Z by first category |
| **Total** | Count in chests on the island. Stations and hearts are not counted. | number |
| **Here** | Count on the standing plot | number |
| **Chests** | How many chests hold it | number |
| **In stations** | Count sitting in station inputs and outputs | number |
| **Reserve** | The reserve for this item, editable. A muted value means it comes from its category or the castle default; typing a value sets an item override; clearing it returns to the inherited value. | number |
| **Spare** | Total minus reserve: what stations and crafting may use | number |
| **Cap** | The production cap, editable the same way. "—" means unlimited. | number, unlimited last |
| **Demand** | What switched-on station recipes want for their feed target | number |
| **Status** | One badge: **Short** (demand above spare), **Below reserve**, **At cap**, **No stock**, or blank | by severity |

Every column sorts ascending or descending by clicking its header. Shift-click adds a second sort key. The chosen sort and visible columns are remembered per player. Columns can be hidden; Item and Total always show.

## Filters

- A search box for item name, alias, or category.
- Category chips, combinable.
- Toggles: **Only short**, **Only at cap**, **Only with my overrides** (reserve or cap set on the item), **Only here**.
- A plot filter (all plots, or one plot label).
- **Group by category**: rows fold under category headers with subtotals. An item in two categories appears under each, and the grand total counts it once.

## Row actions

Expanding a row shows a per-plot breakdown: plot label, count, chests, and the chest that holds the most. Actions:

- **Take…**: pull into bags (`07-stash-pull-find-tidy.md`).
- **Locate**: ping every chest holding it.
- **Where does it go?**: opens the Mass Deposit preview for the item (`03-mass-deposit.md`).
- **Category…**: opens the category in the Belts and categories screen.
- Inline edits to **Reserve** and **Cap**. Each edit is a request to the server, and the cell shows a spinner until the server confirms.

## Bulk edits

With rows selected (shift- or ctrl-click), **Set reserve…** and **Set cap…** apply a value or clear the overrides on all of them. The same works on a category header in grouped view, which sets the category's reserve or cap rather than per-item overrides.

## Reserves and caps

Reserves and caps are castle rules (`02-io-lists.md`). They are set here or on a category, not on the Castle settings page, which only keeps the default reserve and links here.

| Rule | Item override | Category value | Default |
|---|---|---|---|
| Reserve | wins | the largest category value | castle default (10) |
| Cap | wins | the smallest category value | unlimited |

## Live updates

The page subscribes on open and unsubscribes on close (`12-protocol-persistence.md`): a snapshot, then deltas at most twice a second. Rows that change flash briefly. With hundreds of items the table is virtualised, so only visible rows are drawn.

## Who may change what

Anyone allied with the heart can view. Clan members of the heart owner can edit reserves and caps. Guests see the cells locked.
