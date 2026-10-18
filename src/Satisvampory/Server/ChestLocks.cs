// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Progenitor.Inventory;
using Satisvampory.Engine.Security;

namespace Satisvampory.Server
{
    /// <summary>
    /// Password chests: a private chest with a password asks everyone who opens it (Progenitor's open gate,
    /// enforced by the server). "Remember password" is kept per player and chest until the password changes.
    /// Five wrong answers lock that player out of that chest for 30 seconds.
    /// </summary>
    internal sealed class ChestLocks
    {
        const string GrantKey = "lockgrant/";
        const int MaxWrong = 5;
        const double LockoutSeconds = 30;

        readonly SatisContext _ctx;
        readonly Dictionary<(ulong, Guid), (int Wrong, double Until)> _wrong = new();
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        internal ChestLocks(SatisContext ctx) => _ctx = ctx;

        // Its own method: on a Core without the open gate (Progenitor #107) only this call fails (and is caught).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal void Attach() => _ctx.Inventory.OnOpenRequest(Decide, Answer);

        /// <summary>The password hash that applies to a chest now: set, and the chest private; else null.</summary>
        string? LockOf(Guid id)
        {
            var dto = Stores.Container(_ctx.Save, id);
            return dto != null && dto.Private && !string.IsNullOrEmpty(dto.PasswordHash) ? dto.PasswordHash : null;
        }

        ContainerOpenDecision Decide(ContainerOpenRequest q)
        {
            if (q.Container.IsNone || LockOf(q.Container.Value) == null) return ContainerOpenDecision.Allow;
            if (q.HasSessionGrant || Remembered(q.Container.Value).Contains(q.PlatformId)) return ContainerOpenDecision.Allow;
            if (LockedOut(q.PlatformId, q.Container.Value, out var wait))
                return ContainerOpenDecision.Deny($"Too many wrong passwords. Try again in {Math.Ceiling(wait):0} s.");
            return ContainerOpenDecision.Challenge("sv-lock", "Locked chest", "This chest is private. Enter its password.",
                Passwords.MaxLength, offerRemember: true);
        }

        ContainerAnswerVerdict Answer(ContainerOpenAnswer a)
        {
            var id = a.Request.Container.Value;
            var hash = LockOf(id);
            if (hash == null) return ContainerAnswerVerdict.Accept(); // unlocked meanwhile
            if (LockedOut(a.Request.PlatformId, id, out var wait))
                return ContainerAnswerVerdict.Reject($"Too many wrong passwords. Try again in {Math.Ceiling(wait):0} s.");
            if (!Passwords.Verify(a.Secret.Reveal(), hash))
            {
                var key = (a.Request.PlatformId, id);
                var n = (_wrong.TryGetValue(key, out var w) ? w.Wrong : 0) + 1;
                _wrong[key] = (n, n >= MaxWrong ? _clock.Elapsed.TotalSeconds + LockoutSeconds : 0);
                return ContainerAnswerVerdict.Reject("Wrong password.");
            }
            _wrong.Remove((a.Request.PlatformId, id));
            if (a.Remember)
            {
                var list = Remembered(id);
                if (!list.Contains(a.Request.PlatformId)) { list.Add(a.Request.PlatformId); _ctx.Save.Set(GrantKey + id.ToString("N"), list); }
            }
            return ContainerAnswerVerdict.Accept();
        }

        bool LockedOut(ulong player, Guid id, out double wait)
        {
            wait = 0;
            if (!_wrong.TryGetValue((player, id), out var w) || w.Until <= 0) return false;
            wait = w.Until - _clock.Elapsed.TotalSeconds;
            if (wait > 0) return true;
            _wrong.Remove((player, id));
            return false;
        }

        List<ulong> Remembered(Guid id) => _ctx.Save.Get<List<ulong>>(GrantKey + id.ToString("N")) ?? new List<ulong>();

        /// <summary>The password changed or went: everyone is asked again (remembered and session grants dropped).</summary>
        internal void Forget(Guid id)
        {
            _ctx.Save.Remove(GrantKey + id.ToString("N"));
            try { Revoke(id); }
            catch (Exception) { } // an older Core: no session grants to drop
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        void Revoke(Guid id) => _ctx.Inventory.RevokeOpenGrants(new ContainerId(id));
    }
}
