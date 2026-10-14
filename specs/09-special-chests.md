# 09 — Special chests

A container's **special kind** is set from the chest's Deposit options panel (`01-containers.md`). 1.x name words are converted once by the importer. Pickers list special chests under their own names and kinds, so they can be chosen directly in any input or output list.

| Kind | Behavior | Admin allow | Default for a castle |
|---|---|---|---|
| **Overflow** | Last-resort Mass Deposit home. Tidy source only. | — | — |
| **Skip** | Never a source or dest for anything. | — | — |
| **Salvage** | Feeds the devourer on the same plot. | Allow salvage (ON) | Castle setting **Feed salvage to the devourer** OFF |
| **Spawner** | Feeds unit spawners on the plot, two crafts deep. | Allow spawner chests (ON) | on when the chest exists |
| **Brazier** | Keeps braziers on the plot fuelled, leaving at least 10 in the chest. Variants **Night** (only at night) and **Proximity** (only when a player is within 20 m). Tick 2.5 s. | Allow brazier chests (ON), Allow night and proximity (ON) | on when the chest exists |
| **Trash** | Can be emptied by a clan member from its panel (**Empty**, with confirm). Admins can empty every trash chest on a plot. Never a tidy dest. | Allow trash (ON) | — |
| **Spoils** | Servant-loot overflow. Tidy source only. | — | — |

Special chests are never Mass Deposit homes unless a Simple row or Advanced rule names them (`03-mass-deposit.md`).

## Heart blood feed

Not a chest kind. Blood Essence is moved into castle hearts' fuel so a player never has to carry it.

**Target**: every **unlocked** fuel pad filled to 500. A level-N heart has N pads; count them from the level-indexed heart data, not the default. Locked pads stay empty.

**Source**: the heart's **input list** when it has entries, in its pull order. With an empty input list, the island reachable from that heart's plot (`06-castle-sharing.md`), worst Mass Deposit home first. Either way reserve is honored and another heart is never a source.

**Taking out**: a heart is never pulled from by Mass Deposit, crafting, pulls, tidy, or another container's input list. A player who wants blood out (for example before dismantling) adds an output entry on the heart itself, which runs like any output.

**When** (player settings, Me page):

| Setting | Default | Effect |
|---|---|---|
| **Top up hearts when I visit** | ON | Entering a plot owned by me or my clan that is on my island tops up its heart. |
| **Top up my island's hearts when I log in** | OFF | At login, top up every heart on my island: just the plot I am on when both sharing kinds are off, my own castles under castle share, every participating clan castle under clan share. |
| **Also top up hearts outside my island** | OFF | Visiting a plot owned by me or my clan that is **not** on my island (excluded castle, an owner who opted out of clan share, sharing off) tops up its heart too, from that plot's own island. |

A visit top-up runs once per plot entry, within about two seconds of crossing in, with a 60 s cooldown per heart. Logging in while standing on a plot counts as a visit. A login top-up runs once, after the player is fully loaded in; the cooldown stops the two from feeding the same heart twice.

**Castle switch**: **Heart fuel auto-feed** (Castle page, default ON). Off: nothing feeds that heart for anyone, and emptying the pads sticks so the heart can be dismantled. Turning it on clears that.

Admin allow **Heart blood feed** (default ON). Guests never trigger a top-up.
