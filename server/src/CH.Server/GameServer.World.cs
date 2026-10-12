using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Zones, mobs and combat (docs/protocol-notes.md §5). Everything runs on the pump thread: messages and the 10 Hz
/// <see cref="Tick"/>. Times are the server's <see cref="NetTime"/>, which is the clock the client syncs to.
/// Numbers for mobs and damage are approximated (Game/Mob.cs MobStats).
/// </summary>
public sealed partial class GameServer
{
    private const double TickSeconds = 0.1;
    private const double RegenIntervalSeconds = 3;
    private const double OutOfCombatSeconds = 6;
    private const float MeleeTolerance = 1.5f;
    private const float PlayerRadius = 0.3f;
    private const double MobCorpseSeconds = 4;

    private sealed class Zone
    {
        public required int Id { get; init; }
        public Dictionary<uint, Mob> Mobs { get; } = new();
        public List<Session> Players { get; } = new();
        public double NextBatchAt { get; set; }
    }

    private readonly Dictionary<int, Zone> _zones = new();
    private double _nextTick;
    private int _damageId;

    // ---- zones ------------------------------------------------------------------------------

    private Zone ZoneFor(int zoneId)
    {
        if (_zones.TryGetValue(zoneId, out var zone)) return zone;
        zone = new Zone { Id = zoneId };
        var now = NetTime.Now;
        foreach (var mob in ZonePopulation.Build(zoneId, _balance, now, _generatedCamps)) zone.Mobs[mob.ServerId] = mob;
        foreach (var spawn in _store.ListSpawns(zoneId))
            if (MobFromSpawn(zone, spawn, now) is { } mob) zone.Mobs[mob.ServerId] = mob;
        _zones[zoneId] = zone;
        _log.LogInformation("Zone {Zone} populated: {Mobs} monsters, {Npcs} NPCs", zoneId,
            zone.Mobs.Values.Count(m => !m.IsNpc), zone.Mobs.Values.Count(m => m.IsNpc));
        return zone;
    }

    /// <summary>GM-placed spawn → live mob. NPCs reuse an official spawn id when one carries their quest/shop icons.</summary>
    private Mob? MobFromSpawn(Zone zone, SpawnRecord spawn, double now)
    {
        if (!_balance.Mobs.TryGetValue(spawn.TemplateId, out var template)) return null;
        var serverId = 20000 + (uint)spawn.Id;
        if (spawn.Opinion >= 50 && _balance.SpawnIconIdsByMob.TryGetValue(spawn.TemplateId, out var iconIds))
            serverId = iconIds.Select(i => (uint)i).FirstOrDefault(i => !zone.Mobs.ContainsKey(i), serverId);
        var mob = new Mob
        {
            ServerId = serverId,
            Template = template,
            Level = spawn.Level,
            HomeX = spawn.X,
            HomeY = spawn.Y,
            HomeZ = spawn.Z,
            Direction = spawn.Direction,
            Opinion = spawn.Opinion,
            ConversationId = spawn.ConversationId,
            AggroRange = spawn.Opinion >= 50 ? 0 : MobStats.AggroRange(template.Power),
            RespawnSeconds = spawn.RespawnSeconds,
            RoamRadius = spawn.RoamRadius,
            SpawnRecordId = spawn.Id,
        };
        mob.Reset(now);
        return mob;
    }

    private static MobSpawn ToWire(Mob m, double now)
    {
        var (x, z) = m.PositionAt(now);
        var rad = m.Direction * MathF.PI / 180f;
        return new MobSpawn(m.ServerId, (uint)m.Template.Id, x, m.HomeY, z, MathF.Sin(rad), MathF.Cos(rad), m.Opinion,
            Math.Max(0, m.Health), 0, m.ConversationId, m.IsNpc ? 0 : m.Level, m.AggroRange, 1f);
    }

