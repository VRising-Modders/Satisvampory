# 04 — Stations

Stations are refinement stations (furnace, sawmill, loom, grinder, …), crafting stations and tables, unit spawners, the devourer, and braziers. Each has an input list and an output list like a chest (`02-io-lists.md`).

## Production and tabs

Use **Production · Input · Output**, with the original production UI first. No Accepts tab and no separate recipe controls in Input. Refiners use native recipe toggles; crafting benches use the player's queue. Learning a recipe is never permission to produce it automatically.

**Show vanilla redistribution tab** is a player setting, default OFF. Hiding it is presentation only. Progenitor must expose redistribution state/ownership separately so automation never silently competes with vanilla redistribution. When shown, it fits beside the other tabs and identifies the active transfer system.

## Whole crafts only

A station pulls from its input list **only when the input list can supply every ingredient of at least one craft** of a recipe that is switched on.

1. For each switched-on recipe, count what the input list can supply after reserve.
2. If at least one full craft is available, pull up to the **feed target**: five crafts of each ingredient, in whole-craft amounts, never a partial set.
3. If no recipe can make one craft, pull nothing. The row says `not-enough` and names the missing ingredient.

Two recipes that share an ingredient split it by shortfall, still in whole crafts.

## Leftover return

If a station holds input items and **nothing can be built** from them (a recipe was switched off, an ingredient ran out mid-stream, or the input list changed), after a short grace (30 s, so a refill in flight can land) the leftovers go back:

1. To eligible chests among the configured input sources, in forward Mass Deposit priority, splitting across available capacity. This is independent of pull order and last-source order.
2. Otherwise to their Mass Deposit home.
3. Otherwise they stay and the station shows `sink-full` on its Input button.

Leftover return never takes materials reserved for an active or queued craft. Keep the grace period for refining ingredients. When input sources are full, use regular Mass Deposit; if all destinations are full, retain the remainder at the station.

## Outputs

Finished products leave through the output list. With no output list, products stay in the station as in vanilla. "Mass Deposit" in the output list sends each product to its home.

A station in another station's input list gives up only its finished products, never its inputs.

## Crafting at a table

Bench Input defaults to Default Belt in reverse Mass Deposit priority. Configured Input governs both displayed availability and consumption. Repairs continue to use all permitted storage in the applicable plot/castle/clan scope independently of bench Input (`06-castle-sharing.md`). That is a vanilla-style "castle storage" read, not a Satisvampory move, and it honors reserve. See `11-progenitor-api.md`, "Station material availability".

## Special stations

| Station | Input | Output |
|---|---|---|
| Unit spawner | Feeds two crafts deep | Spawned units are not items; no output |
| Devourer | Salvage chests (`09-special-chests.md`) | Its product to the output list |
| Brazier | Fuel: whatever its Input sources hold that the game accepts as fuel, as much as it has room for. Reserves still apply. | none |
