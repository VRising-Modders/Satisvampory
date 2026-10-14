# 12 — Protocol, persistence, 1.x import

## Protocol

- Client to server: requests (add entry, remove entry, reorder, set filter, set mode, set setting, pick result, take, stash, tidy, find query). Every request carries the target container id and a client sequence number.
- Server to client: replies (accepted with the new state, or refused with a reason code), and subscription snapshots then deltas for the panel currently open.
- The handshake requires a matching Satisvampory client by default. Admin **Allow unmodded clients** lets players without it join; they get double-tap stash, servants, and anything already configured, but no panels.
- Built on Progenitor networking (`11-progenitor-api.md`, P8).

## Persistence

Progenitor persistence, one store per scope:

| Store | Keyed by |
|---|---|
| Lists (input and output entries, modes, filters), deposit options (priority, item priorities, bans, special kind) | Container id |
| Belts, Mass Deposit Simple list and Advanced script, reserves, caps, castle aliases, plot label, castle toggles | Castle heart id (settings follow the heart, not the owner) |
| Mass Deposit settings and belts under castle share | Owner player id |
| Mass Deposit settings and belts under clan share | Clan id |
| Clan settings, plot numbering | Clan id |
| Player settings, keybinds, hunt log | Player id |
| Server settings | One record |

A destroyed container's entries are removed from every list and belt, and the affected panels show "n entries removed: chest destroyed".

## One-time import from 1.x

On first boot with no 2.0 data, and again on demand from the Server settings page:

1. Read 1.x's config and save data (reserves, caps, custom categories, aliases, ClanShare and exclusions, salvage, heart fuel, repeat hunts, player toggles) and map each onto the 2.0 setting.
2. For every castle, read chest names. Each `s<n>` / `r<n>` line becomes a belt "Line n · <plot label>". Each receiver gets an input entry pointing at that group; each sender keeps its contents. A station receiver gets the group as input. A 1.x loop allowed by `.sg convloop` imports as a loop in **Cycle once**.
3. Convert names into deposit settings, since 2.0 never reads names: item names, category words, generic names and other names become **Takes** settings, trailing `+` becomes base priority, `--word` becomes bans, and special words and `NS` / `''` become special kinds (`01-containers.md`, "Names are just names").
4. Strip the converted tokens from names only when the admin ticks **Clean names after import** (default OFF), so a rollback to 1.x still works.
5. Write an import report (counts, anything not mapped) to the log and the Server settings page.

Import is idempotent: running it twice does not duplicate belts or entries.