    private void EnterZone(Session s)
    {
        var zone = ZoneFor(s.Position.Zone);
        var wasPresent = zone.Players.Contains(s);
        if (!wasPresent) zone.Players.Add(s);
        s.InZone = true;
        var now = NetTime.Now;
        var (_, x, y, z, _) = s.Position;
        var others = Others(zone, s).Select(Basic).ToList();
        Send(s, Opcode.EnteringNewZoneUpdate, new EnteringNewZoneUpdate(x, y, z) { Players = others }.Write);
        var alive = zone.Mobs.Values.Where(m => m.State != MobState.Dead).ToList();
        if (alive.Count > 0) Send(s, Opcode.BatchZoneMonsterUpdate, b => MobSpawn.WriteBatch(b, alive.Select(m => ToWire(m, now)).ToList()));
        var moving = alive.Where(m => m.IsMoving(now)).Select(m => Leg(m, returning: m.State == MobState.Returning)).ToList();
        if (moving.Count > 0) Send(s, Opcode.mobPatrolUpdate, new MobPatrol(moving).Write);
        Send(s, Opcode.AllZoningMessagesSent, _ => { });
        AnnounceArrival(zone, s, wasPresent);
    }

    /// <summary>Leaves the current zone on disconnect or zone change (buffs survive a zone change).</summary>
    private void LeaveZone(Session s, bool changingZone = false)
    {
        if (!_zones.TryGetValue(s.Position.Zone, out var zone)) return;
        SetAttackTarget(s, null);
        if (zone.Players.Remove(s)) AnnounceDeparture(zone, s);
        s.InZone = false;
        s.Casts.Clear();
        if (!changingZone) s.Effects.Clear();
        foreach (var mob in zone.Mobs.Values.Where(m => ReferenceEquals(m.Target, s))) StartReturn(zone, mob, NetTime.Now);
        if (s.Character is not null) _store.SaveVitals(s.Character.Id, s.Dead ? -1 : s.Health, s.Energy);
    }

    private void Broadcast(Zone zone, Opcode opcode, Action<NetBuffer> write)
    {
        foreach (var p in zone.Players.ToList()) Send(p, opcode, write);
    }

    private static MobLeg Leg(Mob m, bool returning) => new(m.ServerId, m.LegEta, m.FromX, m.FromZ, m.ToX, m.ToZ, returning);

    private void BroadcastLeg(Zone zone, Mob m, bool returning = false) =>
        Broadcast(zone, Opcode.mobPatrolUpdate, new MobPatrol([Leg(m, returning)]).Write);

    // ---- tick -------------------------------------------------------------------------------

    private void MaybeTick()
    {
        var now = NetTime.Now;
        if (now < _nextTick) return;
        _nextTick = now + TickSeconds;
        foreach (var zone in _zones.Values.Where(z => z.Players.Count > 0).ToList()) Tick(zone, now);
        TickDuels(now);
    }

    private void Tick(Zone zone, double now)
    {
        foreach (var mob in zone.Mobs.Values.ToList()) TickMob(zone, mob, now);
        foreach (var s in zone.Players.ToList()) TickPlayer(zone, s, now);
        TickPositions(zone, now);
    }

