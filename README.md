# Satisvampory 2.0

Castle logistics for V Rising, built on [Progenitor](https://github.com/VRising-Modders/Progenitor).

- Native chest tabs: **Inventory / Input / Output / Accepts**; station tabs: **Production / Input / Output**, mounted through Progenitor `AttachPanelTabs`. Inventory and Production retain the game controls; game acceptance restrictions are read-only.
- New storage supplies the **Default Belt**; ordinary stations draw on demand in reverse Mass Deposit order and send products to Mass Deposit. Existing configurations are preserved.
- **Mass Deposit** decides where every item lives from each chest's own deposit settings (never its name), with island overrides, an Advanced rule script, and a Try it evaluator.
- **Castle share** joins one player's castles and **clan share** joins the whole clan, so crafting and building at any station can use any chest on any shared plot. Treasury floor is not required.
- Stations pull only whole crafts. Any nonempty or unknown station queue protects all inputs; known idle stations may return leftovers after the grace period. Explicit loops run once or balance evenly.
- Native redistribution defaults off; `AutomationSuspended` guards prevent competing engine transfers while native redistribution owns them.
- Quick stash, pull, find, tidy on a hotkey, servant loot stash, special chests, and heart blood feed.

There are no chat commands at all, for players or admins: admins use the Server section of the settings page. Autoloot is a separate mod.

Status: **in development, partially tested live**. Chest, furnace and Anvil native/custom tabs have been observed on the isolated test client. Layout checks cover 1920 x 1080 and ultrawide 3840 x 1080; restricted chest content was also checked at 2560 x 1440. The current revision passes 178 engine tests and 294 focused Progenitor tests; plugin/Core builds pass. This does not qualify the full product: multi-castle logistics, optional native redistribution and extended play still need live validation. Queue protection is conservative, not per-ingredient reservation accounting.

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
