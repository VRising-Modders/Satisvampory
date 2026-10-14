# 04 — Stations

Stations are refinement stations (furnace, sawmill, loom, grinder, …), crafting stations and tables, unit spawners, the devourer, and braziers. Each has an input list and an output list like a chest (`02-io-lists.md`).

## Recipe switches

The station's own recipe list is the switch. Refinement stations already have vanilla per-recipe on/off toggles; those are what Satisvampory reads. Where a station has no vanilla toggle (crafting tables), the Input panel shows its recipes with an on/off switch per recipe, default off.

A station accepts only the inputs of recipes that are switched on.

## Whole crafts only

A station pulls from its input list **only when the input list can supply every ingredient of at least one craft** of a recipe that is switched on.

1. For each switched-on recipe, count what the input list can supply after reserve.
2. If at least one full craft is available, pull up to the **feed target**: five crafts of each ingredient, in whole-craft amounts, never a partial set.
3. If no recipe can make one craft, pull nothing. The row says `not-enough` and names the missing ingredient.

Two recipes that share an ingredient split it by shortfall, still in whole crafts.

## Leftover return

If a station holds input items and **nothing can be built** from them (a recipe was switched off, an ingredient ran out mid-stream, or the input list changed), after a short grace (30 s, so a refill in flight can land) the leftovers go back:

1. To the input list entry they came from, if it still exists and has room.
2. Otherwise to their Mass Deposit home.
3. Otherwise they stay and the station shows `sink-full` on its Input button.

Leftover return never takes an item that a switched-on recipe could still use once its other ingredients arrive within the grace.

## Outputs

Finished products leave through the output list. With no output list, products stay in the station as in vanilla. "Mass Deposit" in the output list sends each product to its home.

A station in another station's input list gives up only its finished products, never its inputs.

## Crafting at a table

When a player crafts by hand at a table, repairs, or uses the forge, missing ingredients are taken from storage in scope (`06-castle-sharing.md`). That is a vanilla-style "castle storage" read, not a Satisvampory move, and it honors reserve. See `11-progenitor-api.md`, "Station material availability".

## Special stations

| Station | Input | Output |
|---|---|---|
| Unit spawner | Feeds two crafts deep | Spawned units are not items; no output |
| Devourer | Salvage chests (`09-special-chests.md`) | Its product to the output list |
| Brazier | Fuel; leaves at least 10 in each source | none |
