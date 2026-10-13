using System.Net;
using System.Security.Cryptography;
using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Lidgren UDP game server speaking the client's protocol (docs/protocol-notes.md). All message handling runs on one
/// pump thread, so session state needs no locking.
/// </summary>
public sealed partial class GameServer : IDisposable
{
    private readonly ServerOptions _options;
    private readonly GameStore _store;
    private readonly FrameLog _frames;
    private readonly Balance _balance;
    private readonly IReadOnlyDictionary<int, GeneratedCamp[]> _generatedCamps;
    private readonly ZoneTravel _travel;
    private readonly LootTable _loot;
    private readonly QuestBook _quests;
    private readonly ShopCatalog _shops;
    private readonly TokenVendorCatalog _tokenVendors;
    private readonly PassiveTree _tree;
    private readonly IReadOnlyList<ShopStock> _itemShop;
    private readonly FactionRules _factionRules;
    private readonly Dictionary<int, QuestInfo> _bounties;
    private readonly ILogger _log;
    private readonly NetServer _server;
    private readonly Dictionary<NetConnection, Session> _sessions = new();
    private Thread? _pump;
    private volatile bool _stopping;

    private const int MaxFailedLogins = 5;

    public GameServer(ServerOptions options, GameStore store, FrameLog frames, Balance balance, ILogger log)
    {
        _options = options;
        _store = store;
        _frames = frames;
        _balance = balance;
        var started = System.Diagnostics.Stopwatch.StartNew();
        _chestCatalog = ChestCatalog.Build(balance);
        if (balance.IsLoaded) log.LogInformation("Chest catalogue: {Count} chests in {Ms} ms", _chestCatalog.Chests.Count, started.ElapsedMilliseconds);
        _generatedCamps = ZonePopulation.LoadGenerated(options.PopulationPath);
        _travel = ZoneTravel.Load(options.ZonePointsPath, balance);
        _loot = new LootTable(balance);
        _quests = QuestBook.Load(options.WorldPath);
        _shops = new ShopCatalog(balance, ShopCatalog.Load(options.WorldPath), ShopCatalog.LoadExtras(options.ShopExtrasPath));
        _tokenVendors = TokenVendorCatalog.Load(options.WorldPath);
        _tree = PassiveTree.Load(options.PassiveTreePath);
        _itemShop = LoadItemShop(options.ItemShopPath);
        _factionRules = FactionRules.Load(options.FactionsPath);
        _bounties = BuildBounties();
        if (_tree.IsLoaded) log.LogInformation("Passive tree: {Nodes} nodes", _tree.Nodes.Count);
        if (_quests.Quests.Count > 0) log.LogInformation("Quests: {Quests}, NPCs: {Npcs}", _quests.Quests.Count, _quests.Npcs.Count);
        if (_bounties.Count > 0)
            log.LogInformation("Bounties: {Normal} on the boards, {Ancient} Ancient", _bounties.Values.Count(q => q.Archetype == "BountyBoard"), _bounties.Values.Count(q => q.Archetype == "AncientBountyBoard"));
        if (_travel.ExitCount > 0) log.LogInformation("Zone exits: {Count}", _travel.ExitCount);
        if (_generatedCamps.Count > 0)
            log.LogInformation("Generated monster camps for {Zones} zones ({Mobs} monsters)", _generatedCamps.Count, _generatedCamps.Values.Sum(z => z.Sum(c => c.Count)));
        _log = log;
        var config = new NetPeerConfiguration(Envelope.AppIdentifier)
        {
            LocalAddress = options.BindAddress,
            Port = options.GamePort,
            AcceptIncomingConnections = true,
            MaximumConnections = 64,
            ConnectionTimeout = 90f, // the client's fork uses 90 s
        };
        config.EnableMessageType(NetIncomingMessageType.DiscoveryRequest);
        config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);
        _server = new NetServer(config);
    }

    public int Port => _server.Port;

    public int ConnectionCount => _sessions.Count;

    public void Start()
    {
        _server.Start();
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "game-server" };
        _pump.Start();
        _log.LogInformation("Game server (Lidgren \"{App}\") listening on udp {Address}:{Port}", Envelope.AppIdentifier, _options.BindAddress, Port);
        if (_options.TailscaleOnly)
            _log.LogInformation("Tailscale mode: friends join at Tailscale address {Host}; other networks are refused", _options.PublicHost);
    }

    private void PumpLoop()
    {
        while (!_stopping)
        {
            _server.MessageReceivedEvent.WaitOne(20);
            try
            {
                MaybeTick();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "World tick failed");
            }
            NetIncomingMessage? msg;
            while ((msg = _server.ReadMessage()) is not null)
            {
                try
                {
                    Handle(msg);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Error handling {Type} from {Endpoint}", msg.MessageType, msg.SenderEndPoint);
                }
                finally
                {
                    _server.Recycle(msg);
                }
            }
        }
    }

    private void Handle(NetIncomingMessage msg)
    {
        switch (msg.MessageType)
        {
            case NetIncomingMessageType.ConnectionApproval:
                if (_options.IsAllowedClient(msg.SenderEndPoint?.Address)) msg.SenderConnection?.Approve();
                else
                {
                    _log.LogWarning("Refused connection from {Endpoint}", msg.SenderEndPoint);
                    msg.SenderConnection?.Deny("Not allowed on this server.");
                }
                break;

            case NetIncomingMessageType.DiscoveryRequest:
                if (!_options.IsAllowedClient(msg.SenderEndPoint?.Address)) break;
                var response = _server.CreateMessage();
                Discovery.WriteResponse(response);
                _server.SendDiscoveryResponse(response, msg.SenderEndPoint);
                break;

            case NetIncomingMessageType.StatusChanged:
                var status = (NetConnectionStatus)msg.ReadByte();
                if (status == NetConnectionStatus.Connected)
                {
                    _sessions[msg.SenderConnection] = new Session(msg.SenderConnection);
                    _log.LogInformation("Connected {Endpoint}", msg.SenderEndPoint);
                }
                else if (status == NetConnectionStatus.Disconnected)
                {
                    if (_sessions.TryGetValue(msg.SenderConnection, out var leaving))
                    {
                        if (leaving.InZone) LeaveZone(leaving);
                        LeaveParty(leaving);
                    }
                    _sessions.Remove(msg.SenderConnection);
                    if (leaving is not null) SharePresence(leaving); // now shown offline
                    _log.LogInformation("Disconnected {Endpoint}: {Reason}", msg.SenderEndPoint, msg.ReadString());
                }
                break;

            case NetIncomingMessageType.Data:
                if (_sessions.TryGetValue(msg.SenderConnection, out var session))
                    HandleData(session, msg);
                break;

            case NetIncomingMessageType.WarningMessage:
            case NetIncomingMessageType.ErrorMessage:
                _log.LogWarning("Lidgren: {Text}", msg.ReadString());
                break;
        }
    }

    private void HandleData(Session s, NetIncomingMessage msg)
    {
        var raw = msg.Data.AsSpan(0, msg.LengthBytes).ToArray();
        var header = Envelope.ReadClientHeader(msg, s.SessionId != 0);
        _frames.Inbound(s.Connection.RemoteUniqueIdentifier, header.Opcode, raw);
        if (header.SessionId is { } sid && sid != s.SessionId)
        {
            _log.LogWarning("Session id mismatch on {Opcode}: got {Got}, expected {Expected}", header.Opcode, sid, s.SessionId);
            return;
        }

        switch (header.Opcode)
        {
            case Opcode.VersionCheck:
                var check = VersionCheck.Read(msg);
                // OK means "your balance is current": the client keeps its bundled balance and never downloads one.
                Send(s, Opcode.VersionCheckReply, new VersionCheckReply(true, _options.ProtocolVersion, check.BalanceVersion).Write);
                break;

            case Opcode.Login:
            case Opcode.LoginAdvanced:
                OnLogin(s, LoginRequest.Read(msg, header.Opcode == Opcode.LoginAdvanced));
                break;

            case Opcode.RequestCharacterList:
                if (RequireAccount(s)) SendCharacterList(s);
                break;

            case Opcode.CreateCharacter:
                if (RequireAccount(s)) OnCreateCharacter(s, CreateCharacterRequest.Read(msg));
                break;

            case Opcode.DeleteCharacter:
                if (RequireAccount(s))
                {
                    var deleteName = msg.ReadString();
                    OnDeleteCharacter(s, deleteName, msg.ReadVariableUInt32());
                }
                break;

            case Opcode.BarbershopMessage:
                if (InWorld(s)) OnBarbershop(s, msg);
                break;

            case Opcode.ClientFriendRequest or Opcode.ClientFriendReply or Opcode.RemoveFriend or Opcode.RequestSocialLists
                or Opcode.BlockCharacter or Opcode.UnblockCharacter:
                if (InWorld(s)) OnSocialMessage(s, header.Opcode, msg);
                break;

            case Opcode.ClanMessage:
                if (InWorld(s)) OnClanMessage(s, msg);
                break;

            case Opcode.ClanBankingMessage or Opcode.ClanBankingLogMessage or Opcode.ClanBankingBuySlotMessage:
                if (InWorld(s)) OnClanBank(s, header.Opcode, msg);
                break;

            case Opcode.MailMessage:
                if (InWorld(s)) OnMail(s, msg);
                break;

            case Opcode.ReqServerDynamicData:
                if (InWorld(s)) OnServerDynamicData(s, msg.ReadString());
                break;

            case Opcode.AuctionHouse:
                if (InWorld(s)) OnAuctionHouse(s, msg);
                break;

            case Opcode.BountyBoard:
                if (InWorld(s)) OnBountyBoard(s, msg);
                break;

            case Opcode.ItemShop:
                if (InWorld(s)) OnItemShop(s, msg);
                break;

            case Opcode.OpenOffersPage:
                break; // free-platinum, special-offer and offerwall pages: none on this server

            case Opcode.SettingsChangedMessage:
                break; // phone notification settings and push tokens: nothing to do on this server

            case Opcode.RequestTweak:
                Send(s, Opcode.TweakReply, new TweakReply([]).Write);
                break;

            case Opcode.StartGame:
                if (RequireAccount(s)) OnStartGame(s, StartGameRequest.Read(msg));
                break;

            case Opcode.FinishedZoning:
                OnFinishedZoning(s);
                break;

            case Opcode.PlayerMove:
                OnPlayerMove(s, PlayerMove.Read(msg));
                break;

            case Opcode.CharacterZoningRequest:
                if (InWorld(s)) OnZoningRequest(s, ZoningRequest.Read(msg));
                break;

            case Opcode.TeleportRequest:
                if (InWorld(s)) OnTeleportRequest(s, TeleportRequest.Read(msg));
                break;

            case Opcode.CharacterStuck:
                if (InWorld(s)) OnStuck(s);
                break;

            case Opcode.StartAttackingNPC:
                if (InWorld(s)) OnStartAttacking(s, StartAttacking.Read(msg));
                break;

            case Opcode.StopAttackingNPC:
                SetAttackTarget(s, null);
                SetPvpTarget(s, null);
                break;

            case Opcode.PVPMessage:
                if (InWorld(s)) OnPvpMessage(s, msg);
                break;

            case Opcode.TradeMessage:
                if (InWorld(s)) OnTradeMessage(s, msg);
                break;

            case Opcode.LearnAbility:
                if (InWorld(s))
                {
                    var abilityId = msg.ReadVariableInt32();
                    OnLearnAbility(s, abilityId, msg.ReadVariableInt32());
                }
                break;

            case Opcode.CastSkillOnPlayer:
                if (InWorld(s))
                {
                    msg.ReadVariableInt32(); // the NPC's server id
                    var castSkill = msg.ReadVariableInt32();
                    OnCastSkillOnPlayer(s, castSkill, msg.ReadVariableInt32());
                }
                break;

            case Opcode.TokenVendorMessage:
                if (InWorld(s)) OnTokenVendorMessage(s, msg);
                break;

            case Opcode.BankingMessage:
                if (InWorld(s)) OnBankMessage(s, msg);
                break;

            case Opcode.RequestShop:
            case Opcode.PurchaseItem:
            case Opcode.SellItem:
                if (InWorld(s)) OnShopMessage(s, header.Opcode, msg);
                break;

            case Opcode.QuestStart:
            case Opcode.QuestStageComplete:
            case Opcode.RequestAccurateQuestData:
            case Opcode.PreviewQuestRewards:
            case Opcode.DeleteQuest:
            case Opcode.TrackQuest:
            case Opcode.TrackAllQuests:
                if (InWorld(s)) OnQuestMessage(s, header.Opcode, msg);
                break;

            case Opcode.CharacterPlayingEmote:
                if (InWorld(s)) OnEmote(s, EmoteRequest.Read(msg));
                break;

            case Opcode.PartyMessage:
                if (InWorld(s)) OnPartyMessage(s, msg);
                break;

            case Opcode.UseSkillOnNPC:
                if (InWorld(s)) OnUseSkill(s, UseSkill.Read(msg));
                break;

            case Opcode.CancelSkill:
                if (InWorld(s)) OnCancelSkill(s, msg.ReadVariableUInt32());
                break;

            case Opcode.RessurectionRequest:
                if (InWorld(s)) OnResurrect(s, ResurrectionRequest.Read(msg));
                break;

            case Opcode.GeneralChat:
                if (InWorld(s)) OnChat(s, ChatMessage.ReadFromClient(msg));
                break;

            case Opcode.AttributesUpdate:
                if (InWorld(s)) OnAttributesUpdate(s, msg.ReadVariableUInt32(), msg.ReadVariableUInt32(), msg.ReadVariableUInt32(), msg.ReadVariableUInt32());
                break;

            case Opcode.BuySkill:
                if (InWorld(s)) OnBuySkill(s, msg.ReadVariableInt32(), msg.ReadVariableInt32());
                break;

            case Opcode.UpdateCharacterOptions:
                if (InWorld(s)) OnDisplayOptions(s, msg.ReadByte() == 1, msg.ReadByte() == 1);
                break;

            case Opcode.InGamePopup:
                if (InWorld(s)) OnPopupOption(s, msg.ReadVariableInt32(), msg.ReadVariableInt32(), msg.ReadVariableInt32());
                break;

            case Opcode.EquipItem:
                if (InWorld(s)) OnEquipItem(s, EquipItemRequest.Read(msg));
                break;

            case Opcode.DeleteItem:
                if (InWorld(s)) OnDeleteItem(s, DeleteItemRequest.Read(msg));
                break;

            case Opcode.UseItem:
                if (InWorld(s)) OnUseItem(s, UseItemRequest.Read(msg));
                break;

            case Opcode.SkillBarUpdate:
                if (InWorld(s)) OnSkillBarUpdate(s, SkillBarUpdate.Read(msg));
                break;

            case Opcode.InventoryFavouriteItem:
                if (InWorld(s)) OnFavourite(s, msg.ReadByte() == 1, msg.ReadVariableInt32());
                break;

            case Opcode.InventoryItemSwap:
                if (InWorld(s)) OnInventorySwap(s, msg.ReadByte());
                break;

            case Opcode.ClientErrorMessage:
                var report = ClientErrorMessage.Read(msg).Text;
                // In env Test the client forwards its own debug-level messages; the render ones are cosmetic noise.
                if (report.Contains("db RenderChild Problem", StringComparison.Ordinal)) _log.LogDebug("Client: {Text}", report);
                else _log.LogWarning("Client error report: {Text}", report);
                break;

            case Opcode.SigilsCount:
                // Reply: varint sigilsOfEnergy, varint sigilsOfHealth (NetworkManager.cs:1413-1426).
                if (InWorld(s)) SendSigils(s);
                break;

            case Opcode.RequestSpecialOffers:
                // Reply: varint offer count (NetworkManager.cs:1428). No offers on a private server.
                Send(s, Opcode.RequestSpecialOffers, b => b.WriteVariableInt32(0));
                break;

            case Opcode.Logout:
                _log.LogInformation("{Account} logging out", s.Account?.Username);
                break;

            // Client housekeeping and telemetry; nothing to answer.
            case Opcode.PlayerIsBusy:
            case Opcode.AvgFpsReport:
            case Opcode.GraphicSettingsUpdateMessage:
            case Opcode.InactivityMessage:
            // Sent by the client during login and zone load; replies are optional (docs/protocol-notes.md §4).
            case Opcode.DailyRewardMessage:
            case Opcode.MapEnterStatsMessage:
            case Opcode.ClientBackgrounded:
            case Opcode.ClientResumed:
                _log.LogDebug("Ignored {Opcode}", header.Opcode);
                break;

            default:
                _log.LogWarning("Unhandled opcode {Opcode} ({Value}), {Bytes} bytes", header.Opcode, (uint)header.Opcode, raw.Length);
                break;
        }
    }

    private void OnLogin(Session s, LoginRequest login)
    {
        _log.LogInformation("{Login}", login);
        if (string.IsNullOrWhiteSpace(login.Username))
        {
            Send(s, Opcode.LoginReply, new LoginReply(CommandStatus.Rejected, Reason: "Please create an account.").Write);
            return;
        }
        if (string.IsNullOrEmpty(login.PasswordHash))
        {
            Send(s, Opcode.LoginReply, new LoginReply(CommandStatus.Rejected, Reason: "This server needs a username and password login.").Write);
            return;
        }
        if (s.FailedLogins >= MaxFailedLogins)
        {
            s.Connection.Disconnect("Too many wrong passwords.");
            return;
        }
        var account = _store.GetOrCreateAccount(login.Username.Trim());
        // The client sends a hash of the password; the server stores only a salted PBKDF2 hash of that.
        switch (_store.CheckPassword(account.Id, login.PasswordHash))
        {
            case PasswordCheck.Wrong:
                s.FailedLogins++;
                _log.LogWarning("Wrong password for {Account} from {Endpoint}", account.Username, s.Connection.RemoteEndPoint);
                Send(s, Opcode.LoginReply, new LoginReply(CommandStatus.Rejected, Reason: "Wrong password for this account.").Write);
                return;
            case PasswordCheck.Claimed:
                _log.LogInformation("Account {Account} claimed with its first password", account.Username);
                break;
        }
        s.Account = account;
        // GM: accounts marked with .gm, and anyone playing on the server's own machine (the host).
        s.IsGm = account.IsGm || (_options.LocalGm && NetworkAccess.IsLocalMachine(s.Connection.RemoteEndPoint.Address));
        s.SessionId = NewSessionId();
        // The reply itself still goes out unsessioned: the client only learns the id from this message.
        Send(s, Opcode.LoginReply, new LoginReply(CommandStatus.Accepted, s.SessionId).Write, sessionId: 0);
        _log.LogInformation("Account {Account} logged in, session {Session}", s.Account.Username, s.SessionId);
    }

    private void SendCharacterList(Session s)
    {
        var characters = _store.ListCharacters(s.Account!.Id).Select(c => LoadSheet(c).Summary(_options.WorldId)).ToList();
        var last = characters.Count > 0 ? (int)characters[^1].CharacterId : -1;
        Send(s, Opcode.RequestCharacterListReply, new CharacterListReply(CommandStatus.Accepted, _options.MaxCharacterSlots, last, characters).Write);
    }

    private void OnCreateCharacter(Session s, CreateCharacterRequest req)
    {
        var name = req.Name.Trim();
        string? problem = name.Length is < 3 or > 16 ? "Names must be 3 to 16 characters."
            : !name.All(char.IsLetter) ? "Names may only contain letters."
            : _store.CharacterNameTaken(name) ? "That name is taken."
            : _store.ListCharacters(s.Account!.Id).Count >= _options.MaxCharacterSlots ? "No free character slots."
            : null;
        if (problem is not null)
        {
            Send(s, Opcode.CreateCharacterReply, new CreateCharacterReply(CommandStatus.Rejected, Reason: problem).Write);
            return;
        }

        var a = req.Appearance;
        var spawn = _options.DefaultSpawn;
        var created = _store.CreateCharacter(s.Account!.Id, new NewCharacter(name, a.Race, a.Face, a.Skin, a.SkinColour, a.Hair, a.HairColour,
            a.FaceAccessory, a.FaceAccessoryColour, a.Scale, a.Gender, req.Class, spawn.Zone, spawn.X, spawn.Y, spawn.Z, spawn.Direction));
        _log.LogInformation("Created character {Name} ({Id}) for {Account}", created.Name, created.Id, s.Account.Username);
        Send(s, Opcode.CreateCharacterReply, new CreateCharacterReply(CommandStatus.Accepted, (uint)created.Id, created.Name).Write);
    }

    private void OnStartGame(Session s, StartGameRequest req)
    {
        var character = _store.GetCharacter(req.CharacterId);
        if (character is null || character.AccountId != s.Account!.Id)
        {
            Send(s, Opcode.StartGameReply, new StartGameReply { Status = CommandStatus.Rejected, RejectLog = "unknown character", RejectReason = "Character not found.", Character = null! }.Write);
            return;
        }

        KickDuplicate(s, character.Id);
        if (s.InZone) LeaveZone(s); // a second StartGame on this connection
        LeaveParty(s);
        s.Character = character;
        s.Position = (character.Zone, character.X, character.Y, character.Z, character.Direction);
        GrantKitIfNeeded(character);
        PruneTree(character);
        var sheet = LoadSheet(character);
        if (string.IsNullOrEmpty(sheet.Progress.Hud)) _store.SaveHud(character.Id, CharacterSheet.SerializeHud(sheet.DefaultHud()));
        s.Sheet = sheet;
        s.MaxHealth = sheet.MaxHealth;
        s.MaxEnergy = sheet.MaxEnergy;
        s.Health = sheet.Health > 0 ? sheet.Health : sheet.MaxHealth;
        s.Energy = sheet.Energy;
        s.Dead = false;
        s.NextRegenAt = NetTime.Now + RegenIntervalSeconds;
        s.Teleports.Clear();
        s.Teleports.UnionWith(_store.ListTeleports(character.Id));
        (s.SigilsOfHealth, s.SigilsOfEnergy) = _store.GetSigils(character.Id);
        s.Blocked.Clear();
        s.Blocked.UnionWith(_store.ListBlocked(character.Id));
        LoadQuests(s);
        Send(s, Opcode.StartGameReply, sheet.StartGame(_options.WorldId, s.Teleports.Order().ToList(), CompletedQuestIds(s), ActiveQuests(s)).Write);
        // StartGameReply drops most of the stats block on the client (CCharacterDetails.CopyDetailsFrom), so resend it in full.
        s.Effects.Clear();
        RestorePetFeed(s);
        SendLiveStats(s);
        Send(s, Opcode.GameDataMessage, new GameData(MaxChatLength: 200, AttackDelaySeconds: 0.1f).Write);
        _log.LogInformation("{Name} entering zone {Zone} at ({X:F1}, {Y:F1}, {Z:F1})", character.Name, character.Zone, character.X, character.Y, character.Z);
    }

    private void OnFinishedZoning(Session s)
    {
        if (s.Character is null) return;
        EnterZone(s);
        // The client rebuilt the player on load: buffs, the pet's fed state and the skill toggles again.
        if (s.Effects.Count > 0) SendEffects(s);
        SendSkillToggles(s);
        DiscoverLeystones(s);
        QuestMove(s);
        // Sent once the client is ready (CGame.Ready drops them before): reputation, the item shop's no-cash offers,
        // social lists and the clan; friends and clanmates see the new zone.
        SendFactions(s);
        SendNoCashOffers(s);
        SendBlockedList(s);
        SendClanList(s);
        SharePresence(s);
        if (_store.ListMail(s.Character.Id).Any(m => !m.Read)) SendMailNotice(s);
    }

    private void OnPlayerMove(Session s, PlayerMove move)
    {
        // PlayerMove carries no zone id: ignore moves from the old scene until the new zone has loaded.
        if (s.Character is null || !s.InZone) return;
        s.Position = (s.Position.Zone, move.X, move.Y, move.Z, move.YAngle);
        s.MoveSpeed = move.Speed;
        s.LastMoveAt = NetTime.Now;
        _store.SavePosition(s.Character.Id, s.Position.Zone, move.X, move.Y, move.Z, move.YAngle);
        DiscoverLeystones(s);
        QuestMove(s);
    }

    private bool InWorld(Session s)
    {
        if (s.Account is not null && s.Character is not null) return true;
        _log.LogWarning("In-world message before StartGame from {Endpoint}", s.Connection.RemoteEndPoint);
        return false;
    }

    private bool RequireAccount(Session s)
    {
        if (s.Account is not null) return true;
        _log.LogWarning("Message before login from {Endpoint}", s.Connection.RemoteEndPoint);
        return false;
    }

    private void Send(Session s, Opcode opcode, Action<NetBuffer> write, uint? sessionId = null, NetDeliveryMethod delivery = NetDeliveryMethod.ReliableOrdered)
    {
        var msg = Envelope.CreateServerMessage(_server, opcode, sessionId ?? s.SessionId);
        write(msg);
        _frames.Outbound(s.Connection.RemoteUniqueIdentifier, opcode, msg.Data.AsSpan(0, msg.LengthBytes).ToArray());
        _server.SendMessage(msg, s.Connection, delivery);
    }

    private static uint NewSessionId()
    {
        uint id;
        do id = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)); while (id == 0);
        return id;
    }

    public void Dispose()
    {
        _stopping = true;
        _server.Shutdown("server stopping");
        _pump?.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class Session(NetConnection connection)
    {
        public NetConnection Connection { get; } = connection;
        public uint SessionId { get; set; }
        public Account? Account { get; set; }
        public CharacterRecord? Character { get; set; }
        public (int Zone, float X, float Y, float Z, float Direction) Position { get; set; }

        // In-world state (GameServer.World.cs)
        public bool InZone { get; set; }
        public int Health { get; set; }
        public int MaxHealth { get; set; }
        public int Energy { get; set; }
        public int MaxEnergy { get; set; }
        public bool Dead { get; set; }
        public Mob? AttackTarget { get; set; }
        public double NextSwingAt { get; set; }
        public double LastCombatAt { get; set; } = double.MinValue;
        public double NextRegenAt { get; set; }
        public Dictionary<int, double> Cooldowns { get; } = new();
        public List<PendingCast> Casts { get; } = new();
        public List<ActiveEffect> Effects { get; } = new();
        public CharacterSheet? Sheet { get; set; }
        public int? LastChestTemplate { get; set; }
        public Dictionary<int, int> PledgesSpent { get; } = new();
        public HashSet<long> Blocked { get; } = new();
        public HashSet<int> Teleports { get; } = new();
        public bool IsGm { get; set; }
        public int FailedLogins { get; set; }

        // Multiplayer (GameServer.Players.cs)
        public float MoveSpeed { get; set; }
        public double LastMoveAt { get; set; } = double.MinValue;
        public Party? Party { get; set; }
        public HashSet<int> PartyInvites { get; } = new();
        public int SharedHealthPercent { get; set; } = -1;
        public Session? PvpTarget { get; set; }
        public Duel? Duel { get; set; }
        public HashSet<int> DuelInvites { get; } = new();
        public Trade? Trade { get; set; }
        public HashSet<int> TradeRequests { get; } = new();
        public Dictionary<int, QuestState> Quests { get; } = new();
        public int? PetTemplate { get; set; }
        public string? TreeLayoutVersion { get; set; }
        public int SigilsOfHealth { get; set; }
        public int SigilsOfEnergy { get; set; }

        public int CharacterId => (int)(Character?.Id ?? 0);
        public string Name => Character?.Name ?? Account?.Username ?? "?";
    }

    /// <summary>A skill being cast: an enemy mob or duel opponent, or a friendly player (heals and buffs on others).</summary>
    private sealed record PendingCast(SkillInfo Skill, int Level, Mob? Target, double ResolveAt, Session? PlayerTarget = null, Session? Friend = null);
}
