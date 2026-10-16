using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Duels (PVPMessage, CH_PVPNetworkHandler / CDuelTarget). The client starts its duel HUD when the opponent
/// appears in its PlayerHateList, and only then lets the player attack them. Duels end at 1 HP (nobody dies), after
/// the time limit, or when the two move apart. Durations, range and damage are <b>approximated</b>.
/// </summary>
public sealed partial class GameServer
{
    private const double DuelCountdownSeconds = 5;
    private const double DuelSeconds = 180;
    private const float DuelRange = 60f;

    private enum PvpType
    {
        ClientRequest = 0,
        ServerRequest = 1,
        ClientReply = 2,
        ServerReply = 3,
        Begin = 4,
        End = 5,
    }

    /// <summary>CDuelTarget.DUEL_END_CONDITIONS.</summary>
    private enum DuelEnd
    {
        Draw = 1,
        Victory = 2,
        Defeat = 3,
        TimeOut = 4,
        RangeOut = 5,
        Unknown = 6,
    }

    private sealed class Duel
    {
        public required Session A { get; init; }
        public required Session B { get; init; }
        public double StartsAt { get; init; }
        public double EndsAt { get; init; }
        public bool Started { get; set; }

        public Session Other(Session s) => ReferenceEquals(s, A) ? B : A;
    }

    private readonly List<Duel> _duels = new();

    private void OnPvpMessage(Session s, NetIncomingMessage msg)
    {
        switch ((PvpType)msg.ReadVariableInt32())
        {
            case PvpType.ClientRequest:
                ChallengeToDuel(s, (int)msg.ReadVariableUInt32());
                break;
            case PvpType.ClientReply:
                var accept = msg.ReadByte() == 1;
                AnswerDuel(s, (int)msg.ReadVariableUInt32(), accept);
                break;
        }
    }

    private void ChallengeToDuel(Session s, int opponentId)
    {
        var opponent = OnlineByCharacter(opponentId);
        string? problem = opponent is null || IsBlockedBy(s, opponent) ? "That player is not online."
            : ReferenceEquals(opponent, s) ? "You can't duel yourself."
            : s.Duel is not null || opponent.Duel is not null ? "One of you is already in a duel."
            : !opponent.InZone || opponent.Position.Zone != s.Position.Zone
              || Dist2D(s.Position.X, s.Position.Z, opponent.Position.X, opponent.Position.Z) > DuelRange ? $"{opponent.Name} is too far away."
            : s.Dead || opponent.Dead ? "Nobody can duel while dead."
            : null;
        if (problem is not null)
        {
            Send(s, Opcode.PVPMessage, b => WriteReply(b, false, opponentId, problem));
            return;
        }
        opponent!.DuelInvites.Add(s.CharacterId);
        Send(opponent, Opcode.PVPMessage, b =>
        {
            b.WriteVariableInt32((int)PvpType.ServerRequest);
            b.WriteVariableUInt32((uint)s.CharacterId);
        });
    }

    private void AnswerDuel(Session s, int challengerId, bool accept)
    {
        if (!s.DuelInvites.Remove(challengerId) || OnlineByCharacter(challengerId) is not { } challenger) return;
        if (!accept)
        {
            Send(challenger, Opcode.PVPMessage, b => WriteReply(b, false, s.CharacterId, "declined"));
            return;
        }
        if (s.Duel is not null || challenger.Duel is not null || challenger.Position.Zone != s.Position.Zone)
        {
            Send(challenger, Opcode.PVPMessage, b => WriteReply(b, false, s.CharacterId, "no longer possible"));
            return;
        }
        Send(challenger, Opcode.PVPMessage, b => WriteReply(b, true, s.CharacterId, ""));
        var now = NetTime.Now;
        var duel = new Duel { A = challenger, B = s, StartsAt = now + DuelCountdownSeconds, EndsAt = now + DuelCountdownSeconds + DuelSeconds };
        _duels.Add(duel);
        foreach (var p in new[] { challenger, s })
        {
            p.Duel = duel;
            var opponent = duel.Other(p);
            Send(p, Opcode.PVPMessage, b =>
            {
                b.WriteVariableInt32((int)PvpType.Begin);
                b.Write(duel.StartsAt);
                b.Write(duel.EndsAt);
                b.WriteVariableUInt32((uint)opponent.CharacterId);
            });
        }
        _log.LogInformation("Duel: {A} vs {B}", challenger.Name, s.Name);
    }

    private static void WriteReply(NetBuffer b, bool ok, int otherId, string text)
    {
        b.WriteVariableInt32((int)PvpType.ServerReply);
        b.Write((byte)(ok ? 1 : 0));
        b.WriteVariableUInt32((uint)otherId);
        b.Write(text);
    }


    /// <summary>Countdown, time limit and distance for every duel. Runs every tick.</summary>
    private void TickDuels(double now)
    {
        foreach (var duel in _duels.ToList())
        {
            var (a, b) = (duel.A, duel.B);
            if (a.Character is null || b.Character is null || !a.InZone || !b.InZone || a.Position.Zone != b.Position.Zone || a.Dead || b.Dead)
            {
                EndDuel(duel, null, DuelEnd.Unknown);
                continue;
            }
            if (!duel.Started && now >= duel.StartsAt)
            {
                duel.Started = true;
                RefreshHateList(a);
                RefreshHateList(b);
            }
            if (now >= duel.EndsAt) EndDuel(duel, null, DuelEnd.TimeOut);
            else if (Dist2D(a.Position.X, a.Position.Z, b.Position.X, b.Position.Z) > DuelRange) EndDuel(duel, null, DuelEnd.RangeOut);
        }
    }