    private void TickMob(Zone zone, Mob mob, double now)
    {
        if (mob.State == MobState.Dead)
        {
            if (!mob.Removed && now >= mob.RemoveAt)
            {
                mob.Removed = true;
                Broadcast(zone, Opcode.ZoneMonsterDisappeared, b => b.WriteVariableUInt32(mob.ServerId));
            }
            if (now >= mob.RespawnAt)
            {
                mob.Reset(now);
                Broadcast(zone, Opcode.ZoneMonsterAppeared, ToWire(mob, now).Write);
            }
            return;
        }
        if (mob.IsNpc) return;
        TickMobEffects(zone, mob, now);
        if (mob.State == MobState.Dead) return;

        switch (mob.State)
        {
            case MobState.Idle:
                if (mob.AggroRange > 0 && NearestVictim(zone, mob, mob.AggroRange, now) is { } victim)
                {
                    Engage(zone, mob, victim, now);
                    break;
                }
                if (mob.RoamRadius > 0 && now >= mob.NextThinkAt && !mob.IsMoving(now))
                {
                    var angle = Random.Shared.NextDouble() * Math.PI * 2;
                    var dist = Math.Sqrt(Random.Shared.NextDouble()) * mob.RoamRadius;
                    var eta = mob.MoveTo(now, mob.HomeX + (float)(Math.Cos(angle) * dist), mob.HomeZ + (float)(Math.Sin(angle) * dist), Mob.RoamSpeed);
                    mob.NextThinkAt = eta + 4 + Random.Shared.NextDouble() * 8;
                    BroadcastLeg(zone, mob);
                }
                break;

            case MobState.Chasing:
            case MobState.Fighting:
                if (mob.Target is not Session target || !target.InZone || target.Dead || !zone.Players.Contains(target)
                    || mob.DistanceFromHome(now) > Mob.LeashDistance)
                {
                    StartReturn(zone, mob, now);
                    break;
                }
                var (mx, mz) = mob.PositionAt(now);
                var (_, px, _, pz, _) = target.Position;
                var distance = MathF.Sqrt((px - mx) * (px - mx) + (pz - mz) * (pz - mz));
                var reach = mob.Template.AttackRange + mob.Template.Radius + PlayerRadius + 0.5f;
                if (distance > reach)
                {
                    mob.State = MobState.Chasing;
                    if (now >= mob.NextThinkAt)
                    {
                        var stop = Math.Max(0.5f, reach * 0.7f) / Math.Max(distance, 0.01f);
                        mob.MoveTo(now, px - (px - mx) * stop, pz - (pz - mz) * stop, Mob.ChaseSpeed);
                        mob.NextThinkAt = now + 0.5;
                        BroadcastLeg(zone, mob);
                    }
                    break;
                }
                if (mob.State == MobState.Chasing || mob.IsMoving(now))
                {
                    mob.StopAt(now);
                    BroadcastLeg(zone, mob);
                }
                mob.State = MobState.Fighting;
                if (!mob.AttackAnnounced)
                {
                    mob.AttackAnnounced = true;
                    Broadcast(zone, Opcode.AttackState, new AttackState(true, true, (int)mob.ServerId, false, target.CharacterId).Write);
                    mob.NextAttackAt = Math.Max(mob.NextAttackAt, now + 0.5);
                }
                if (now >= mob.NextAttackAt)
                {
                    mob.NextAttackAt = now + MobStats.AttackInterval(mob.Template);
                    MobHits(zone, mob, target, now);
                }
                break;

            case MobState.Returning:
                if (now >= mob.LegEta)
                {
                    mob.State = MobState.Idle;
                    mob.Health = mob.MaxHealth;
                    mob.NextThinkAt = now + 3;
                    Broadcast(zone, Opcode.NewEntitiesOfInterest, b =>
                    {
                        b.WriteVariableInt32(1);
                        b.WriteVariableInt32(1);
                        b.WriteVariableInt32((int)mob.ServerId);
                        b.WriteVariableInt32(mob.Health);
                        b.Write((byte)0);
                        b.Write(mob.HomeX);
                        b.Write(mob.HomeY);
                        b.Write(mob.HomeZ);
                    });
                }
                break;
        }
    }

    private Session? NearestVictim(Zone zone, Mob mob, float range, double now)
    {
        var (mx, mz) = mob.PositionAt(now);
        return zone.Players
            .Where(p => p.InZone && !p.Dead && !IsHiddenFrom(p, mob))
            .Select(p => (Player: p, Dist: MathF.Sqrt((p.Position.X - mx) * (p.Position.X - mx) + (p.Position.Z - mz) * (p.Position.Z - mz))))
            .Where(p => p.Dist <= range * (100 - Modifiers(p.Player).CamouflagePercent) / 100f)
            .OrderBy(p => p.Dist)
            .Select(p => p.Player)
            .FirstOrDefault();
    }

