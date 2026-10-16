# 05 — Loops

A **loop** is a path of edges that returns to where it started: chest 1 → chest 2 → … → chest 1, including chest 1 → chest 1 through any number of chests in between. Loops are allowed. 1.x refused them because they emptied castles; 2.0 allows them with explicit modes and a guard.

## Detection

The server finds loops whenever the graph changes and gives each loop an id. A loop shows in every member's Input and Output panel as a ring icon with "Loop · 4 chests", and the add picker flags a pick that would close one ("This closes a loop — choose how it runs") instead of refusing it.

## Modes

Set per loop from any member's panel. The default when a loop is first closed is **Cycle once**. Changing the mode starts a fresh bounded pass, including after the loop has gone idle.

| Mode | Behavior |
|---|---|
| **Cycle once** | Items move around the loop until each item has passed every member once, then the loop stops. It sleeps until something in the loop changes: an item added or removed by anything other than the loop, a member added or removed, or a filter changed. Then it runs one more cycle. |
| **Even distribution** | The loop moves items until every member holds an even share of each item it accepts (by free space, rounded to stacks), then stops. It starts again when the balance is off by more than one stack. |
| **Off** | The edge that closes the loop is not run. The rest of the path runs normally. |

Even distribution routes surplus along configured, item-permitted edges toward downstream shortages. It can pass surplus through a chest that already has its share; comparing neighboring inventories alone can strand an uneven distribution around a long ring. The idle check uses the item catalog’s stack size.

## Guards

- Moves inside one loop are budgeted per tick (default 12 per loop) so a loop cannot starve the rest of the castle.
- An item moved by a loop is not moved again by the same loop in the same tick.
- If a loop is still moving after 60 s of cycling with no change from outside, it is stopped with the status `loop-waiting` and a log line. That catches filter combinations that never settle.
- Admin setting **Allow loops** (default ON). Off: every loop edge behaves as mode Off.
- A loop never moves items out of the sharing island.