    /// <summary>Ends a duel: the winner (if any) sees Victory and the other Defeat; otherwise both see the reason.</summary>
    private void EndDuel(Duel duel, Session? winner, DuelEnd reason)
    {
        if (!_duels.Remove(duel)) return;
        foreach (var p in new[] { duel.A, duel.B })
        {
            p.Duel = null;
            SetPvpTarget(p, null);
            p.Casts.RemoveAll(c => c.PlayerTarget is not null);
            if (p.Character is null) continue;
            var result = winner is null ? reason : ReferenceEquals(p, winner) ? DuelEnd.Victory : DuelEnd.Defeat;
            var opponent = duel.Other(p);
            Send(p, Opcode.PVPMessage, b =>
            {
                b.WriteVariableInt32((int)PvpType.End);
                b.WriteVariableUInt32((uint)opponent.CharacterId);
                b.WriteVariableInt32((int)result);
                b.Write("");
            });
            RefreshHateList(p);
        }
        _log.LogInformation("Duel {A} vs {B} ended: {Result}{Winner}", duel.A.Name, duel.B.Name, reason, winner is null ? "" : $", {winner.Name} won");
    }


    private void SetPvpTarget(Session s, Session? target)
    {
        var old = s.PvpTarget;
        if (ReferenceEquals(old, target)) return;
        s.PvpTarget = target;
        if (!s.InZone || !_zones.TryGetValue(s.Position.Zone, out var zone)) return;
        if (old is not null) SendToOthers(zone, s, Opcode.AttackState, new AttackState(false, false, s.CharacterId, false, old.CharacterId).Write);
        if (target is not null) SendToOthers(zone, s, Opcode.AttackState, new AttackState(true, false, s.CharacterId, false, target.CharacterId).Write);
    }

    /// <summary>Auto-attack swings against a duel opponent (called from the player tick).</summary>
    private void TickPvpAttack(Zone zone, Session s, double now)
    {
        if (s.PvpTarget is not { } target) return;
        if (Enemy(s, target.CharacterId) is null)
        {
            SetPvpTarget(s, null);
            return;
        }
        if (now < s.NextSwingAt) return;
        var sheet = s.Sheet!;
        if (Dist2D(s.Position.X, s.Position.Z, target.Position.X, target.Position.Z) - 2 * PlayerRadius > sheet.AttackRange + MeleeTolerance)
        {
            s.NextSwingAt = now + 0.25;
            return;
        }
        var stats = LiveStats(s);
        PlayerHitsPlayer(zone, s, target, AttackKind.Attack, 0, AutoAttackDamage(s, player: target), now);
        s.NextSwingAt = now + stats.AttackSpeed / 1000.0;
    }

    /// <summary>
    /// Damage between players (duels and PvP zones). The damage passed in is already cut by the target's resists, as
    /// against monsters (armour for pierce, slash and crush; wards and lures count); here it takes the same roll (two-thirds
    /// to full) and ×2 crits. Defence decides misses.
    /// </summary>
    private void PlayerHitsPlayer(Zone zone, Session s, Session target, AttackKind kind, int skillId, int baseDamage, double now, bool allowMiss = true)
    {
        var duel = target.Duel is { Started: true } d && ReferenceEquals(d.Other(target), s) ? d : null;
        if (duel is null && !ZoneHostile(s, target)) return;
        s.LastCombatAt = target.LastCombatAt = now;
        BreakStealth(s);
        BreakStealth(target);
        var m = Modifiers(target);
        var miss = allowMiss && !target.Sheet!.Tree.Has(PassiveTree.IronHide)
            && Random.Shared.NextDouble() < Math.Min(0.5, 0.08 + m.Defence / 400.0 + target.Sheet.EvadeChance);
        var critical = !miss && Random.Shared.NextDouble() < s.Sheet!.CritChance;
        var damage = 0;
        if (!miss)
        {
            damage = Math.Max(1, (int)Math.Round(baseDamage * DamageRoll(s, kind) * (critical ? s.Sheet.CritMultiplier : 1)));
            damage = AbsorbWithShields(target, damage); // what's left after energy shields
        }
        LifeTap(s, damage);
        if (damage > 0 && kind is AttackKind.Attack or AttackKind.Skill) TrainCombatAbility(s, kind == AttackKind.Attack ? CriticalStrike : CriticalSkills);
        if (damage > 0) MaybeInterrupt(target, now);
        // Duels stop at 1 HP: nobody dies. PvP zones are for real.
        var lethal = target.Health - damage <= 0;
        target.Health = Math.Max(duel is null ? 0 : 1, target.Health - damage);
        var reaction = miss ? Reaction.Miss : kind is AttackKind.Skill ? Reaction.SkillHitAggressive : Reaction.Hit;
        var entry = new DamageEntry(DamageDirection.PlayerToPlayer, s.CharacterId, target.CharacterId, kind, skillId, damage, target.Health, reaction, ++_damageId, critical);
        SendPlayerDamage(zone, target, entry);
        if (damage > 0) _store.AddStat(s.Character!.Id, kind == AttackKind.Attack ? "melee_hit" : "skills_hit", damage);
        if (!lethal) return;
        if (duel is not null)
        {
            EndDuel(duel, s, DuelEnd.Victory);
            return;
        }
        PlayerDies(zone, target, now);
        PvpKill(zone, s, target);
    }
}
