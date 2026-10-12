using Lidgren.Network;

namespace CH.Protocol.Messages;

/// <summary>Appearance block (ReadAppearanceData, NetworkManager.cs:4084-4097).</summary>
public sealed record Appearance(
    int Race,
    int Face,
    int Skin,
    int SkinColour,
    int Hair,
    int HairColour,
    int FaceAccessory,
    int FaceAccessoryColour,
    float Scale,
    byte Gender,
    bool ShowHeadGear = true,
    bool ShowFashion = true)
{
    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32(Race);
        b.WriteVariableInt32(Face);
        b.WriteVariableInt32(Skin);
        b.WriteVariableInt32(SkinColour);
        b.WriteVariableInt32(Hair);
        b.WriteVariableInt32(HairColour);
        b.WriteVariableInt32(FaceAccessory);
        b.WriteVariableInt32(FaceAccessoryColour);
        b.Write(Scale);
        b.Write(Gender);
        b.Write((byte)(ShowHeadGear ? 1 : 0));
        b.Write((byte)(ShowFashion ? 1 : 0));
    }

    public static Appearance Read(NetBuffer b) => new(
        b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32(),
        b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32(),
        b.ReadFloat(), b.ReadByte(), b.ReadByte() == 1, b.ReadByte() == 1);
}

/// <summary>ShortCharacterData (ReadShortCharacterData, NetworkManager.cs:4050-4082).</summary>
public sealed record CharacterSummary(
    uint CharacterId,
    uint ServerCharacterId,
    int WorldId,
    string Name,
    uint GmFlag,
    string Clan,
    Appearance Appearance,
    int[] Equipment,
    int Zone,
    int Level,
    int FishingLevel,
    int CookingLevel,
    int Class,
    float AttackRange,
    int AttackSpeed,
    int PvpLevel,
    int PvpRating,
    bool Dismounted)
{
    /// <summary>Equipment slots in the wire format. The client ignores slot values of -1 (CInventory.cs:433-442).</summary>
    public const int EquipmentSlots = 25;

    public const int EmptySlot = -1;

    public static int[] NoEquipment() => Enumerable.Repeat(EmptySlot, EquipmentSlots).ToArray();

    public void Write(NetBuffer b)
    {
        if (Equipment.Length != EquipmentSlots)
            throw new ArgumentException($"equipment must have {EquipmentSlots} slots");
        b.WriteVariableUInt32(CharacterId);
        b.WriteVariableUInt32(ServerCharacterId);
        b.WriteVariableInt32(WorldId);
        b.Write(Name);
        b.WriteVariableUInt32(GmFlag);
        b.Write(Clan);
        Appearance.Write(b);
        foreach (var templateId in Equipment) b.WriteVariableInt32(templateId);
        b.WriteVariableInt32(Zone);
        b.WriteVariableInt32(Level);
        b.WriteVariableInt32(FishingLevel);
        b.WriteVariableInt32(CookingLevel);
        b.WriteVariableInt32(Class);
        b.Write(AttackRange);
        b.WriteVariableInt32(AttackSpeed);
        b.WriteVariableInt32(PvpLevel);
        b.WriteVariableInt32(PvpRating);
        b.WriteVariableInt32(Dismounted ? 1 : 0);
    }

    public static CharacterSummary Read(NetBuffer b)
    {
        var id = b.ReadVariableUInt32();
        var serverId = b.ReadVariableUInt32();
        var world = b.ReadVariableInt32();
        var name = b.ReadString();
        var gm = b.ReadVariableUInt32();
        var clan = b.ReadString();
        var appearance = Appearance.Read(b);
        var equipment = new int[EquipmentSlots];
        for (var i = 0; i < EquipmentSlots; i++) equipment[i] = b.ReadVariableInt32();
        return new CharacterSummary(id, serverId, world, name, gm, clan, appearance, equipment,
            Zone: b.ReadVariableInt32(), Level: b.ReadVariableInt32(), FishingLevel: b.ReadVariableInt32(),
            CookingLevel: b.ReadVariableInt32(), Class: b.ReadVariableInt32(), AttackRange: b.ReadFloat(),
            AttackSpeed: b.ReadVariableInt32(), PvpLevel: b.ReadVariableInt32(), PvpRating: b.ReadVariableInt32(),
            Dismounted: b.ReadVariableInt32() != 0);
    }
}

