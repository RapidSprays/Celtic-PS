using Lidgren.Network;

namespace CH.Protocol.Messages;

// Combat loop (docs/protocol-notes.md §5). Line refs are to NetworkManager.cs unless noted. On the combat path every
// "flag" is a full byte, not a 1-bit boolean.

/// <summary>CHARACTER_TYPE used in combat requests: 0 the player itself, 1 another player, 2 a mob.</summary>
public enum TargetType
{
    Self = 0,
    OtherPlayer = 1,
    Mob = 2,
}

/// <summary>Who hit whom in a CombatDamageMessage entry.</summary>
public enum DamageDirection
{
    PlayerToMob = 0,
    MobToPlayer = 1,
    MobToMob = 2,
    PlayerToPlayer = 3,
}

/// <summary>ATTACK_TYPE.</summary>
public enum AttackKind
{
    Attack = 0,
    Skill = 1,
    StatusEffect = 2,
    AttackTriggeredSkill = 3,
    AreaSkill = 4,
    Concentration = 5,
    KillingBlow = 6,
}

/// <summary>COMBAT_REACTION_TYPES: indexes the client's hit-text strings, animations and sounds.</summary>
public enum Reaction
{
    Hit = 0,
    Miss = 1,
    Dodge = 2,
    Block = 3,
    Parry = 6,
    SkillHitAggressive = 7,
    SkillHitPositive = 8,
    NoReaction = 11,
    SkillAvoided = 12,
    ZeroDamage = 13,
}

/// <summary>StartAttackingNPC (13009), client → server (writer 4929): target type, server time, target id. Sent once per engagement.</summary>
public sealed record StartAttacking(TargetType TargetType, double ServerTime, uint TargetId)
{
    public static StartAttacking Read(NetBuffer b) => new((TargetType)b.ReadVariableInt32(), b.ReadDouble(), b.ReadVariableUInt32());

    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32((int)TargetType);
        b.Write(ServerTime);
        b.WriteVariableUInt32(TargetId);
    }
}

/// <summary>UseSkillOnNPC (13011), client → server (writer 2831), used for every target type including self.</summary>
public sealed record UseSkill(TargetType TargetType, uint TargetId, double ServerTime, uint SkillId)
{
    public static UseSkill Read(NetBuffer b) => new((TargetType)b.ReadVariableInt32(), b.ReadVariableUInt32(), b.ReadDouble(), b.ReadVariableUInt32());

    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32((int)TargetType);
        b.WriteVariableUInt32(TargetId);
        b.Write(ServerTime);
        b.WriteVariableUInt32(SkillId);
    }
}

/// <summary>One hit or heal in a CombatDamageMessage.</summary>
public readonly record struct DamageEntry(
    DamageDirection Direction,
    int CasterId,
    int TargetId,
    AttackKind Kind,
    int SkillId,
    int Damage,
    int TargetHealth,
    Reaction Reaction,
    int DamageId,
    bool Critical);

/// <summary>HP for an entity, appended to the end of a CombatDamageMessage. This is what moves HP bars.</summary>
public readonly record struct HealthUpdate(int EntityType, int ServerId, int Health)
{
    public const int Player = 0;
    public const int Mob = 1;
}

