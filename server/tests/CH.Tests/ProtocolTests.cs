using CH.Protocol;
using CH.Protocol.Messages;
using Lidgren.Network;

namespace CH.Tests;

public class ProtocolTests
{
    private static readonly NetPeer Peer = new(new NetPeerConfiguration(Envelope.AppIdentifier));

    private static NetIncomingMessage Reread(NetOutgoingMessage outgoing)
    {
        // Same bytes, read back through the incoming-message API the client uses.
        var bytes = outgoing.Data.AsSpan(0, outgoing.LengthBytes).ToArray();
        var incoming = (NetIncomingMessage)Activator.CreateInstance(typeof(NetIncomingMessage), nonPublic: true)!;
        incoming.Data = bytes;
        incoming.LengthBits = outgoing.LengthBits;
        return incoming;
    }

    private static string Hex(NetOutgoingMessage m) => Convert.ToHexString(m.Data, 0, m.LengthBytes);

    [Fact]
    public void Server_header_without_session_is_flag_zero_then_varuint_opcode()
    {
        // 12002 = 0x2EE2 → varuint E2 5D
        var m = Envelope.CreateServerMessage(Peer, Opcode.RequestCharacterListReply, 0);
        Assert.Equal("00E25D", Hex(m));
    }

    [Fact]
    public void Server_header_with_session_is_flag_one_then_little_endian_uint32()
    {
        var m = Envelope.CreateServerMessage(Peer, Opcode.RequestCharacterListReply, 0x01020304);
        Assert.Equal("0104030201E25D", Hex(m));
    }

    [Fact]
    public void Client_header_after_login_skips_signature_and_reads_session()
    {
        var m = Envelope.CreateClientMessage(Peer, Opcode.PlayerMove, 77);
        m.Write(1.5f);
        Assert.Equal(2 + Envelope.SignatureBytes + 4 + 4, m.LengthBytes); // varuint 13003 is two bytes
        var incoming = Reread(m);
        var header = Envelope.ReadClientHeader(incoming, sessionEstablished: true);
        Assert.Equal(new ClientHeader(Opcode.PlayerMove, 77u), header);
        Assert.Equal(1.5f, incoming.ReadFloat());
    }

    [Theory]
    [InlineData(Opcode.VersionCheck)]
    [InlineData(Opcode.Login)]
    [InlineData(Opcode.LoginAdvanced)]
    public void Unsigned_opcodes_never_carry_the_signature(Opcode opcode)
    {
        var m = Envelope.CreateClientMessage(Peer, opcode, 77);
        var header = Envelope.ReadClientHeader(Reread(m), sessionEstablished: true);
        Assert.Null(header.SessionId);
        Assert.Equal(m.LengthBytes, opcode == Opcode.VersionCheck ? 1 : 2);
    }

    [Fact]
    public void VersionCheckReply_bool_is_one_bit_and_following_fields_are_unaligned()
    {
        var m = Peer.CreateMessage();
        new VersionCheckReply(true, 1, 101).Write(m);
        Assert.Equal(1 + 8 + 16, m.LengthBits); // 1 bit + varint(zigzag 1 = 2) + varint(zigzag 101 = 202, two bytes)
        Assert.Equal(new VersionCheckReply(true, 1, 101), VersionCheckReply.Read(Reread(m)));
    }

    [Fact]
    public void Login_round_trips_and_ToString_hides_credentials()
    {
        var login = new LoginRequest(true, "alice", "SECRETHASH", "a,b,c,d", 143, "K", "dev-1", 2, "push", "EN_UK", "tok", 10032, "GB", "6.15.0", 12345678901234567);
        var m = Peer.CreateMessage();
        login.Write(m);
        Assert.Equal(login, LoginRequest.Read(Reread(m), advanced: true));
        var text = login.ToString();
        Assert.Contains("alice", text);
        Assert.DoesNotContain("SECRETHASH", text);
        Assert.DoesNotContain("dev-1", text);
        Assert.DoesNotContain("12345678901234567", text);
    }

    [Fact]
    public void CreateCharacter_uses_the_clients_field_order()
    {
        var req = new CreateCharacterRequest("Brigid", new Appearance(2, 3, 4, 5, 6, 7, 8, 9, 1.05f, 1), 3);
        var m = Peer.CreateMessage();
        req.Write(m);
        var r = Reread(m);
        Assert.Equal(1, r.ReadByte());             // gender first
        Assert.Equal(9, r.ReadVariableInt32());    // face accessory colour
        Assert.Equal(6, r.ReadVariableInt32());    // hair
        Assert.Equal(1.05f, r.ReadFloat());        // scale
        Assert.Equal(3, r.ReadVariableInt32());    // face
        Assert.Equal("Brigid", r.ReadString());
        Assert.Equal(req.Appearance with { ShowFashion = true, ShowHeadGear = true }, CreateCharacterRequest.Read(Reread(m)).Appearance);
    }

    private static CharacterSummary Summary(string name = "Brigid") => new(
        7, 7, 2, name, 0, "", new Appearance(1, 1, 1, 1, 1, 1, 0, 0, 1f, 0), CharacterSummary.NoEquipment(),
        2, 1, 1, 1, 1, 2f, 100, 0, 0, false);

    [Fact]
    public void CharacterListReply_round_trips()
    {
        var reply = new CharacterListReply(CommandStatus.Accepted, 6, 7, [Summary("Aine"), Summary("Brigid")]);
        var m = Peer.CreateMessage();
        reply.Write(m);
        var back = CharacterListReply.Read(Reread(m));
        Assert.Equal(2, back.Characters.Count);
        Assert.Equal("Brigid", back.Characters[1].Name);
        Assert.Equal(CharacterSummary.NoEquipment(), back.Characters[1].Equipment);
        Assert.Equal(m.LengthBits, Reread(m).LengthBits);
    }

    [Fact]
    public void StartGameReply_round_trips_every_section_and_consumes_all_bits()
    {
        var reply = new StartGameReply
        {
            Character = Summary(),
            Abilities = [new IdValue(3, 1)],
            Skills = [new SkillEntry(10, 1, 5, 0f)],
            Stats = new CharacterStats { Bonuses = [new IdValue(1, 2)], CompiledSkills = [new IdValue(10, 1)] },
            Coins = 50,
            Inventory = [new ItemSlot(1, 100, 3, true, 0, false)],
            HudSets = [new HudSet(0, [new IdValue(1, 10)])],
            X = -109.51f, Y = -3.7f, Z = -16.86f, Direction = 1.25f,
            CompletedQuests = [4],
            Teleports = [2003],
            KnownZones = [2],
        };
        var m = Peer.CreateMessage();
        reply.Write(m);
        var incoming = Reread(m);
        var back = StartGameReply.Read(incoming);
        Assert.Equal(incoming.LengthBits, incoming.Position);
        Assert.Equal(reply.Character, back.Character with { Equipment = reply.Character.Equipment });
        Assert.Equal((reply.X, reply.Y, reply.Z, reply.Direction), (back.X, back.Y, back.Z, back.Direction));
        Assert.Equal(reply.Skills, back.Skills);
        Assert.Equal(reply.Inventory, back.Inventory);
        Assert.Equal(25, back.Equipment.Count);
        Assert.Equal(reply.KnownZones, back.KnownZones);
    }
}
