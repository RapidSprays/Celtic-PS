using Lidgren.Network;

namespace CH.Protocol.Messages;

/// <summary>CreateCharacterReply (12006), server → client (NetworkManager.cs:4450-4470).</summary>
public sealed record CreateCharacterReply(CommandStatus Status, uint CharacterId = 0, string Name = "", string Reason = "")
{
    public void Write(NetBuffer b)
    {
        b.Write((byte)Status);
        if (Status == CommandStatus.Accepted)
        {
            b.WriteVariableUInt32(CharacterId);
            b.Write(Name);
        }
        else
        {
            b.Write(Reason);
        }
    }

    public static CreateCharacterReply Read(NetBuffer b)
    {
        var status = (CommandStatus)b.ReadByte();
        return status == CommandStatus.Accepted
            ? new CreateCharacterReply(status, b.ReadVariableUInt32(), b.ReadString())
            : new CreateCharacterReply(status, Reason: b.ReadString());
    }
}

/// <summary>TweakReply (13021), server → client: <c>count(varint) × string filename</c> (NetworkManager.cs:1440-1453). The client adds "default" itself.</summary>
public sealed record TweakReply(IReadOnlyList<string> Files)
{
    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32(Files.Count);
        foreach (var f in Files) b.Write(f);
    }

    public static TweakReply Read(NetBuffer b)
    {
        var n = b.ReadVariableInt32();
        var files = new List<string>();
        for (var i = 0; i < n; i++) files.Add(b.ReadString());
        return new TweakReply(files);
    }
}

/// <summary>Equipment or inventory slot (NetworkManager.cs:3285-3326): six fields. A template id ≤ 0 means empty.</summary>
public sealed record ItemSlot(int ServerInventoryId, int TemplateId, int Quantity, bool Bound, int Charges, bool Favourite)
{
    public static readonly ItemSlot Empty = new(0, -1, 0, false, 0, false);

    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32(ServerInventoryId);
        b.WriteVariableInt32(TemplateId);
        b.WriteVariableInt32(Quantity);
        b.Write((byte)(Bound ? 1 : 0));
        b.WriteVariableInt32(Charges);
        b.Write((byte)(Favourite ? 1 : 0));
    }

    public static ItemSlot Read(NetBuffer b) =>
        new(b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadByte() == 1, b.ReadVariableInt32(), b.ReadByte() == 1);
}

/// <summary>A (key, value) pair of varints, the shape the client uses for abilities, bonuses, avoidances and compiled skills.</summary>
public readonly record struct IdValue(int Id, int Value);

/// <summary>Learned skill (readSkillList, NetworkManager.cs:3431): id, level, max level, cooldown remaining.</summary>
public readonly record struct SkillEntry(int SkillId, int Level, int MaxLevel, float TimeRemaining);

/// <summary>Full stats block (readStats with full = 1, NetworkManager.cs:3475-3555).</summary>
public sealed record CharacterStats
{
    public int Health { get; init; } = 100;
    public int MaxHealth { get; init; } = 100;
    public int Energy { get; init; } = 100;
    public int MaxEnergy { get; init; } = 100;
    public int Attack { get; init; } = 10;
    public int Defence { get; init; } = 10;
    public int AttackSpeed { get; init; } = 100;
    public int WeaponDamage { get; init; } = 1;
    public int ArmourValue { get; init; }
    public int Vitality { get; init; } = 10;
    public int Strength { get; init; } = 10;
    public int Focus { get; init; } = 10;
    public int Dexterity { get; init; } = 10;
    public int Encumbrance { get; init; }
    public int SkillPoints { get; init; }
    public int AttributePoints { get; init; }
    public int Concentration { get; init; }
    public int MaxConcentration { get; init; }
    public IReadOnlyList<IdValue> Bonuses { get; init; } = [];
    public float AttackRange { get; init; } = 2f;
    public int FastTravelItemLimit { get; init; }
    public float MaxSpeed { get; init; } = 5f;
    public float Scale { get; init; } = 1f;
    public IReadOnlyList<IdValue> CompiledAbilities { get; init; } = [];
    public IReadOnlyList<IdValue> Avoidances { get; init; } = [];
    public IReadOnlyList<IdValue> CompiledSkills { get; init; } = [];
    public int SoloBankItemLimit { get; init; }

