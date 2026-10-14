# 14 — Belts and Categories screen

Two kinds of set, with different names so they never get confused:

| | **Belt** | **Category** |
|---|---|---|
| Holds | Containers and stations | Items |
| Example | "Ore in": the three chests beside the furnaces | "Flowers": every flower item |
| Used by | Input and output lists, Mass Deposit overrides and rules | List filters, deposit options (priorities, bans), Mass Deposit rules, reserves, caps, find, pull |
| Defined in | `01-containers.md`, "Belts" | `01-containers.md`, "Categories" |

This screen is where both are made, named, filled, and tidied up. Lists and Mass Deposit only use them.

The UI always says "Belt" or "Category", never just "group". Both words are explained in the game itself: hover definitions, empty-state text, a first-time tip, and the Help tab (`16-help.md`). A belt and a category may share a name ("Ore" belt, "ore" category) because they are never offered in the same field.

## Opening it

- Keybind (unbound by default, set in Settings).
- **Belts and categories** row on the Castle settings page.
- **Manage belts…** / **Manage categories…** at the bottom of every picker and list editor.
- A list row that is a belt has an **Edit belt** action that opens the screen on it.
- A container's Deposit settings show the belts it is on as chips, with **+** to add it to one and **×** to remove it. Clicking a chip opens that belt.
- From the Mass Deposit screen: any belt or category named in a row or rule.
- From the Inventory page (`15-inventory.md`): any category.

## Layout

Styled like the vanilla options menu: a tab strip, a list on the left, the selected entry on the right.

```
┌ Belts and categories · island: Hollow Keep + 2 plots ─────────────┐
│ [ Belts ] [ Categories ]                               [ search ] │
│                                                                   │
│ ▸ Ore in            3 chests · here, Plot 2  │ Ore in      [Rename]│
│ ▸ Line 1            4 chests · here          │ 3 chests · 2 plots  │
│ ▸ Jewel cabinets    2 chests · Plot 3        │                     │
│                                              │ ≡ Iron bin · here   ✕│
│ [+ New belt]                                 │ ≡ Ore chest · here  ✕│
│                                              │ ≡ Ore · Plot 2 · 40m✕│
│                                              │ [+ Add…] [◎ Pick]   │
│                                              │ [+ Add by name…]    │
│                                              │                     │
│                                              │ Used by (4)         │
│                                              │  Input of Furnace…  │
│                                              │  MD override 2     │
│                                              │  Rule, line 6       │
│                                              │ [Locate all] [Delete]│
└───────────────────────────────────────────────────────────────────┘
```

## Belts tab

Left list: every belt on the island, with its member count and the plot labels it spans. Search by belt name, member name, or plot. Sort by name or by recent use.

Right panel for the selected belt:

- **Name**, renamed in place. Unique among belts on the island, compared case-insensitively. No quotes.
- **Members** are an ordered list. Order matters for "Respect list order" pulls and for "Fill in order" sends.
  - Drag to reorder, or use the up and down buttons. Remove with ✕.
  - Each row shows the member's icon, name, plot label, distance, what it holds at a glance, and **Locate**.
- **Add…** opens the picker (`02-io-lists.md`, "Pickers"). It allows multi-select.
- **◎ Pick** is pick in world. It stays in pick mode until Esc, adding each container the player interacts with.
- **Add by name…** adds every container on the island whose name contains the text as whole words. For example, `ore` adds "Ore", "Ore 2" and "Copper ore", but not "Core". It shows the matches before adding. Hearts are never offered.
- **Used by** lists every list entry, Mass Deposit override, and Advanced rule line that refers to this belt. Each one jumps to where it is used.
- **Locate all** pings every member for ten seconds.
- **Duplicate** copies the belt under a new name.
- **Delete** asks first, listing what uses the belt.
  - The belt is removed from every list that used it, and those lists show what was removed.
  - Mass Deposit overrides naming it are removed.
  - Advanced rule lines naming it are not edited. The script fails its check and keeps running on its last good version until a player fixes it.

A belt is identified by an id, not its name. A rename updates every list entry and Mass Deposit row in the same step. It also rewrites `belt "…"` references in Advanced scripts; comments are left alone.

## Categories tab

Left list: built-in categories (marked **Built-in**), castle copies of built-ins (marked **Edited**), and custom categories. Search by name, alias, or member item.

Right panel:

- **Name** and **aliases**. Built-in names are fixed. Custom categories can be renamed, and a rename updates everything that names them.
- **Members** are item chips with icons and the island stock per item. Expanding a chip shows that item's reserve and cap, editable in place. The Inventory page edits the same values.
- **Add…** opens the item picker, which supports search with icons and multi-select. It can also add every item of a vanilla item category.
- **Remove** a member with ✕.
- Editing a built-in makes a castle copy first. **Restore** returns it to the generated list.
- **Used by** lists the list filters, deposit options (priorities and bans), Mass Deposit rules, reserves, and caps that name it.
- **New category**, **Duplicate**, and **Delete** (not for built-ins).

## Who may change what

Any clan member of the heart owner can create and edit belts and categories on the island. Guests see the screen read-only. Server item aliases stay on the admin page.

## Where they live

Belts and edited categories belong to the island's settings owner, the same as Mass Deposit (`06-castle-sharing.md`):

- the standing castle's owner under castle share;
- the clan under clan share;
- the castle itself when it is not shared.

If the island changes, for example because a player leaves clan share, nothing is deleted. Belt members outside the new island show `out-of-scope` until the island changes back.
