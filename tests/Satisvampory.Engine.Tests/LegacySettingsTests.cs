// SPDX-License-Identifier: AGPL-3.0-only
using System.Linq;
using Satisvampory.Engine.Import;
using Satisvampory.Engine.Model;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class LegacySettingsTests
    {
        // Invented ids and values in the 1.x file's shape; no real player data.
        const string File = @"{
          ""0"": { ""SortStash"": true, ""Pull"": true, ""CraftPull"": false, ""AutoStashMissions"": true, ""Conveyor"": true,
                   ""ConveyorLoops"": false, ""Salvage"": true, ""UnitSpawner"": false, ""Brazier"": true, ""Named"": false,
                   ""Trash"": true, ""RrGlobalAllow"": false, ""RepeatHunt"": true, ""RepeatHuntRequireOnline"": true,
                   ""RepeatHuntMaxSuccess"": 95, ""ClanShareByClan"": { ""c(Normal 1:1)"": true },
                   ""ClanShareExcludedTerritories"": [7], ""ItemAliases"": { ""ior"": 1001 },
                   ""AutoScoop"": true, ""StarterKitSeeded"": [""n(1)""], ""Mystery"": 1 },
          ""1001"": { ""SortStash"": false, ""DontPullLast"": false, ""SilentStash"": true, ""RrGlobal"": true, ""AutoStashMissions"": false,
                      ""PullReserve"": 25, ""ItemReserves"": { ""1001"": 250, ""1004"": 1000 }, ""ItemCaps"": { ""1006"": 300, ""bad"": 5 },
                      ""ItemGroups"": { ""ore"": [""1001""], ""Leathers"": [""1004"", 1005] },
                      ""OverlaidGroups"": [""ore""], ""DeletedGroups"": [""gems""],
                      ""ItemAliases"": { ""rb"": 1006 }, ""PlotSalvage"": { ""86"": true, ""45"": false },
                      ""RepeatHuntOffPlots"": [12], ""HeartFeed"": { ""45"": true } },
          ""not-a-player"": {}
        }";

        [Fact]
        public void Maps_server_player_castle_and_categories_and_reports_the_rest()
        {
            var cat = Catalog();
            cat.SealBuiltIns();
            var r = LegacySettings.Read(File, cat);

            Assert.False(r.Server.AllowCraftFromStorage);
            Assert.False(r.Server.AllowSpawnerChests);
            Assert.Contains(r.NotImported, n => n.StartsWith("Named:"));
            Assert.False(r.Server.AllowStashFromAnywhere);
            Assert.True(r.Server.AllowRepeatHunts);
            Assert.True(r.Server.RepeatHuntsOnlyWhileClanOnline);
            Assert.Equal(95, r.Server.RepeatHuntSuccessCap);
            Assert.True(r.Server.AllowLoops); // 2.0 default, reported
            Assert.True(r.ClanShareByClanKey["c(Normal 1:1)"]);
            Assert.Equal(new[] { 7 }, r.ExcludedTerritories);
            Assert.Equal(IronOre, r.ServerAliases["ior"]);

            var p = Assert.Single(r.Players);
            Assert.Equal(1001UL, p.PlatformId);
            Assert.False(p.Me.StashOnDoubleSort);
            Assert.False(p.Me.NeverPullLastStack);
            Assert.True(p.Me.SilentStash && p.Me.StashFromAnywhere);
            Assert.False(p.Me.ServantLootStash);
            Assert.Equal(25, p.Rules.DefaultReserve);
            Assert.Equal(250, p.Rules.ItemReserve[IronOre]);
            Assert.Equal(300, p.Rules.ItemCap[Ruby]);
            Assert.Single(p.Rules.ItemCap); // "bad" key skipped
            Assert.Equal(new[] { IronOre }, p.Categories.Edited["ore"]);
            Assert.Equal(new[] { Plank, Lumber }, p.Categories.Custom["Leathers"]);
            Assert.Equal(new[] { "gems" }, p.DeletedBuiltIns);
            Assert.Equal(Ruby, p.Categories.Aliases["rb"]);
            Assert.Equal(new[] { 86 }, p.SalvageTerritories);
            Assert.Equal(new[] { 12 }, p.RepeatHuntOffTerritories);

            Assert.Contains(r.NotImported, n => n.StartsWith("AutoScoop: autoloot"));
            Assert.Contains(r.NotImported, n => n.StartsWith("StarterKitSeeded: supply lend"));
            Assert.Contains(r.NotImported, n => n.StartsWith("HeartFeed:"));
            Assert.Contains(r.NotImported, n => n.StartsWith("ConveyorLoops:"));
            Assert.Contains(r.NotImported, n => n == "Mystery: unknown field");
            Assert.Contains(r.NotImported, n => n.Contains("not-a-player"));
        }

        [Fact]
        public void Edited_and_custom_categories_apply_to_an_island_catalog_without_touching_the_server_one()
        {
            var cat = Catalog();
            cat.SealBuiltIns();
            var edits = new CategoryEdits();
            edits.Edited["ore"] = new() { IronOre };
            edits.Custom["Leathers"] = new() { Plank };
            edits.Custom["gems"] = new() { Plank }; // cannot shadow a built-in
            edits.Aliases["rb"] = Ruby;
            var island = cat.WithEdits(edits);

            Assert.False(island.InCategory(CopperOre, "ore"));
            Assert.True(cat.InCategory(CopperOre, "ore"));
            Assert.True(island.TryResolveCategory("leathers", out var key) && island.InCategory(Plank, key));
            Assert.False(island.InCategory(Plank, "gems"));
            Assert.True(island.TryResolveItem("rb", out var rb) && rb == Ruby);
            Assert.False(cat.TryResolveItem("rb", out _));
        }

        [Fact]
        public void Reads_the_real_1x_file_shape_when_a_local_copy_is_given()
        {
            // Maintainer-only: point SATIS1_SETTINGS at a read-only copy of a 1.x settings file.
            var path = System.Environment.GetEnvironmentVariable("SATIS1_SETTINGS");
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            var r = LegacySettings.Read(System.IO.File.ReadAllText(path), Catalog());
            Assert.NotEmpty(r.Players);
            Assert.DoesNotContain(r.NotImported, n => n.EndsWith("unknown field"));
        }
    }
}