    private void Engage(Zone zone, Mob mob, Session target, double now)
    {
        if (mob.IsNpc || mob.State == MobState.Dead) return;
        if (mob.State is MobState.Chasing or MobState.Fighting && ReferenceEquals(mob.Target, target)) return;
        mob.Target = target;
        mob.State = MobState.Chasing;
        mob.AttackAnnounced = false;
        mob.NextThinkAt = now;
        Broadcast(zone, Opcode.EntityCombatStateChanged, new CombatStateChanged([(1, (int)mob.ServerId, true)]).Write);
    }

    private void StartReturn(Zone zone, Mob mob, double now)
    {
        if (mob.AttackAnnounced && mob.Target is Session t)
            Broadcast(zone, Opcode.AttackState, new AttackState(false, true, (int)mob.ServerId, false, t.CharacterId).Write);
        mob.Target = null;
        mob.AttackAnnounced = false;
        mob.State = MobState.Returning;
        mob.Damage.Clear();
        mob.MoveTo(now, mob.HomeX, mob.HomeZ, Mob.ChaseSpeed);
        BroadcastLeg(zone, mob, returning: true);
        Broadcast(zone, Opcode.EntityCombatStateChanged, new CombatStateChanged([(1, (int)mob.ServerId, false)]).Write);
    }

    // ---- player combat ----------------------------------------------------------------------

    private void TickPlayer(Zone zone, Session s, double now)
    {
        if (s.Dead) return;

        TickPvpAttack(zone, s, now);
        if (s.AttackTarget is { } target)
        {
            if (target.State == MobState.Dead || !zone.Mobs.ContainsKey(target.ServerId)) SetAttackTarget(s, null);
            else if (now >= s.NextSwingAt)
            {
                var sheet = s.Sheet!;
                if (InReach(s, target, sheet.AttackRange + MeleeTolerance, now))
                {
                    var stats = LiveStats(s);
                    var ranged = sheet.AttackRange > 3 ? Modifiers(s).RangedDamage : 0;
                    var damage = sheet.WeaponDamage + stats.Attack / 3 + sheet.Level + ranged;
                    PlayerHits(zone, s, target, AttackKind.Attack, 0, damage, now);
                    s.NextSwingAt = now + stats.AttackSpeed / 1000.0 + 0.1;
                }
                else
                {
                    s.NextSwingAt = now + 0.25;
                }
            }
        }

        foreach (var cast in s.Casts.Where(c => now >= c.ResolveAt).ToList())
        {
            s.Casts.Remove(cast);
            ResolveCast(zone, s, cast, now);
        }

        TickEffects(zone, s, now);

        if (now >= s.NextRegenAt)
        {
            s.NextRegenAt = now + RegenIntervalSeconds;
            var outOfCombat = now - s.LastCombatAt > OutOfCombatSeconds;
            var m = Modifiers(s);
            var health = Math.Min(s.MaxHealth, s.Health + (outOfCombat ? Math.Max(1, s.MaxHealth / 20) : 0) + m.HealthRegen);
            var energy = Math.Min(s.MaxEnergy, s.Energy + (outOfCombat ? Math.Max(1, s.MaxEnergy / 10) : 1) + m.EnergyRegen);
            if (health != s.Health || energy != s.Energy)
            {
                s.Health = health;
                s.Energy = energy;
                Send(s, Opcode.StatsUpdate, new StatsDelta([(StatKey.Health, s.Health), (StatKey.Energy, s.Energy)]).Write);
                ShareHealth(zone, s);
            }
        }
    }

    private static bool InReach(Session s, Mob mob, float range, double now)
    {
        var (mx, mz) = mob.PositionAt(now);
        var dx = s.Position.X - mx;
        var dz = s.Position.Z - mz;
        return MathF.Sqrt(dx * dx + dz * dz) - mob.Template.Radius - PlayerRadius <= range;
    }