/// <summary>
/// RequestCharacterListReply (12002), server → client (processCharacterListReply, NetworkManager.cs:4133-4254).
/// Accepted carries <c>varuint maxSlots, varuint count, varint lastSelectedCharId, count × ShortCharacterData</c>.
/// </summary>
public sealed record CharacterListReply(CommandStatus Status, uint MaxSlots, int LastSelectedCharacterId, IReadOnlyList<CharacterSummary> Characters, string Reason = "")
{
    public void Write(NetBuffer b)
    {
        b.Write((byte)Status);
        if (Status != CommandStatus.Accepted)
        {
            b.Write(Reason);
            return;
        }
        b.WriteVariableUInt32(MaxSlots);
        b.WriteVariableUInt32((uint)Characters.Count);
        b.WriteVariableInt32(LastSelectedCharacterId);
        foreach (var c in Characters) c.Write(b);
    }

    public static CharacterListReply Read(NetBuffer b)
    {
        var status = (CommandStatus)b.ReadByte();
        if (status != CommandStatus.Accepted)
            return new CharacterListReply(status, 0, -1, [], b.ReadString());
        var maxSlots = b.ReadVariableUInt32();
        var count = b.ReadVariableUInt32();
        var last = b.ReadVariableInt32();
        var list = new List<CharacterSummary>();
        for (var i = 0; i < count; i++) list.Add(CharacterSummary.Read(b));
        return new CharacterListReply(status, maxSlots, last, list);
    }
}

/// <summary>CreateCharacter (12005), client → server, signed. Sender at NetworkManager.cs:4415-4433 (unusual field order).</summary>
public sealed record CreateCharacterRequest(string Name, Appearance Appearance, int Class)
{
    public void Write(NetBuffer b)
    {
        var a = Appearance;
        b.Write(a.Gender);
        b.WriteVariableInt32(a.FaceAccessoryColour);
        b.WriteVariableInt32(a.Hair);
        b.Write(a.Scale);
        b.WriteVariableInt32(a.Face);
        b.Write(Name);
        b.WriteVariableInt32(a.Race);
        b.WriteVariableInt32(a.Skin);
        b.WriteVariableInt32(a.SkinColour);
        b.WriteVariableInt32(a.HairColour);
        b.WriteVariableInt32(a.FaceAccessory);
        b.WriteVariableInt32(Class);
    }

    public static CreateCharacterRequest Read(NetBuffer b)
    {
        var gender = b.ReadByte();
        var faceAccCol = b.ReadVariableInt32();
        var hair = b.ReadVariableInt32();
        var scale = b.ReadFloat();
        var face = b.ReadVariableInt32();
        var name = b.ReadString();
        var race = b.ReadVariableInt32();
        var skin = b.ReadVariableInt32();
        var skinCol = b.ReadVariableInt32();
        var hairCol = b.ReadVariableInt32();
        var faceAcc = b.ReadVariableInt32();
        var cls = b.ReadVariableInt32();
        return new CreateCharacterRequest(name, new Appearance(race, face, skin, skinCol, hair, hairCol, faceAcc, faceAccCol, scale, gender), cls);
    }
}

/// <summary>StartGame (13001), client → server, signed (sendStartGame, NetworkManager.cs:4787-4797).</summary>
public sealed record StartGameRequest(string CharacterName, uint CharacterId)
{
    public void Write(NetBuffer b)
    {
        b.Write(CharacterName);
        b.WriteVariableInt32(0);
        b.WriteVariableUInt32(CharacterId);
        b.WriteVariableInt32(0);
        b.Write("uuid");
    }

    public static StartGameRequest Read(NetBuffer b)
    {
        var name = b.ReadString();
        b.ReadVariableInt32();
        var id = b.ReadVariableUInt32();
        b.ReadVariableInt32();
        b.ReadString();
        return new StartGameRequest(name, id);
    }
}

/// <summary>PlayerMove (13003), client → server, signed, ReliableSequenced (CPlayableCharacter.cs:470-505).</summary>
public sealed record PlayerMove(double ServerTime, float X, float Y, float Z, float YAngle, float Speed)
{
    public void Write(NetBuffer b)
    {
        b.Write(ServerTime);
        b.Write(X);
        b.Write(Y);
        b.Write(Z);
        b.Write(YAngle);
        b.Write(Speed);
    }

    public static PlayerMove Read(NetBuffer b) => new(b.ReadDouble(), b.ReadFloat(), b.ReadFloat(), b.ReadFloat(), b.ReadFloat(), b.ReadFloat());
}
