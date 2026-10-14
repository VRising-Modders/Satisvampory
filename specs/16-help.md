# 16 — Explaining the words

Satisvampory uses a few words of its own. Every one is explained where the player meets it, so nobody needs a wiki.

## Glossary

The wording below is used for tooltips, the glossary page, and tips. Keep it to one or two sentences each.

| Term | Explanation shown to players |
|---|---|
| **Belt** | A named list of chests and stations that you use together, like "Ore in" for the three chests beside your furnaces. Put a belt in a chest's or station's Input or Output and it acts on every chest on the belt. |
| **Category** | A named set of items, like Flowers or Ore. Use categories to filter what moves, to set reserves and caps, and in Mass Deposit rules. |
| **Input** | Where this chest or station pulls items from. |
| **Output** | Where this chest or station sends items to. |
| **Mass Deposit** | Satisvampory's idea of where each item belongs. Quick stash, tidy and servant loot use it, and you can choose it as an Output. |
| **Island** | All the castles whose chests you can use from where you stand. It follows castle share and clan share. |
| **Castle share** | Your own castles act as one: craft at one, use chests at another. |
| **Clan share** | Every castle of every clan member taking part acts as one. |
| **Reserve** | How many of an item stations and crafting leave untouched. |
| **Cap** | Stations stop making an item once the island holds this many. |
| **Loop** | Chests that feed each other in a circle. You choose whether it runs once or keeps items evenly spread. |
| **Priority** | A chest's rank for Mass Deposit: higher wins among chests that fit an item. |

## Where it shows

- **Hover definitions.** The words Belt, Category, Mass Deposit, Island, Reserve, Cap and Loop show a small ⓘ the first few times they appear in a panel. Hovering any of them shows its glossary line: in list rows, picker sections, tab names, column headers, chip labels. The ⓘ stops showing after the player has hovered that term three times. A Me setting brings it back.
- **Empty states.** The first time a list is empty, it explains itself instead of showing a bare blank:
  - Belts tab with no belts: "A belt is a named list of chests and stations you use together, like 'Ore in' for the chests beside your furnaces. Make one, then add it to any Input or Output. [+ New belt]"
  - An empty Input: "Add the chests, belts, or Mass Deposit this pulls from. [+ Add…] [◎ Pick in world]"
  - An empty Output: "Add where this sends items. Mass Deposit sends each item to where it belongs."
- **Picker sections.** The picker's Belts section header carries the belt line as a subtitle.
- **First-time tips.** One vanilla-style tip card per feature, shown once: the first time the player opens an Input or Output, opens the Belts and categories screen, opens Mass Deposit, and stands on a shared island. Each has **Got it** and **Show all tips**.
- **Glossary page.** A **Help** tab in Satisvampory's settings lists every term above, plus a short "How it fits together" paragraph and links that open each screen.

## Rules

- The UI says **Belt** and **Category**, never "group".
- Explanations come through localization keys like every other string (`00-product.md`).
- Changing a term's wording changes it in one place, the glossary table, and every surface follows.