    private void OnStartAttacking(Session s, StartAttacking req)
    {
        if (req.TargetType == TargetType.OtherPlayer)
        {
            if (Enemy(s, (int)req.TargetId) is not { } enemy) return;
            SetAttackTarget(s, null);
            SetPvpTarget(s, enemy);
            s.NextSwingAt = Math.Max(s.NextSwingAt, NetTime.Now + 0.3);
            BreakStealth(s);
            return;
        }
        if (req.TargetType != TargetType.Mob || !_zones.TryGetValue(s.Position.Zone, out var zone)) return;
        if (!zone.Mobs.TryGetValue(req.TargetId, out var mob) || mob.IsNpc || mob.State == MobState.Dead) return;
        SetPvpTarget(s, null);
        SetAttackTarget(s, mob);
        s.NextSwingAt = Math.Max(s.NextSwingAt, NetTime.Now + 0.3);
        BreakStealth(s);
    }

    private void OnUseSkill(Session s, UseSkill req)
    {
        var now = NetTime.Now;
        var known = s.Sheet!.UsableSkills.FirstOrDefault(k => k.SkillId == (int)req.SkillId);
        if (known == default || !_balance.Skills.TryGetValue((int)req.SkillId, out var skill) || s.Dead)
        {
            Send(s, Opcode.SkillUpdate, new SkillUpdate((int)req.SkillId, 0, 0).Write);
            return;
        }
        var level = skill.Level(known.Level);
        Mob? target = null;
        Session? enemy = null;
        if (skill.CastTarget == "Enemy" && req.TargetType == TargetType.OtherPlayer)
        {
            if (Enemy(s, (int)req.TargetId) is { } e
                && Dist2D(s.Position.X, s.Position.Z, e.Position.X, e.Position.Z) - 2 * PlayerRadius <= Math.Max(skill.CastRange, s.Sheet.AttackRange) + MeleeTolerance + 1f)
                enemy = e;
        }
        else if (skill.CastTarget == "Enemy")
        {
            if (_zones.TryGetValue(s.Position.Zone, out var zone) && zone.Mobs.TryGetValue(req.TargetId, out var mob)
                && !mob.IsNpc && mob.State != MobState.Dead && InReach(s, mob, Math.Max(skill.CastRange, s.Sheet.AttackRange) + MeleeTolerance + 1f, now))
                target = mob;
        }
        var onCooldown = s.Cooldowns.TryGetValue(skill.Id, out var readyAt) && now < readyAt - 0.25;
        if ((skill.CastTarget == "Enemy" && target is null && enemy is null) || onCooldown || s.Energy < level.EnergyCost)
        {
            Send(s, Opcode.SkillUpdate, new SkillUpdate(skill.Id, known.Level, 0).Write); // tells the client the skill failed
            return;
        }
        s.Casts.Add(new PendingCast(skill, known.Level, target, now + level.CastingTime, enemy));
    }

    private void OnCancelSkill(Session s, uint skillId)
    {
        s.Casts.RemoveAll(c => c.Skill.Id == (int)skillId);
        Send(s, Opcode.SkillUpdate, new SkillUpdate((int)skillId, 0, 0).Write);
    }

