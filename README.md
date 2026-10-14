# Satisvampory 2.0

Castle logistics for V Rising, built on [Progenitor](https://github.com/VRising-Modders/Progenitor).

- Every chest, station and castle heart gets an **Input** and an **Output** button in its own vanilla panel, each opening an ordered list of chests, belts, Mass Deposit and special chests.
- **Mass Deposit** decides where every item lives from each chest's own deposit settings (never its name), with island overrides, an Advanced rule script, and a Try it evaluator.
- **Castle share** joins one player's castles and **clan share** joins the whole clan, so crafting and building at any station can use any chest on any shared plot. Treasury floor is not required.
- Stations pull only whole crafts and return leftovers. Explicit loops run once or balance evenly.
- Quick stash, pull, find, tidy on a hotkey, servant loot stash, special chests, and heart blood feed.

There are no chat commands at all, for players or admins: admins use the Server section of the settings page. Autoloot is a separate mod.

Status: **in development, not yet tested in game**. The game-free engine, its tests and the plugin are here; the product contract is [`specs/`](specs/README.md).

Crafting and building from storage uses Progenitor's experimental material sources. They are off until the server and every client set `[Materials] NativeSeams = true` in Progenitor's config and restart; without that, crafting and building use the game's own rules and everything else still works.

## Layout

| Path | What |
|---|---|
| `specs/` | Product contract. Read before changing behavior. |
| `src/Satisvampory.Engine` | Game-free routing engine: lists, Mass Deposit, sharing, stations, loops, player actions, 1.x import. |
| `tests/Satisvampory.Engine.Tests` | Engine tests. `dotnet test` |
| `src/Satisvampory` | The plugin: one DLL for server and client. Settings page (Me, Keys, Server, Help), keybinds, player settings sync, save stores, admin requests from the Server section (server-checked), and the world adapter over Progenitor. |

## Building

Needs the game with BepInEx installed (for its interop assemblies) and Progenitor installed in it.

```
dotnet test tests/Satisvampory.Engine.Tests
dotnet build src/Satisvampory
```

The plugin compiles against `<game>\BepInEx\progenitor-core\Progenitor.dll`, which must be a Progenitor Core from its main branch at or after the inventory, stations and panel-site APIs (October 2026). Point `PROGENITOR_DLL` (or `Directory.Build.local.props`) at a newer Core when the installed one is older.

## Relationship to 1.x

Same product idea, complete rework. This is original code. It contains no code, data, or text from Satisvampory 1.x or KindredLogistics, and carries no attribution to either. 1.x chest names and settings can be imported once ([`specs/12-protocol-persistence.md`](specs/12-protocol-persistence.md)).

Never load 1.x and 2.0 on the same server.

## License

[AGPL-3.0](LICENSE).
