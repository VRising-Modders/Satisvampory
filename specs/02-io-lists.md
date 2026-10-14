# 02 — Input and output lists

## Where they live

When a player opens a container or station, the vanilla panel gains two small icon buttons drawn with the game's button sprite:

- **Left edge: Input** (arrow into a box). Tooltip "Input: where this pulls from".
- **Right edge: Output** (arrow out of a box). Tooltip "Output: where this sends to".

A third control, the **Deposit options** gear beside the rename field, holds the chest's Mass Deposit priority and bans. It is not a list; see `01-containers-groups.md`, "Advanced options".

The input and output buttons each show a small count badge (number of entries) and turns the game's warning red when an entry is blocked. Clicking opens a side panel anchored to that edge of the vanilla window, styled like the vanilla tooltip and side panels. Only one side panel is open at a time.

Guests see the buttons and can open the panels read-only, with the tooltip "Clan members can change logistics".

## A list

```
┌ Input · Ore chest · here ──────────── [?] ┐
│ Pull order  ( Respect list order ▾ )      │
│                                           │
│ ≡ 1  ▣ Chest group: Ore in (3)      ✕     │
│ ≡ 2  ▢ Iron Ore · Plot 2 · 40 m      ✕    │
│        filter: Iron Ore                   │
│ ≡ 3  ◆ Mass Deposit (reverse)       ✕     │
│                                           │
│ [+ Add…]   [◎ Pick in world]              │
│                                           │
│ Status: moving · 320 Iron Ore last minute │
└───────────────────────────────────────────┘
```

- The list grows without a limit the player can hit in practice. It scrolls past about eight rows.
- Rows reorder by dragging the grip (≡) or with up and down buttons on the focused row.
- Each row has an optional **filter**: anything (default), or one or more items or item groups, plus exclusions. The filter is set from the row's expand arrow.
- Each row shows its live state: idle, moving, or a blocked reason (`Diagnostics` below).
- Every edit is a request to the server. The panel updates from the server's reply, not optimistically.

### Entry kinds

| Entry | In an input list | In an output list |
|---|---|---|
| A container | Pull from it | Send to it |
| A station | Pull its finished products | Send it recipe inputs |
| A chest group | Pull from its members | Send to its members |
| **Mass Deposit** | Pull from storage in **reverse** Mass Deposit priority (worst home first) | Send each item to its Mass Deposit home (1.x "RR") |
| A special chest (by its name, for example "Trash", "Salvage") | Pull from it where its kind allows | Send to it |

Special chests appear in pickers under their own names and kinds (`09-special-chests.md`), so "send leftovers to Trash · here" is one pick.

## Pull order (input lists)

| Option | Meaning |
|---|---|
| **Respect list order** (default) | Drain entry 1 before entry 2, and so on. Inside a group, respect member order. |
| **Reverse Mass Deposit priority** | Ignore list order. Take from wherever the item sits worst by Mass Deposit priority (overflow and generic dumps first, the item's best home last), so pulling never undoes a good sort. |

## Send order (output lists)

| Option | Meaning |
|---|---|
| **Fill in order** (default) | Fill entry 1 until it is full or refuses the item, then entry 2. |
| **Spread evenly** | Split by free space among the entries that accept the item. |

**Mass Deposit** as an output entry always uses Mass Deposit's own order for that item.

## What moves

- An output edge moves what the destination accepts and the row filter allows. A chest destination accepts anything unless filtered; a station accepts only inputs of recipes switched on (`04-stations.md`).
- An input edge pulls what this container needs: for a station, its recipe inputs; for a chest, anything the row filter allows. A chest with an unfiltered input row of "Mass Deposit (reverse)" would pull everything, so the editor asks for a filter on that combination.
- Reserve: a castle **reserve** (default 10 per item, by item or item group) is the island stock that feeding stations, heart fuel, and crafting from storage leave behind. Chest-to-chest moves keep items on the island, so reserve does not limit them. Pulls by the player (`07-stash-pull-find-tidy.md`) ignore it.
- **Production cap** per item or item group: stop feeding a station whose product is already at the cap on the island.
- Several stations wanting the same item share it: each round feeds every station one more whole craft, up to the feed target.
- Nothing moves on a raided plot, out of a skip chest, or into a full one.

An edge is defined once. An output entry on A pointing at B and an input entry on B pointing at A are the same edge, shown in both panels and marked "set on A".

## Pickers

**Add…** opens a searchable picker:

- Type to filter by container name, plot label, station name, chest group, item held, or item wanted. Results autofill as you type.
- Rows: icon, name, plot label, distance, and a hint such as "holds 320 Plank" or "wants Iron Ore".
- Sections: Here, other plots (grouped by plot label), Chest groups, Special chests, Mass Deposit.
- Rows that would be refused (this container, a skip chest, a raided plot, out of sharing scope) are greyed with the reason. A loop is **not** refused; it is flagged and sends the player to the loop settings (`05-loops.md`).

**◎ Pick in world**, for when finding the chest in a list is harder than walking to it:

1. The panel closes. A prompt reads "Interact with a container or station to add it. Esc to cancel."
2. The next container or station the player interacts with is added. Vanilla does not open its panel.
3. A notice confirms "Added Ore · here to Input of Furnace · here", the original panel reopens, and both ends flash.
4. Picking a chest on a plot outside the sharing scope is refused with the reason.

## Diagnostics

Each row and each panel button can say why nothing moves, highest first:

| Code | Shown as |
|---|---|
| `raided` | Castle is under attack |
| `disabled` | Logistics are off for this castle, or the server disallows them |
| `out-of-scope` | This chest is on a plot outside the current sharing island |
| `no-source` | Nothing in the input holds what is needed |
| `not-enough` | Not enough for one full craft (`04-stations.md`) |
| `reserve` | Only the castle reserve remains (n) |
| `cap` | The island already holds the production cap (n) |
| `sink-full` | Destination has no free slot |
| `not-wanted` | No recipe switched on uses this item |
| `loop-waiting` | Loop finished its cycle; waiting for a change (`05-loops.md`) |
