# 08 — Servants

Carried over from 1.x with UI instead of chat.

## Loot auto-stash

When servants come back from a hunt, their loot goes to Mass Deposit homes in the island (or the servant's plot when sharing is off), preferring the servant's own plot on ties. Player setting (applies to castles the player owns) default ON, plus admin allow. Only the returning servants are emptied, never the heart or a player.

## Notices and hunt log

- **Sent**: a notification to the sender: who went, where, survival chance, repeat on or off.
- **Returned**: to connected clan members: where, who came back, loot stacks, who died or was hurt, "sending again" when repeating.
- **Hunt log** (Settings, Me page, or keybind): haul since last logout or the last 72 hours, grouped by plot label, then each servant's state.

## Repeat hunts

Admin allow (default OFF), admin "only while a clan member is online" (default OFF), admin success cap (default 99, display only). Castle setting **Repeat hunts** (default ON when allowed). The Clan page lists every castle with a toggle and **All on**.

When on, surviving uninjured servants are resent to the same zone, mission, and throne once idle. A refused send is reported with the game's reason, never as sent. Raided hearts do not resend.

Send mechanics: write the heart's mission record and the servant's on-mission state as the game does. Never synthesize the throne send event. Never patch the success-chance methods.

## Throne targeting

A throne lists and sends only its own castle's servants, as in vanilla, even under sharing (decided 2026-10-16: servants are not merged across an island's castles).