/// <summary>
/// CombatDamageMessage (13019), server → client (reader 2602). The trailing health updates have no count: the client reads
/// triples until the message ends, so nothing may follow them.
/// </summary>
public sealed record CombatDamage(IReadOnlyList<DamageEntry> Entries, IReadOnlyList<HealthUpdate> Health, int Dps = 0, int Hps = 0)
{
    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32(Dps);
        b.WriteVariableInt32(Hps);
        b.WriteVariableInt32(Entries.Count);
        foreach (var e in Entries)
        {
            b.WriteVariableInt32((int)e.Direction);
            b.WriteVariableInt32(e.CasterId);
            b.WriteVariableInt32(e.TargetId);
            b.WriteVariableInt32((int)e.Kind);
            b.WriteVariableInt32(e.SkillId);
            b.WriteVariableInt32(e.Damage);
            b.WriteVariableInt32(e.TargetHealth);
            b.WriteVariableInt32((int)e.Reaction);
            b.WriteVariableInt32(e.DamageId);
            b.WriteVariableInt32(e.Critical ? 1 : 0);
        }
        b.WriteVariableInt32(0); // no cancellations
        foreach (var h in Health)
        {
            b.WriteVariableInt32(h.EntityType);
            b.WriteVariableInt32(h.ServerId);
            b.WriteVariableInt32(h.Health);
        }
    }

    public static CombatDamage Read(NetIncomingMessage b)
    {
        var dps = b.ReadVariableInt32();
        var hps = b.ReadVariableInt32();
        var entries = new List<DamageEntry>();
        for (int i = 0, n = b.ReadVariableInt32(); i < n; i++)
        {
            entries.Add(new DamageEntry((DamageDirection)b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32(),
                (AttackKind)b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32(),
                (Reaction)b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32() == 1));
        }
        for (int i = 0, n = b.ReadVariableInt32(); i < n; i++)
        {
            b.ReadVariableInt32(); b.ReadVariableInt32(); b.ReadVariableInt32(); b.ReadVariableInt32();
        }
        var health = new List<HealthUpdate>();
        while (b.Position < b.LengthBits)
            health.Add(new HealthUpdate(b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32()));
        return new CombatDamage(entries, health, dps, hps);
    }
}

/// <summary>
/// AttackState (16004), server → client (reader 2654): starts or stops an attacker's swing loop on the client. This is
/// how a mob visibly attacks the player.
/// </summary>
public sealed record AttackState(bool Start, bool AttackerIsMob, int AttackerId, bool DefenderIsMob, int DefenderId)
{
    public void Write(NetBuffer b)
    {
        b.Write((byte)(Start ? 1 : 0));
        b.Write((byte)(AttackerIsMob ? 1 : 0));
        b.WriteVariableInt32(AttackerId);
        b.Write((byte)(DefenderIsMob ? 1 : 0));
        b.WriteVariableInt32(DefenderId);
    }
}

/// <summary>EntityCombatStateChanged (13029), server → client (reader 2451).</summary>
public sealed record CombatStateChanged(IReadOnlyList<(int EntityType, int ServerId, bool InCombat)> Entities)
{
    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32(Entities.Count);
        foreach (var (type, id, inCombat) in Entities)
        {
            b.WriteVariableInt32(type);
            b.WriteVariableInt32(id);
            b.Write((byte)(inCombat ? 1 : 0));
        }
    }
}

/// <summary>Keys of the delta form of the stats block (readStats, 3475-3799).</summary>
public enum StatKey
{
    Strength = 0,
    Dexterity = 1,
    Focus = 2,
    Vitality = 3,
    MaxHealth = 4,
    MaxEnergy = 5,
    Attack = 6,
    Defence = 7,
    Armour = 8,
    MaxSpeed = 15, // float
    AttackSpeed = 16,
    SkillPoints = 24,
    AttributePoints = 25,
    Health = 27,
    Energy = 28,
    WeaponDamage = 29,
}

/// <summary>StatsUpdate (15015) in delta form: only the listed integer stats change.</summary>
public sealed record StatsDelta(IReadOnlyList<(StatKey Key, int Value)> Changes)
{
    public void Write(NetBuffer b)
    {
        b.Write((byte)0); // delta form
        b.WriteVariableInt32(Changes.Count);
        foreach (var (key, value) in Changes)
        {
            if (key == StatKey.MaxSpeed) throw new ArgumentException("MaxSpeed is a float; send it in a full stats block");
            b.WriteVariableInt32((int)key);
            b.WriteVariableInt32(value);
        }
    }
}

/// <summary>SkillUpdate (16003), server → client (reader 2078). Completes a cast: cooldown &gt; 0 means success.</summary>
public sealed record SkillUpdate(int SkillId, int Level, float CooldownSeconds)
{
    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32(SkillId);
        b.WriteVariableInt32(Level);
        b.Write(CooldownSeconds);
    }
}