    public void Write(NetBuffer b)
    {
        b.Write((byte)1); // full form
        foreach (var v in new[]
                 {
                     Health, MaxHealth, Energy, MaxEnergy, Attack, Defence, AttackSpeed, WeaponDamage, ArmourValue,
                     Vitality, Strength, Focus, Dexterity, Encumbrance, SkillPoints, AttributePoints, Concentration, MaxConcentration,
                 })
            b.WriteVariableInt32(v);
        Wire.WriteIdValues(b, Bonuses);
        b.Write(AttackRange);
        b.WriteVariableInt32(FastTravelItemLimit);
        b.Write(MaxSpeed);
        b.Write(Scale);
        Wire.WriteIdValues(b, CompiledAbilities);
        Wire.WriteIdValues(b, Avoidances);
        Wire.WriteIdValues(b, CompiledSkills);
        b.WriteVariableInt32(SoloBankItemLimit);
    }

    public static CharacterStats Read(NetBuffer b)
    {
        if (b.ReadByte() != 1) throw new NotSupportedException("delta stats form is not used in StartGameReply");
        var v = new int[18];
        for (var i = 0; i < v.Length; i++) v[i] = b.ReadVariableInt32();
        return new CharacterStats
        {
            Health = v[0], MaxHealth = v[1], Energy = v[2], MaxEnergy = v[3], Attack = v[4], Defence = v[5], AttackSpeed = v[6],
            WeaponDamage = v[7], ArmourValue = v[8], Vitality = v[9], Strength = v[10], Focus = v[11], Dexterity = v[12],
            Encumbrance = v[13], SkillPoints = v[14], AttributePoints = v[15], Concentration = v[16], MaxConcentration = v[17],
            Bonuses = Wire.ReadIdValues(b),
            AttackRange = b.ReadFloat(),
            FastTravelItemLimit = b.ReadVariableInt32(),
            MaxSpeed = b.ReadFloat(),
            Scale = b.ReadFloat(),
            CompiledAbilities = Wire.ReadIdValues(b),
            Avoidances = Wire.ReadIdValues(b),
            CompiledSkills = Wire.ReadIdValues(b),
            SoloBankItemLimit = b.ReadVariableInt32(),
        };
    }
}

/// <summary>HUD slot sets (NetworkManager.cs:3979): an ignored varint, then sets of (iconType, serverId) slots.</summary>
public sealed record HudSet(int SetId, IReadOnlyList<IdValue> Slots);

/// <summary>
/// StartGameReply (13002), server → client (processStartGameReply, NetworkManager.cs:2905-2968; FullCharacterData 3199-3283).
/// Constraints: <see cref="Character"/>'s zone must exist in the client balance Zones, skill and quest ids must exist, and
/// unknown equipment templates make the client send ClientErrorMessage.
/// </summary>
public sealed record StartGameReply
{
    public CommandStatus Status { get; init; } = CommandStatus.Accepted;
    public string RejectLog { get; init; } = "";
    public string RejectReason { get; init; } = "";
    public int Platinum { get; init; }
    public int MaxAccountLevel { get; init; } = 1;
    public required CharacterSummary Character { get; init; }
    public float Experience { get; init; }
    public int PvpExperience { get; init; }
    public float FishingExperience { get; init; }
    public IReadOnlyList<IdValue> Abilities { get; init; } = [];
    public IReadOnlyList<SkillEntry> Skills { get; init; } = [];
    public CharacterStats Stats { get; init; } = new();
    public int Level { get; init; } = 1;
    public int FishingLevel { get; init; } = 1;
    public int CookingLevel { get; init; } = 1;
    public float CookingExperience { get; init; }
    public int Coins { get; init; }
    public IReadOnlyList<ItemSlot> Equipment { get; init; } = Enumerable.Repeat(ItemSlot.Empty, CharacterSummary.EquipmentSlots).ToArray();
    public IReadOnlyList<ItemSlot> Inventory { get; init; } = [];
    public IReadOnlyList<HudSet> HudSets { get; init; } = [];
    public int BaseVitality { get; init; } = 10;
    public int BaseStrength { get; init; } = 10;
    public int BaseFocus { get; init; } = 10;
    public int BaseDexterity { get; init; } = 10;
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float Direction { get; init; }
    public IReadOnlyList<int> CompletedQuests { get; init; } = [];
    public IReadOnlyList<int> Teleports { get; init; } = [];
    public IReadOnlyList<int> KnownZones { get; init; } = [];

