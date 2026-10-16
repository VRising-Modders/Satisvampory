// SPDX-License-Identifier: AGPL-3.0-only
using System.Linq;
using Satisvampory.Engine.Model;
using Satisvampory.Engine.Stations;
using Xunit;

namespace Satisvampory.Engine.Tests
{
    public class RepeatHuntTests
    {
        static readonly ServantState[] Back =
        {
            new(default, true, false), new(default, true, true), new(default, false, false),
        };

        [Fact]
        public void Healthy_survivors_go_again_when_every_switch_allows()
        {
            var r = Servants.Resend(Back, new Servants.RepeatRules(true, false, true, false, false));
            Assert.Single(r);
            Assert.True(r.Single().Alive && !r.Single().Injured);
        }

        [Fact]
        public void The_owner_can_turn_their_own_repeat_hunts_off()
        {
            Assert.Empty(Servants.Resend(Back, new Servants.RepeatRules(true, false, true, false, true, OwnerOn: false)));
        }

        [Fact]
        public void Server_castle_raid_and_online_rules_still_apply()
        {
            Assert.Empty(Servants.Resend(Back, new Servants.RepeatRules(false, false, true, false, true)));
            Assert.Empty(Servants.Resend(Back, new Servants.RepeatRules(true, false, false, false, true)));
            Assert.Empty(Servants.Resend(Back, new Servants.RepeatRules(true, false, true, true, true)));
            Assert.Empty(Servants.Resend(Back, new Servants.RepeatRules(true, true, true, false, false)));
        }
    }
}
