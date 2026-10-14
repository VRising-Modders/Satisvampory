# Satisvampory 2.0

Castle logistics for V Rising, built on [Progenitor](https://github.com/VRising-Modders/Progenitor).

- Every chest, station and castle heart gets an **Input** and an **Output** button in its own vanilla panel, each opening an ordered list of chests, belts, Mass Deposit and special chests.
- **Mass Deposit** decides where every item lives, with a Simple priority list and an Advanced rule script.
- **Castle share** joins one player's castles and **clan share** joins the whole clan, so crafting and building at any station can use any chest on any shared plot. Treasury floor is not required.
- Stations pull only whole crafts and return leftovers. Explicit loops run once or balance evenly.
- Quick stash, pull, find, tidy on a hotkey, servant loot stash, special chests, and heart blood feed.

There are no chat commands to learn. Autoloot is a separate mod.

Status: **in development**. The game-free engine and its tests are here. The plugin waits on Progenitor's inventory, station and panel APIs. The product contract is [`specs/`](specs/README.md).

## Layout

| Path | What |
|---|---|
| `specs/` | Product contract. Read before changing behavior. |
| `src/Satisvampory.Engine` | Game-free routing engine: lists, Mass Deposit, sharing, stations, loops, player actions, 1.x import. |
| `tests/Satisvampory.Engine.Tests` | Engine tests. `dotnet test` |
| `src/Satisvampory` | The plugin: one DLL for server and client. Settings page (Me, Keys, Server, Help), keybinds, player settings sync, save stores, `.sv` admin commands, and the world adapter over Progenitor. |

## Building

Needs the game with BepInEx installed (for its interop assemblies) and Progenitor installed in it.

```
dotnet test tests/Satisvampory.Engine.Tests
dotnet build src/Satisvampory
```

The plugin compiles against `<game>\BepInEx\progenitor-core\Progenitor.dll`. Override paths with `VRISING_GAME_DIR`, `PROGENITOR_DLL`, or a git-ignored `Directory.Build.local.props`.

Stash, tidy and the conveyor need Progenitor's inventory API, which is still in review. To build the world adapter against a Progenitor Core built from that branch:

```
dotnet build src/Satisvampory -p:ProgenitorPa=true -p:ProgenitorDll=<path to that Progenitor.dll>
```

## Relationship to 1.x

Same product idea, complete rework. This is original code. It contains no code, data, or text from Satisvampory 1.x or KindredLogistics, and carries no attribution to either. 1.x chest names and settings can be imported once ([`specs/12-protocol-persistence.md`](specs/12-protocol-persistence.md)).

Never load 1.x and 2.0 on the same server.

## License

[AGPL-3.0](LICENSE).