/// <summary>SkillListUpdate (16001), server → client (reader 2126): the skills the player may use.</summary>
public sealed record SkillListUpdate(int SkillPoints, IReadOnlyList<SkillEntry> Skills)
{
    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32(SkillPoints);
        b.WriteVariableInt32(Skills.Count);
        foreach (var s in Skills)
        {
            b.WriteVariableInt32(s.SkillId);
            b.WriteVariableInt32(s.Level);
            b.WriteVariableInt32(s.MaxLevel);
            b.Write(s.TimeRemaining);
        }
    }
}

/// <summary>Coins plus the whole inventory (readInventory, 3329). Used by WonCombat, InventoryUpdate, pickups and item use.</summary>
public sealed record InventoryWithMoney(int Coins, IReadOnlyList<ItemSlot> Items)
{
    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32(Coins);
        b.WriteVariableInt32(Items.Count);
        foreach (var item in Items) item.Write(b);
    }

    public static InventoryWithMoney Read(NetBuffer b)
    {
        var coins = b.ReadVariableInt32();
        var items = new List<ItemSlot>();
        for (int i = 0, n = b.ReadVariableInt32(); i < n; i++) items.Add(ItemSlot.Read(b));
        return new InventoryWithMoney(coins, items);
    }
}

/// <summary>WonCombat (13017), server → client (reader 2508). The mob template id must exist in the client balance data.</summary>
public sealed record WonCombat(
    int MobTemplateId,
    int MobServerId,
    int KillerServerId,
    int XpGained,
    float NewXpPercent,
    int CoinsGained,
    IReadOnlyList<(int TemplateId, int Quantity)> Items,
    InventoryWithMoney Inventory)
{
    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32(MobTemplateId);
        b.WriteVariableInt32(MobServerId);
        b.WriteVariableInt32(KillerServerId);
        b.WriteVariableInt32(0); // level type: combat
        b.WriteVariableInt32(XpGained);
        b.WriteVariableInt32(0); // not lucky
        b.Write(NewXpPercent);
        b.WriteVariableInt32(CoinsGained);
        b.WriteVariableInt32(0); // not lucky
        b.WriteVariableInt32(Items.Count);
        foreach (var (templateId, qty) in Items)
        {
            b.WriteVariableInt32(templateId);
            b.WriteVariableInt32(qty);
        }
        Inventory.Write(b);
    }
}

/// <summary>LevelUp (13018), server → client (reader 1550): level type, new level, then available quest lists.</summary>
public sealed record LevelUp(int NewLevel)
{
    public IReadOnlyList<int> AvailableQuests { get; init; } = [];
    public IReadOnlyList<int> NotYetAvailableQuests { get; init; } = [];

    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32(0); // combat level
        b.WriteVariableInt32(NewLevel);
        QuestMessages.WriteAvailable(b, AvailableQuests, NotYetAvailableQuests);
    }
}

/// <summary>RessurectionRequest (13041), client → server (writer 5845): use an item (byte), item inventory id (-1 = leystone).</summary>
public sealed record ResurrectionRequest(bool UseItem, int ItemInventoryId)
{
    public static ResurrectionRequest Read(NetBuffer b) => new(b.ReadByte() == 1, b.ReadVariableInt32());

    public void Write(NetBuffer b)
    {
        b.Write((byte)(UseItem ? 1 : 0));
        b.WriteVariableInt32(ItemInventoryId);
    }
}

/// <summary>RespawnPlayer (12012), server → client (reader 5860): revives characters at a position with absolute HP.</summary>
public sealed record RespawnPlayer(IReadOnlyList<(uint ServerId, float X, float Y, float Z, int Health)> Players)
{
    public void Write(NetBuffer b)
    {
        b.WriteVariableUInt32((uint)Players.Count);
        foreach (var (id, x, y, z, hp) in Players)
        {
            b.WriteVariableUInt32(id);
            b.Write(x);
            b.Write(y);
            b.Write(z);
            b.WriteVariableInt32(hp);
        }
    }
}