    private void ResolveCast(Zone zone, Session s, PendingCast cast, double now)
    {
        var level = cast.Skill.Level(cast.Level);
        if (s.Energy < level.EnergyCost || (cast.Target is { } t && t.State == MobState.Dead))
        {
            Send(s, Opcode.SkillUpdate, new SkillUpdate(cast.Skill.Id, cast.Level, 0).Write);
            return;
        }
        s.Energy -= level.EnergyCost;
        s.Cooldowns[cast.Skill.Id] = now + level.RechargeTime;
        Send(s, Opcode.SkillUpdate, new SkillUpdate(cast.Skill.Id, cast.Level, Math.Max(0.5f, level.RechargeTime)).Write);
        Send(s, Opcode.StatsUpdate, new StatsDelta([(StatKey.Energy, s.Energy)]).Write);

        var amount = Math.Abs(level.InitialDamage) + Math.Abs(level.BaseDamage);
        if (cast.PlayerTarget is { } enemy)
        {
            var damage = (int)amount + (cast.Skill.IncludesWeaponAttack ? s.Sheet!.WeaponDamage : 0) + LiveStats(s).Attack / 4 + s.Sheet!.SkillDamageBonus;
            if (Enemy(s, enemy.CharacterId) is not null) PlayerHitsPlayer(zone, s, enemy, AttackKind.Skill, cast.Skill.Id, Math.Max(1, damage), now, allowMiss: false);
            return;
        }
        if (cast.Target is { } mob)
        {
            var damage = (int)amount + (cast.Skill.IncludesWeaponAttack ? s.Sheet!.WeaponDamage : 0) + LiveStats(s).Attack / 4 + s.Sheet!.SkillDamageBonus;
            PlayerHits(zone, s, mob, AttackKind.Skill, cast.Skill.Id, Math.Max(1, damage), now, allowMiss: false);
            if (cast.Skill.StatusEffectId > 0 && mob.State != MobState.Dead) ApplyMobEffect(mob, s, cast.Skill, cast.Level, now);
            return;
        }
        if (cast.Skill.StatusEffectId > 0) ApplyEffect(s, cast.Skill, cast.Level, now);
        if (level.InitialDamage < 0 || level.BaseDamage < 0)
        {
            var heal = Math.Max(1, (int)amount);
            s.Health = Math.Min(s.MaxHealth, s.Health + heal);
            var entry = new DamageEntry(DamageDirection.PlayerToPlayer, s.CharacterId, s.CharacterId, AttackKind.Skill, cast.Skill.Id,
                -heal, s.Health, Reaction.SkillHitPositive, ++_damageId, false);
            SendPlayerDamage(zone, s, entry);
        }
    }

    private void PlayerHits(Zone zone, Session s, Mob mob, AttackKind kind, int skillId, int baseDamage, double now, bool allowMiss = true)
    {
        s.LastCombatAt = now;
        BreakStealth(s);
        var roll = Random.Shared.NextDouble();
        var reaction = kind is AttackKind.Skill or AttackKind.StatusEffect ? Reaction.SkillHitAggressive : Reaction.Hit;
        var critical = false;
        int damage;
        if (allowMiss && kind == AttackKind.Attack && roll < 0.08)
        {
            reaction = Reaction.Miss;
            damage = 0;
        }
        else
        {
            damage = (int)Math.Round(baseDamage * (0.8 + Random.Shared.NextDouble() * 0.4));
            if (roll > 0.95)
            {
                critical = true;
                damage = (int)(damage * 1.5);
            }
            damage = Math.Max(1, damage);
        }
        mob.Health -= damage;
        mob.Damage[s] = mob.Damage.GetValueOrDefault(s) + damage;
        var procs = kind == AttackKind.Attack && damage > 0 && mob.Health > 0 ? RollProcs(s) : [];
        var entry = new DamageEntry(DamageDirection.PlayerToMob, s.CharacterId, (int)mob.ServerId, kind, skillId, damage, mob.Health, reaction, ++_damageId, critical);
        Broadcast(zone, Opcode.CombatDamageMessage, new CombatDamage([entry], [new HealthUpdate(HealthUpdate.Mob, (int)mob.ServerId, Math.Max(0, mob.Health))]).Write);
        if (mob.Health <= 0) KillMob(zone, mob, s, now);
        else if (mob.State is MobState.Idle or MobState.Returning) Engage(zone, mob, s, now);
        foreach (var (proc, level) in procs)
        {
            if (mob.State == MobState.Dead) break;
            var lvl = proc.Level(level);
            var procDamage = (int)(Math.Abs(lvl.InitialDamage) + Math.Abs(lvl.BaseDamage));
            if (procDamage > 0) PlayerHits(zone, s, mob, AttackKind.AttackTriggeredSkill, proc.Id, procDamage, now, allowMiss: false);
            if (proc.StatusEffectId > 0 && mob.State != MobState.Dead)
            {
                if (proc.CastTarget == "Enemy") ApplyMobEffect(mob, s, proc, level, now);
                else ApplyEffect(s, proc, level, now);
            }
        }
    }

