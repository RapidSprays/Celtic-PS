using CH.Protocol;
using CH.Server.Game;

namespace CH.Server;

/// <summary>
/// Monster tagging (CombatEntityLocked 13028: varint type 1, varint serverId, varint status; TargetLockType: 1 open,
/// 2 locked by someone else, 3 owned by you or your party) and cast interrupts (SkillInterrupted 13025: varint skillId,
/// float seconds of cooldown left; 0 = ready again).
/// </summary>
public sealed partial class GameServer
{
    private const int LockOpen = 1, LockLocked = 2, LockOwned = 3;

    /// <summary>Monster special attacks: 3-star monsters and up, approximated (real monster skills are server-only).</summary>
    private const int SpecialAttackMinPower = 3;
    private const double SpecialAttackChance = 0.3, SpecialAttackCooldown = 12, SpecialAttackMultiplier = 2.0;

    private static bool SameParty(Session a, Session b) => ReferenceEquals(a, b) || (a.Party is { } p && ReferenceEquals(p, b.Party));

    private static bool OwnsOrShares(Session s, Mob mob) => mob.Owner is Session o && SameParty(o, s);

    private static bool OwnerStillHere(Zone zone, Mob mob) => mob.Owner is Session o && o.InZone && !o.Dead && zone.Players.Contains(o);

    private void SetMobOwner(Zone zone, Mob mob, Session? owner)
    {
        if (ReferenceEquals(mob.Owner, owner)) return;
        mob.Owner = owner;
        foreach (var p in zone.Players.Where(p => p.InZone))
        {
            var status = owner is null ? LockOpen : SameParty(owner, p) ? LockOwned : LockLocked;
            Send(p, Opcode.CombatEntityLocked, b =>
            {
                b.WriteVariableInt32(1);
                b.WriteVariableInt32((int)mob.ServerId);
                b.WriteVariableInt32(status);
            });
        }
    }

    /// <summary>
    /// Being hit while casting can break the cast: the chance is 100 − the skill rank's ChargingProtection (client data).
    /// An interrupted skill is ready again at once.
    /// </summary>
    private void MaybeInterrupt(Session s, double now)
    {
        foreach (var cast in s.Casts.Where(c => c.ResolveAt > now).ToList())
        {
            var protection = cast.Skill.Level(cast.Level).ChargingProtection;
            if (Random.Shared.Next(100) < protection) continue;
            s.Casts.Remove(cast);
            Send(s, Opcode.SkillInterrupted, b =>
            {
                b.WriteVariableInt32(cast.Skill.Id);
                b.Write(0f);
            });
        }
    }
}
