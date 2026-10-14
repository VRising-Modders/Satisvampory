// SPDX-License-Identifier: AGPL-3.0-only
using BepInEx;
using Progenitor.Plugins;
using Satisvampory.Config;
using Satisvampory.Server;

namespace Satisvampory
{
    /// <summary>
    /// Satisvampory 2.0. One DLL on both sides: the server runs the engine and owns every item
    /// move; the client draws panels and sends requests. Settings, keybinds and saves go through
    /// Progenitor.
    /// </summary>
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    [BepInDependency("progenitor")]
    [ProgenitorSide(Side.Both)]
    // Either side may run without the other; the halves talk over raw ctx.Net packets, so peers
    // must share a minor version.
    [NetworkCompatibility(NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor)]
    [Counterpart(CounterpartNeed.Limited, Features = new[]
    {
        "Hub (inventory, find, belts, Mass Deposit)", "Chest and station Input and Output panels",
        "Quick stash and tidy", "Admin tools and import",
    })]
    public sealed class Plugin : ProgenitorPlugin<SatisContext>
    {
        protected override void OnLoad(SatisContext ctx)
        {
            var server = ServerConfig.Bind(ctx);
            var player = PlayerConfig.Bind(ctx);
            var keys = Keys.Register(ctx);
            var admin = new AdminPanel();
            SettingsPage.Build(ctx, server, player, keys, admin);

            if (ctx.IsServer) new ServerHalf(ctx, server).Attach();
            else new ClientHalf(ctx, player, keys, admin).Attach();
        }
    }

    /// <summary>Empty, as every Progenitor context should be.</summary>
    public sealed class SatisContext : PluginContext
    {
    }
}
