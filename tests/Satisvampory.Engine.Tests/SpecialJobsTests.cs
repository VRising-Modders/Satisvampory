// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Deposit;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Stations;
using Xunit;
using static Satisvampory.Engine.Tests.TestItems;

namespace Satisvampory.Engine.Tests
{
    public class SpecialJobsTests
    {
        static readonly PlotId Home = new(1), Away = new(2);
        readonly FakeWorld _w = new();

        ContainerInfo Station(PlotId p, StationRole role, float x = 0)
        {
            var s = _w.Chest(p, role.ToString(), kind: NodeKind.Station);
            s.Role = role;
            s.X = x;
            return s;
        }

        ContainerInfo Special(PlotId p, SpecialKind kind)
        {
            var c = _w.Chest(p, kind.ToString());
            c.Deposit.Special = kind;
            return c;
        }

        [Fact]
        public void Salvage_feeds_the_devourer_on_its_own_plot_only_when_switched_on()
        {
            var chest = Special(Home, SpecialKind.Salvage);
            _w.Put(chest, Sword, 3);
            var dev = Station(Home, StationRole.Devourer);
            var otherDev = Station(Away, StationRole.Devourer);

            var r = new JobReport();
            SpecialJobs.Salvage(_w, Home, new SpecialSwitches(), r);
            Assert.Equal(3, _w.Count(chest.Id, Sword)); // castle switch defaults off

            SpecialJobs.Salvage(_w, Home, new SpecialSwitches { CastleFeedsSalvage = true }, r);
            Assert.Equal(3, _w.Count(dev.Id, Sword));
            Assert.Equal(0, _w.Count(otherDev.Id, Sword));
        }

        [Fact]
        public void Spawner_feeds_two_whole_crafts()
        {
            var chest = Special(Home, SpecialKind.Spawner);
            _w.Put(chest, BloodEssence, 100);
            var tomb = Station(Home, StationRole.UnitSpawner);
            _w.StationRecipes[tomb.Id] = new List<Recipe> { new(9, new[] { new ItemStack(BloodEssence, 15) }, new ItemStack[0], true) };
            SpecialJobs.Spawners(_w, Home, new SpecialSwitches(), new JobReport());
            Assert.Equal(30, _w.Count(tomb.Id, BloodEssence));
        }

        [Fact]
        public void Trash_empties_only_trash_chests()
        {
            var trash = Special(Home, SpecialKind.Trash);
            var keep = _w.Chest(Home, "keep");
            _w.Put(trash, Plank, 50).Put(keep, Plank, 50);
            var r = SpecialJobs.EmptyTrash(_w, _w.ContainersOn(Home), new SpecialSwitches());
            Assert.Equal(50, r.Destroyed[Plank]);
            Assert.Equal(50, _w.Count(keep.Id, Plank));
            Assert.Empty(SpecialJobs.EmptyTrash(_w, _w.ContainersOn(Home), new SpecialSwitches { AllowTrash = false }).Destroyed);
        }

        [Fact]
        public void Servant_loot_goes_home_first_then_spoils()
        {
            var cat = Catalog();
            var homeOre = _w.Chest(Home, "ore").OnlyCategory("ore");
            var awayOre = _w.Chest(Away, "ore").OnlyCategory("ore");
            var spoils = Special(Home, SpecialKind.Spoils);
            var servant = _w.Chest(new PlotId(99), "servant");
            _w.Put(servant, IronOre, 20).Put(servant, Sword, 1);
            var ctx = new DepositContext(cat, _w, new[] { Home, Away }, Home);

            var moves = Servants.StashLoot(_w, ctx, new[] { new ServantState(servant.Id, true, false) });
            Assert.Equal(20, _w.Count(homeOre.Id, IronOre));
            Assert.Equal(0, _w.Count(awayOre.Id, IronOre));
            Assert.Equal(1, _w.Count(spoils.Id, Sword));
            Assert.Contains(moves, m => m.Job == "spoils");
        }

        [Fact]
        public void Repeat_hunts_send_only_healthy_survivors_when_allowed()
        {
            var a = new ServantState(ContainerId.New(), true, false);
            var hurt = new ServantState(ContainerId.New(), true, true);
            var dead = new ServantState(ContainerId.New(), false, false);
            var all = new[] { a, hurt, dead };
            Assert.Equal(new[] { a }, Servants.Resend(all, new(true, false, true, false, false)));
            Assert.Empty(Servants.Resend(all, new(false, false, true, false, true)));
            Assert.Empty(Servants.Resend(all, new(true, true, true, false, false)));
            Assert.Empty(Servants.Resend(all, new(true, false, true, true, true)));
        }
    }
}