    public void Write(NetBuffer b)
    {
        b.Write((byte)Status);
        if (Status != CommandStatus.Accepted)
        {
            b.Write(RejectLog);
            b.Write(RejectReason);
            return;
        }
        b.WriteVariableInt32(Platinum);
        b.WriteVariableInt32(MaxAccountLevel);

        // FullCharacterData
        Character.Write(b);
        b.Write(Experience);
        b.WriteVariableInt32(PvpExperience);
        b.Write(FishingExperience);
        Wire.WriteIdValues(b, Abilities);
        b.WriteVariableInt32(Skills.Count);
        foreach (var s in Skills)
        {
            b.WriteVariableInt32(s.SkillId);
            b.WriteVariableInt32(s.Level);
            b.WriteVariableInt32(s.MaxLevel);
            b.Write(s.TimeRemaining);
        }
        Stats.Write(b);
        b.WriteVariableInt32(Level);
        b.WriteVariableInt32(FishingLevel);
        b.WriteVariableInt32(CookingLevel);
        b.Write(CookingExperience);
        b.WriteVariableInt32(Coins);
        if (Equipment.Count != CharacterSummary.EquipmentSlots) throw new InvalidOperationException("equipment must have 25 slots");
        foreach (var slot in Equipment) slot.Write(b);
        b.WriteVariableInt32(Inventory.Count);
        foreach (var slot in Inventory) slot.Write(b);
        b.WriteVariableInt32(0); // ignored
        b.WriteVariableInt32(HudSets.Count);
        foreach (var set in HudSets)
        {
            b.WriteVariableInt32(set.SetId);
            b.WriteVariableInt32(set.Slots.Count);
            foreach (var slot in set.Slots)
            {
                b.WriteVariableInt32(slot.Id);
                b.WriteVariableInt32(slot.Value);
            }
        }
        b.WriteVariableInt32(BaseVitality);
        b.WriteVariableInt32(BaseStrength);
        b.WriteVariableInt32(BaseFocus);
        b.WriteVariableInt32(BaseDexterity);

        b.Write(X);
        b.Write(Y);
        b.Write(Z);
        b.Write(Direction);

        // FullQuestList: completed ids, then active quests (none yet)
        b.WriteVariableInt32(CompletedQuests.Count);
        foreach (var q in CompletedQuests) b.WriteVariableInt32(q);
        b.WriteVariableInt32(0);

        b.WriteVariableInt32(Teleports.Count);
        foreach (var t in Teleports) b.WriteVariableInt32(t);
        b.WriteVariableInt32(-1); // clan block: no clan
        b.WriteVariableInt32(KnownZones.Count);
        foreach (var z in KnownZones) b.WriteVariableInt32(z);
        b.WriteVariableInt32(0); // unused
    }

    /// <summary>Mirror of the client's reader, for the fake client and tests. Supports the subset the server writes (no active quests, no clan).</summary>
    public static StartGameReply Read(NetBuffer b)
    {
        var status = (CommandStatus)b.ReadByte();
        if (status != CommandStatus.Accepted)
            return new StartGameReply { Status = status, RejectLog = b.ReadString(), RejectReason = b.ReadString(), Character = null! };

        var platinum = b.ReadVariableInt32();
        var maxAccountLevel = b.ReadVariableInt32();
        var character = CharacterSummary.Read(b);
        var xp = b.ReadFloat();
        var pvpXp = b.ReadVariableInt32();
        var fishXp = b.ReadFloat();
        var abilities = Wire.ReadIdValues(b);
        var skills = new List<SkillEntry>();
        for (int i = 0, n = b.ReadVariableInt32(); i < n; i++)
            skills.Add(new SkillEntry(b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadFloat()));
        var stats = CharacterStats.Read(b);
        var level = b.ReadVariableInt32();
        var fishLevel = b.ReadVariableInt32();
        var cookLevel = b.ReadVariableInt32();
        var cookXp = b.ReadFloat();
        var coins = b.ReadVariableInt32();
        var equipment = new List<ItemSlot>();
        for (var i = 0; i < CharacterSummary.EquipmentSlots; i++) equipment.Add(ItemSlot.Read(b));
        var inventory = new List<ItemSlot>();
        for (int i = 0, n = b.ReadVariableInt32(); i < n; i++) inventory.Add(ItemSlot.Read(b));
        b.ReadVariableInt32();
        var hud = new List<HudSet>();
        for (int i = 0, n = b.ReadVariableInt32(); i < n; i++)
        {
            var setId = b.ReadVariableInt32();
            var slots = new List<IdValue>();
            for (int j = 0, cap = b.ReadVariableInt32(); j < cap; j++) slots.Add(new IdValue(b.ReadVariableInt32(), b.ReadVariableInt32()));
            hud.Add(new HudSet(setId, slots));
        }
        int bv = b.ReadVariableInt32(), bs = b.ReadVariableInt32(), bf = b.ReadVariableInt32(), bd = b.ReadVariableInt32();
        float x = b.ReadFloat(), y = b.ReadFloat(), z = b.ReadFloat(), dir = b.ReadFloat();
        var completed = Wire.ReadInts(b);
        if (b.ReadVariableInt32() != 0) throw new NotSupportedException("active quests not supported by the mirror reader");
        var teleports = Wire.ReadInts(b);
        if (b.ReadVariableInt32() >= 0) throw new NotSupportedException("clan block not supported by the mirror reader");
        var zones = Wire.ReadInts(b);
        b.ReadVariableInt32();
        return new StartGameReply
        {
            Status = status, Platinum = platinum, MaxAccountLevel = maxAccountLevel, Character = character, Experience = xp,
            PvpExperience = pvpXp, FishingExperience = fishXp, Abilities = abilities, Skills = skills, Stats = stats, Level = level,
            FishingLevel = fishLevel, CookingLevel = cookLevel, CookingExperience = cookXp, Coins = coins, Equipment = equipment,
            Inventory = inventory, HudSets = hud, BaseVitality = bv, BaseStrength = bs, BaseFocus = bf, BaseDexterity = bd,
            X = x, Y = y, Z = z, Direction = dir, CompletedQuests = completed, Teleports = teleports, KnownZones = zones,
        };
    }
}