/// <summary>GameDataMessage (13046), server → client (reader 2998): max chat length and the delay added to every swing.</summary>
public sealed record GameData(int MaxChatLength, float AttackDelaySeconds)
{
    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32(MaxChatLength);
        b.Write(AttackDelaySeconds);
    }
}

/// <summary>Mob or NPC spawn record (ZoneMonsterAppeared 13008 / BatchZoneMonsterUpdate 13006, readers 2408 / 2848).</summary>
public sealed record MobSpawn(
    uint ServerId,
    uint TemplateId,
    float X,
    float Y,
    float Z,
    float DirX,
    float DirY,
    int Opinion,
    int Health,
    int Energy,
    int ConversationId,
    int Level,
    float AggroRange,
    float Scale)
{
    public void Write(NetBuffer b)
    {
        b.WriteVariableUInt32(ServerId);
        b.WriteVariableUInt32(TemplateId);
        b.Write(X);
        b.Write(Y);
        b.Write(Z);
        b.Write(DirX);
        b.Write(DirY);
        b.WriteVariableInt32(Opinion);
        b.WriteVariableInt32(Health);
        b.WriteVariableInt32(Energy);
        b.WriteVariableInt32(ConversationId);
        b.WriteVariableInt32(Level);
        b.Write(AggroRange);
        b.Write(Scale);
    }

    /// <summary>BatchZoneMonsterUpdate payload: varuint count, then records.</summary>
    public static void WriteBatch(NetBuffer b, IReadOnlyCollection<MobSpawn> mobs)
    {
        b.WriteVariableUInt32((uint)mobs.Count);
        foreach (var m in mobs) m.Write(b);
    }
}

/// <summary>One leg of mob movement for mobPatrolUpdate (23008, reader 2783). The ETA is in the server's NetTime clock.</summary>
public readonly record struct MobLeg(uint ServerId, double EtaServerTime, float FromX, float FromZ, float ToX, float ToZ, bool Returning);

/// <summary>mobPatrolUpdate (23008): the only message that moves mobs smoothly. From == to stops the mob.</summary>
public sealed record MobPatrol(IReadOnlyList<MobLeg> Legs)
{
    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32(Legs.Count);
        foreach (var l in Legs)
        {
            b.WriteVariableUInt32(l.ServerId);
            b.Write(l.EtaServerTime);
            b.Write(l.FromX);
            b.Write(l.FromZ);
            b.Write(l.ToX);
            b.Write(l.ToZ);
            b.Write((byte)(l.Returning ? 1 : 0));
        }
    }
}

/// <summary>HW_CHAT_BOX_CHANNEL.</summary>
public enum ChatChannel
{
    None = -1,
    Global = 0,
    Group = 1,
    Clan = 2,
    Local = 3,
    System = 4,
    Battle = 5,
    WhisperIncoming = 6,
    WhisperOutgoing = 7,
    Zone = 8,
    Trade = 9,
    Command = 10,
}

/// <summary>GeneralChat (17001). Client → server: channel, text (prefix such as "s " included). Server → client: channel, sender, text, text id.</summary>
public sealed record ChatMessage(ChatChannel Channel, string Sender, string Text)
{
    public static ChatMessage ReadFromClient(NetBuffer b) => new((ChatChannel)b.ReadVariableInt32(), "", b.ReadString());

    public static ChatMessage ReadFromServer(NetBuffer b) => new((ChatChannel)b.ReadVariableInt32(), b.ReadString(), b.ReadString());

    public void WriteToClient(NetBuffer b)
    {
        b.WriteVariableInt32((int)Channel);
        b.Write(Sender);
        b.Write(Text);
        b.WriteVariableInt32(0);
    }

    public void WriteFromClient(NetBuffer b)
    {
        b.WriteVariableInt32((int)Channel);
        b.Write(Text);
    }
}
