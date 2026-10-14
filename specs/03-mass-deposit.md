# 03 — Mass Deposit

Mass Deposit decides where an item lives. Each chest's **deposit settings** (`01-containers.md`) rate every item; the chest that rates it highest is its home. **Chest names play no part.**

It is used in three ways:

| Use | When |
|---|---|
| **The Mass Deposit action** | A player's quick stash (`07-stash-pull-find-tidy.md`), and things a player triggers that put items away: tidy, servant loot, station leftovers. |
| **Push: Mass Deposit** (an Output row) | Each tick, a container with this row sends its items (after its filter) to their homes. |
| **Pull: reverse Mass Deposit** (an Input row on something that consumes) | Each tick, a station, castle heart, unit spawner or stable pulls what it **needs** (switched-on recipe inputs, fuel up to its target, feed) from wherever it is stored worst: chests that are not homes for it first, then homes from worst to best. Plain chests have no demand, so they never pull this way; to move items into a chest, Push from the chest holding them or list chests and belts in its Input. |

The UI always says **Push: Mass Deposit** and **Pull: reverse Mass Deposit** so it is clear which way items move.

Scope: the island the player reaches (`06-castle-sharing.md`). One settings set governs the whole island: the standing owner's under castle share, the clan's under clan share, or the castle's own when it is not shared.

## How a chest rates an item

From the chest's deposit settings:

| Setting | Effect |
|---|---|
| **Takes** | **Anything** (default for a new chest), **Only what's listed**, or **Nothing**. |
| **Base priority** | −10 to +10, default 0. |
| **Item and category priorities** | Each entry is added to the base for those items. An item's own entry beats any category entry; between categories the larger number wins. On an "Only what's listed" chest, an entry is also what makes the chest take the item. |
| **Never deposit here** | Items and categories this chest never takes. |
| **Special kind** | Overflow chests are the last resort for anything. Skip chests are never homes. Salvage, spawner, brazier, trash and spoils chests are homes only when an override or rule names them (`09-special-chests.md`). |

Order of homes for an item:

1. Override and rule targets, in the order they are listed (below).
2. Everything else by priority, highest first.
3. Ties: a chest already holding some, then the plot the player is standing on, then the nearest.
4. Overflow chests last.

Chests that list the item at the same positive priority **split** deposits between them; other chests fill one at a time.

## The Mass Deposit screen

It opens from the Castle settings page, from any Push or Pull row, and from a keybind. It has three tabs.

### Overview

A table of every item and category that has a home on the island, with its ordered homes and each chest's priority. Clicking a chest opens its deposit settings. Dragging a chest above another in an item's list changes that chest's priority for the item by just enough to put it there. The change is written into that chest's settings, so the chests stay the single source of truth.

### Overrides (Simple)

A short ordered list of island-wide overrides, each "for these items, use this target first". The items are an item, a category, or any item. The target is a belt, a chest, a plot, overflow, or a special kind. For example: "Blood Essence → belt Alchemy first". Overrides are for the rare case where per-chest settings are awkward, such as sending everything from one plot to one belt.

### Advanced

A short rule script, edited in a text area with syntax colour and line errors. Rules run top to bottom. An empty script changes nothing.

```
# Blood Essence never goes to treasury chests
for "Blood Essence":  only belt "Alchemy", then general

# Gems: the vault first, then the jewel cabinets
for category gems:    first chest "Gem vault" @ "Hollow Keep"
                      then belt "Jewel cabinets"

# Keep plot 3 out of everything
for any:              never plot "Plot 3"

# Seeds stay on the plot they were stashed from
for category seeds:   prefer here
```

| Clause | Effect |
|---|---|
| `for <items>:` | Which items the rule covers: `"Item name"`, `category <name>`, `any`. |
| `first <target>` / `then <target>` | Put targets ahead of the chests' own priorities, in order. |
| `only <target>` (`, then <target>` …) | Only these targets are homes for these items. |
| `never <target>` | Remove a target, even one an override pinned. |
| `prefer here` / `prefer plot "<label>"` | Tie-break toward a plot. |

Targets are `chest "<name>"` (optionally `@ "<plot label>"`), `belt "<belt>"`, `general` (chests that take anything), `overflow`, `plot "<label>"`, and `special <kind>`.

When a script is saved:
- Unknown chests, belts, categories and plots are errors with a line number, and the save is refused.
- A rule that matches nothing is a warning.
- The last good script keeps running until a fixed one is saved.
- The script compiles to the same structure the engine runs. It has no loops, variables or arithmetic, so it cannot hang the server.

### Evaluator

The evaluator answers "what if" without moving anything. It sits at the bottom of every tab, and as a **Try it** box on each Input and Output panel (`02-io-lists.md`).

You enter an item and an amount, then pick a direction:

- **Push n of X:** every chest it would go to, in order, with the amount and why each one ranks there (for example "Category ore +3 on base 2" or "Rule on line 4"). Then every chest on the island that would **not** get any, with the reason:
  - no free space;
  - takes only listed items, and this is not one;
  - bans category ore;
  - skip chest;
  - Trash chest, used only when an override names it;
  - removed by a rule;
  - not needed, because better homes took it all.
- **Pull n of X:** every chest it would come from, in order, with amounts and why it comes in that order (for example "not a home for it, drained first" or "home rank 3"). Then every chest not used:
  - holds none;
  - keeps its last stack;
  - kept as reserve;
  - skip or special chest;
  - not in this Input list;
  - not needed.
- **From a container's Output panel:** the rows in order. Each target shows its amount, including **0** with the reason. For example, 2,000 Iron Ingot with row 1 Push: Mass Deposit and row 2 a chest shows the Mass Deposit homes with their amounts, then "Row 2 · Spare chest · 0 · Mass Deposit in row 1 took it all".
- **From an Input panel:** the same pull answer, limited to that list's sources and its pull order.

If the island cannot cover the amount, the result says how many are short. Results use the current contents and settings, and refresh while the screen is open.

## Fixed constraints

- Skip chests, castle hearts, stations and raided plots are never homes.
- Vanilla treasury chests refuse plain Blood Essence. Their free space for it is zero, so Mass Deposit passes them over; Greater Blood Essence is fine.
- A chest that bans an item is never its home, whatever overrides or rules say.
- An explicit Output row to a chest delivers regardless of that chest's deposit settings, because a list row is an instruction, not a ranking. The row shows a warning when the chest bans the item.