    /// <summary>Equipment procs (ProcSkillID at ProcSkillChance) that trigger on this hit.</summary>
    private List<(SkillInfo Skill, int Level)> RollProcs(Session s) =>
        s.Sheet!.Gear.Where(g => g.ProcSkillId > 0 && g.ProcSkillChance > 0 && Random.Shared.NextDouble() < g.ProcSkillChance)
            .Select(g => (Skill: _balance.Skills.GetValueOrDefault(g.ProcSkillId), g.ProcSkillLevel))
            .Where(p => p.Skill is not null).Select(p => (p.Skill!, p.ProcSkillLevel)).ToList();

    private void MobHits(Zone zone, Mob mob, Session target, double now)
    {
        target.LastCombatAt = now;
        BreakStealth(target);
        var m = Modifiers(target);
        var miss = Random.Shared.NextDouble() < Math.Min(0.5, 0.1 + m.Defence / 200.0);
        var armour = target.Sheet!.Armour + m.Armour;
        var damage = miss ? 0 : Math.Max(1, (int)Math.Round(MobStats.HitDamage(mob.Level, mob.Template.Power) * (0.8 + Random.Shared.NextDouble() * 0.4)) - armour / 10);
        var absorbed = damage - AbsorbWithShields(target, damage);
        target.Health = Math.Max(0, target.Health - (damage - absorbed));
        var entry = new DamageEntry(DamageDirection.MobToPlayer, (int)mob.ServerId, target.CharacterId, AttackKind.Attack, 0, damage, target.Health,
            miss ? Reaction.Miss : Reaction.Hit, ++_damageId, false);
        SendPlayerDamage(zone, target, entry);
        if (target.Health <= 0)
        {
            PlayerDies(zone, target, now);
            return;
        }
        if (!miss && m.DamageShield > 0 && mob.State != MobState.Dead)
        {
            var shield = target.Effects.First(e => e.Type == EffectType.DamageShield);
            PlayerHits(zone, target, mob, AttackKind.StatusEffect, shield.Info.Id, m.DamageShield, now, allowMiss: false);
        }
    }

    private void KillMob(Zone zone, Mob mob, Session killer, double now)
    {
        if (mob.AttackAnnounced && mob.Target is Session t)
            Broadcast(zone, Opcode.AttackState, new AttackState(false, true, (int)mob.ServerId, false, t.CharacterId).Write);
        mob.State = MobState.Dead;
        mob.Target = null;
        mob.AttackAnnounced = false;
        mob.StopAt(now);
        mob.RemoveAt = now + MobCorpseSeconds;
        mob.RespawnAt = now + Math.Max(MobCorpseSeconds + 1, mob.RespawnSeconds);
        foreach (var p in zone.Players.Where(p => ReferenceEquals(p.AttackTarget, mob)).ToList()) SetAttackTarget(p, null);
        Reward(killer, mob);
    }

    private void Reward(Session s, Mob mob)
    {
        var c = s.Character!;
        var progress = _store.GetProgress(c.Id);
        var xp = MobStats.Xp(mob.Level, mob.Template.Power);
        var coins = MobStats.Coins(mob.Level, mob.Template.Power);
        var loot = new List<(int, int)>();
        if (RollChestDrop() is { } chestDrop)
        {
            loot.Add((chestDrop, 1));
            GrantItem(c.Id, chestDrop, 1);
        }
        if (Random.Shared.NextDouble() < 0.2)
        {
            loot.Add((ClassKit.MinorHealthPotion, 1));
            var stack = _store.ListItems(c.Id).FirstOrDefault(i => i.Slot == ItemRecord.Backpack && i.TemplateId == ClassKit.MinorHealthPotion);
            if (stack is not null) _store.SetItemQuantity(stack.Id, stack.Quantity + 1);
            else _store.AddItem(c.Id, ClassKit.MinorHealthPotion, 1);
        }

        _store.SaveProgress(c.Id, progress.Level, progress.Xp, progress.Coins + coins);
        var newLevel = AddXp(s, xp, out var sheet);
        Send(s, Opcode.WonCombat, new WonCombat(mob.Template.Id, (int)mob.ServerId, s.CharacterId, xp, sheet.XpPercent, coins, loot, sheet.InventoryWithMoney()).Write);
        if (newLevel > progress.Level) AnnounceLevel(s, newLevel);
        SharePartyXp(s, xp);
    }