/// <summary>Another player as seen by the client (CBasicPC.cs:121-194): ShortCharacterData without "dismounted", plus position.</summary>
public sealed record OtherPlayer(CharacterSummary Character, float X, float Y, float Z, float Direction)
{
    public void Write(NetBuffer b)
    {
        var c = Character;
        b.WriteVariableUInt32(c.CharacterId);
        b.WriteVariableUInt32(c.ServerCharacterId);
        b.WriteVariableInt32(c.WorldId);
        b.Write(c.Name);
        b.WriteVariableUInt32(c.GmFlag);
        b.Write(c.Clan);
        c.Appearance.Write(b);
        foreach (var t in c.Equipment) b.WriteVariableInt32(t);
        b.WriteVariableInt32(c.Zone);
        b.WriteVariableInt32(c.Level);
        b.WriteVariableInt32(c.FishingLevel);
        b.WriteVariableInt32(c.CookingLevel);
        b.WriteVariableInt32(c.Class);
        b.Write(c.AttackRange);
        b.WriteVariableInt32(c.AttackSpeed);
        b.WriteVariableInt32(c.PvpLevel);
        b.WriteVariableInt32(c.PvpRating);
        b.Write(X);
        b.Write(Y);
        b.Write(Z);
        b.Write(Direction);
    }
}

/// <summary>Friend entry (NetworkManager.cs:3062): name, id, online, zone, level, class.</summary>
public sealed record FriendEntry(string Name, int Id, bool Online, int Zone, int Level, int Class);

/// <summary>EnteringNewZoneUpdate (13005), server → client (NetworkManager.cs:2259-2307).</summary>
public sealed record EnteringNewZoneUpdate(float X, float Y, float Z)
{
    public IReadOnlyList<int> AvailableQuests { get; init; } = [];
    public IReadOnlyList<int> NotYetAvailableQuests { get; init; } = [];
    public IReadOnlyList<OtherPlayer> Players { get; init; } = [];
    public IReadOnlyList<FriendEntry> Friends { get; init; } = [];

    public void Write(NetBuffer b)
    {
        b.Write(X);
        b.Write(Y);
        b.Write(Z);
        b.Write(0f); // ignored by the client
        Wire.WriteInts(b, AvailableQuests);
        Wire.WriteInts(b, NotYetAvailableQuests);
        b.WriteVariableUInt32((uint)Players.Count);
        foreach (var p in Players) p.Write(b);
        b.WriteVariableInt32(Friends.Count);
        foreach (var f in Friends)
        {
            b.Write(f.Name);
            b.WriteVariableInt32(f.Id);
            b.Write((byte)(f.Online ? 1 : 0));
            b.WriteVariableInt32(f.Zone);
            b.WriteVariableInt32(f.Level);
            b.WriteVariableInt32(f.Class);
        }
    }
}

/// <summary>Helpers for the repeated list shapes.</summary>
public static class Wire
{
    public static void WriteIdValues(NetBuffer b, IReadOnlyList<IdValue> list)
    {
        b.WriteVariableInt32(list.Count);
        foreach (var e in list)
        {
            b.WriteVariableInt32(e.Id);
            b.WriteVariableInt32(e.Value);
        }
    }

    public static List<IdValue> ReadIdValues(NetBuffer b)
    {
        var list = new List<IdValue>();
        for (int i = 0, n = b.ReadVariableInt32(); i < n; i++) list.Add(new IdValue(b.ReadVariableInt32(), b.ReadVariableInt32()));
        return list;
    }

    public static void WriteInts(NetBuffer b, IReadOnlyList<int> list)
    {
        b.WriteVariableInt32(list.Count);
        foreach (var v in list) b.WriteVariableInt32(v);
    }

    public static List<int> ReadInts(NetBuffer b)
    {
        var list = new List<int>();
        for (int i = 0, n = b.ReadVariableInt32(); i < n; i++) list.Add(b.ReadVariableInt32());
        return list;
    }
}
