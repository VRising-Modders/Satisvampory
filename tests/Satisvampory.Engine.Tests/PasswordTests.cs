// SPDX-License-Identifier: AGPL-3.0-only
using Satisvampory.Engine.Security;
using Xunit;

namespace Satisvampory.Engine.Tests
{
    public class PasswordTests
    {
        [Fact]
        public void A_password_is_stored_salted_and_checks_only_against_itself()
        {
            var a = Passwords.Hash("fish");
            var b = Passwords.Hash("fish");
            Assert.NotEqual(a, b); // salted
            Assert.DoesNotContain("fish", a);
            Assert.True(Passwords.Verify("fish", a));
            Assert.False(Passwords.Verify("Fish", a));
            Assert.False(Passwords.Verify("", a));
            Assert.False(Passwords.Verify("fish", ""));
            Assert.False(Passwords.Verify("fish", "garbage"));
        }
    }
}