    /// <summary>Adds XP, levelling up as many times as it covers. Returns the new level.</summary>
    private int AddXp(Session s, int xp, out CharacterSheet sheet)
    {
        var progress = _store.GetProgress(s.Character!.Id);
        var level = Math.Max(1, progress.Level);
        var totalXp = Math.Max(0, progress.Xp + xp);
        while (level < Progression.MaxLevel && totalXp >= Progression.XpToNextLevel(level))
        {
            totalXp -= Progression.XpToNextLevel(level);
            level++;
        }
        _store.SaveProgress(s.Character.Id, level, totalXp, progress.Coins);
        sheet = RefreshSheet(s);
        return level;
    }

    /// <summary>Tells the client about a new level and refills health and energy.</summary>
    private void AnnounceLevel(Session s, int level)
    {
        s.Health = int.MaxValue;
        s.Energy = int.MaxValue;
        Send(s, Opcode.LevelUp, new LevelUp(level).Write);
        SendProgress(s); // new maximums, attribute and skill points, raised skill rank caps
        BroadcastAppearance(s); // the level shown to others
        if (s.Party is { } party) SendRoster(party);
        _log.LogInformation("{Name} is now level {Level}", s.Name, level);
    }

    /// <summary>Reloads the sheet after progress or gear changes, carrying current HP and energy over.</summary>
    private CharacterSheet RefreshSheet(Session s)
    {
        var sheet = LoadSheet(s.Character!);
        s.Sheet = sheet;
        _ = LiveStats(s); // recomputes maxima with active effects and clamps current values
        return sheet;
    }

    private void PlayerDies(Zone zone, Session s, double now)
    {
        s.Dead = true;
        s.AttackTarget = null;
        s.Casts.Clear();
        s.Effects.Clear();
        foreach (var mob in zone.Mobs.Values.Where(m => ReferenceEquals(m.Target, s)).ToList()) StartReturn(zone, mob, now);
        _log.LogInformation("{Name} died", s.Name);
    }

    /// <summary>RessurectionRequest: revive at the nearest leystone with full health (no item revives yet).</summary>
    private void OnResurrect(Session s, ResurrectionRequest req)
    {
        if (!s.Dead) return;
        var (zoneId, x, y, z, dir) = s.Position;
        if (_balance.Zones.TryGetValue(zoneId, out var zone) && zone.PlayerSpawns.Count > 0)
        {
            var nearest = zone.PlayerSpawns.OrderBy(p => (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z)).First();
            (x, y, z) = (nearest.X, nearest.Y, nearest.Z);
        }
        s.Dead = false;
        s.Health = s.MaxHealth;
        s.Energy = s.MaxEnergy;
        s.Position = (zoneId, x, y, z, dir);
        _store.SavePosition(s.Character!.Id, zoneId, x, y, z, dir);
        _store.SaveVitals(s.Character.Id, s.Health, s.Energy);
        Send(s, Opcode.RespawnPlayer, new RespawnPlayer([((uint)s.CharacterId, x, y, z, s.Health)]).Write);
        // Other clients hold other players' health out of 50.
        if (_zones.TryGetValue(zoneId, out var here))
        {
            SendToOthers(here, s, Opcode.RespawnPlayer, new RespawnPlayer([((uint)s.CharacterId, x, y, z, 50)]).Write);
            here.NextBatchAt = 0;
        }
        SendEffects(s); // clears buff icons and resends full stats
    }
}
