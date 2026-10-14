# 06 — Sharing: castle share and clan share

There are two kinds of sharing. Each joins plots into one storage **island**.

| Kind | Joins | Default |
|---|---|---|
| **Castle share** | Every plot owned by the **same player**: one player's castles act as one. | **ON** |
| **Clan share** | Every plot owned by **anyone in the clan who takes part**. | **ON** |

Clan share includes castle share. With both off, the island is the standing plot.

A player standing on a plot reaches:

- Clan share on, the player takes part, and the standing plot's owner takes part: every non-excluded plot whose owner takes part.
- Castle share on and the standing plot's owner shares between their castles, and the player is the owner or in the owner's clan: every non-excluded plot of the **standing plot's owner**. A clanmate crafting in your castle reaches your other castles, not their own.
- Otherwise (both off, a guest, or standing on an excluded plot): the standing plot only.

## Taking part in clan share

Each player has **Take part in clan share** (Me page, default ON). Once taking part, they choose which of their castles join with **Exclude this castle**. A player who opts out:

- has their plots removed from every other member's island;
- does not reach other members' plots either, so nobody takes without sharing;
- still has castle share across their own plots.

## What the island gives

- Craft, build, repair, and use any station with materials sitting in **any** chest on the island, with no items moved in advance. The station's "available materials" shows island totals, and the craft takes from the chests directly.
- Put chests from any island plot in input and output lists and groups.
- Quick stash, pull, find, and tidy across the island.

Treasury floor is **not** required for any of this. A plain chest on bare floor on another plot counts.

With both kinds off, every chest on the **standing plot** counts, on any floor, treasury or not. Chests on other plots are invisible to lists, pickers, crafting, and stash. Existing list entries pointing at them show `out-of-scope` and do nothing.

## Settings

| Setting | Scope | Default |
|---|---|---|
| **Castle share** | Server (admin): the feature exists | ON |
| **Players may turn castle share off** | Server (admin) | ON |
| **Share between my castles** | Player (Me page) | ON |
| **Clan share** | Server (admin): the feature exists | ON |
| **Players may turn clan share off** | Server (admin) | ON |
| **Take part in clan share** | Player (Me page) | ON |
| **Exclude this castle** | Castle, heart owner only | OFF |

Sharing is the players' choice inside the admin's limits. The admin decides whether each kind exists and whether players may opt out; when opting out is locked, the player row shows locked at ON. A player who turns **Share between my castles** off keeps their plots separate from each other. An excluded castle is local-only when standing on it and invisible to the island from elsewhere.

Mass Deposit settings follow the island (`03-mass-deposit.md`). Castle share uses the standing owner's settings, clan share uses the clan's, and an unshared plot uses its own castle's.

## How crafting reads shared storage

Vanilla gathers a workstation's available materials from the castle's storage. 2.0 replaces that source, through Progenitor (`11-progenitor-api.md`, P3), with the containers on the island:

- Every container on the island except skip chests, special-purpose chests, raided plots, and excluded castles.

Consumption follows the same set. It takes from the worst Mass Deposit home first and honors the castle reserve, so crafting never strips a well-sorted chest before a dump. Building placement costs use the same path.

Both the client's displayed counts and the server's consumption come from the server. The client never computes availability itself.

## Who counts

Only the owner and clan members get island reach. Castle guests use the standing plot's own storage and never pull from other plots.

## Raided

A raided plot drops out of the island for everything, including crafting reads, until the raid ends. Its label shows "Under attack" in pickers.
