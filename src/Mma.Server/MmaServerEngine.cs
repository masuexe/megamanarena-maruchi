using System.Net;
using Mma.Protocol;

namespace Mma.Server;

/// <summary>
/// Pure, deterministic protocol and lobby/match state machine for a single Mega Man Arena headless server room.
/// </summary>
public sealed class MmaServerEngine
{
    private readonly ServerOptions _options;
    private readonly GameModeConfig _modeConfig;
    private readonly ServerTimingOptions _timings;
    private readonly TimeProvider _timeProvider;
    private readonly TextWriter _logWriter;
    private readonly Func<ushort> _nextSessionId;
    private readonly Func<ulong> _nextRandomSeed;

    private readonly List<PlayerSlotState> _players = [];
    private ushort _sessionId;
    private LobbyPhase _phase = LobbyPhase.Lobby;
    private DateTimeOffset? _startingMatchDeadline;
    private bool _gotoLevelSent;
    private bool _matchInputsSeen;
    private uint _estimatedMatchFrame;

    public MmaServerEngine(
        ServerOptions options,
        TimeProvider? timeProvider = null,
        ServerTimingOptions? timings = null,
        TextWriter? logWriter = null,
        Func<ushort>? nextSessionId = null,
        Func<ulong>? nextRandomSeed = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _modeConfig = options.ModeConfig;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timings = timings ?? ServerTimingOptions.Default;
        _logWriter = logWriter ?? TextWriter.Null;
        _nextSessionId = nextSessionId ?? (() => (ushort)Random.Shared.Next(1, 10001));
        _nextRandomSeed = nextRandomSeed ?? GenerateNonZeroSeed;
        _sessionId = _nextSessionId();
    }

    public ushort SessionId => _sessionId;
    public LobbyPhase Phase => _phase;
    public int ConnectedPlayerCount => _players.Count;
    public GameModeConfig ModeConfig => _modeConfig;
    public ServerOptions Options => _options;

    public IReadOnlyDictionary<uint, uint> GetCachedInputs(byte slot)
    {
        if (slot < 1 || slot > _players.Count)
        {
            return new Dictionary<uint, uint>();
        }

        return new Dictionary<uint, uint>(_players[slot - 1].CachedInputs);
    }

    public bool IsControlEndpointRegistered(IPEndPoint endpoint)
        => FindByControlEndpoint(endpoint) is not null;

    /// <summary>
    /// Processes a datagram received on the primary control UDP socket (<see cref="ServerOptions.Port"/>).
    /// </summary>
    public IReadOnlyList<OutboundDatagram> HandleControlDatagram(IPEndPoint remoteEndPoint, ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        if (payload.Length < MmaPacketHeader.ByteSize)
        {
            return [];
        }

        var reader = new GmlBufferReader(payload);
        var header = MmaPacketHeader.ReadFrom(reader);
        var packetType = (MmaPacketType)header.DataType;

        if (packetType != MmaPacketType.Input)
        {
            _logWriter.WriteLine(
                $"[{_timeProvider.GetUtcNow():O}] Received UDP datagram ({payload.Length} bytes) from {remoteEndPoint}: sessionId={header.SessionId}, dataType={header.DataType} ({packetType})");
        }

        try
        {
            if (packetType == MmaPacketType.Connect)
            {
                return HandleConnect(remoteEndPoint, reader);
            }

            PlayerSlotState? sender = FindByControlEndpoint(remoteEndPoint);
            if (sender is null || header.SessionId != _sessionId)
            {
                return [];
            }

            return packetType switch
            {
                MmaPacketType.LobbyChange or MmaPacketType.RulesUpdate or MmaPacketType.BufferChange
                    or MmaPacketType.TourneyRules or MmaPacketType.TourneyStrike => [],
                MmaPacketType.NewPlayerJoinAck => HandleNewPlayerJoinAck(sender, reader),
                MmaPacketType.Ping => HandlePing(sender, reader),
                MmaPacketType.GratuitousPing => HandleGratuitousPing(sender, reader),
                MmaPacketType.GratuitousPingEcho => HandleGratuitousPingEcho(sender, reader),
                MmaPacketType.CharChange => HandleCharChange(sender, reader),
                MmaPacketType.StageChange => HandleStageChange(sender, reader),
                MmaPacketType.SquadList => HandleSquadList(sender, reader),
                MmaPacketType.SquadResend => HandleSquadResend(sender, reader),
                MmaPacketType.Handicap => HandleHandicap(sender, reader),
                MmaPacketType.PlayerReady => HandlePlayerReady(sender, reader),
                MmaPacketType.PlayerUnready => HandlePlayerUnready(sender, reader),
                MmaPacketType.GotoLevel => HandleMatchReady(sender, reader),
                MmaPacketType.Forfeit => HandleForfeit(sender, reader),
                MmaPacketType.Disconnect => HandleDisconnect(sender, reader),
                MmaPacketType.Input => HandleControlInput(sender, reader),
                _ => []
            };
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
        {
            _logWriter.WriteLine($"[{_timeProvider.GetUtcNow():O}] Ignored malformed {packetType} packet from {remoteEndPoint}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Processes a datagram received on the fast input UDP socket (<c>Port + 1</c>, no u16 session ID header).
    /// </summary>
    public IReadOnlyList<OutboundDatagram> HandleInputDatagram(IPEndPoint remoteEndPoint, ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        if (payload.Length < 1)
        {
            return [];
        }

        var reader = new GmlBufferReader(payload);
        var packetType = (MmaPacketType)reader.ReadU8();

        try
        {
            return packetType switch
            {
                MmaPacketType.Input => HandleFastInput(remoteEndPoint, reader),
                MmaPacketType.PortDiscovery => HandlePortDiscovery(remoteEndPoint, reader),
                MmaPacketType.InputPing => HandleInputPing(remoteEndPoint, reader),
                _ => []
            };
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
        {
            _logWriter.WriteLine($"[{_timeProvider.GetUtcNow():O}] Ignored malformed fast {packetType} packet from {remoteEndPoint}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Advances time-based lobby deadlines, stall detection, and idle lobby keepalives.
    /// </summary>
    public IReadOnlyList<OutboundDatagram> Tick()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (_phase == LobbyPhase.StartingMatch &&
            _startingMatchDeadline is not null &&
            now > _startingMatchDeadline.Value)
        {
            _logWriter.WriteLine($"[{now:O}] Match start deadline exceeded; closing lobby.");
            return CloseAndResetRoom();
        }

        if (_phase == LobbyPhase.InMatch && _players.Count > 0)
        {
            var outbound = new List<OutboundDatagram>();
            foreach (PlayerSlotState player in _players)
            {
                if (now - player.LastMatchActivity > _timings.MatchStallTimeout)
                {
                    _logWriter.WriteLine($"[{now:O}] Player P{player.Slot} stalled in match (>{_timings.MatchStallTimeout.TotalSeconds:0.#}s); closing lobby.");
                    return CloseAndResetRoom();
                }

                // In onlineUseInputRUDP mode, P1 never initiates GratuitousPing (23) in rmOnlineLobby;
                // probe idle post-match peers once input stream stops so returned clients can reply Echo (24).
                if (_matchInputsSeen &&
                    !player.ReturnedToLobby &&
                    _timings.LobbyHeartbeatInterval > TimeSpan.Zero &&
                    now - player.LastMatchActivity >= _timings.LobbyHeartbeatInterval &&
                    now - player.LastHeartbeatSentAt >= _timings.LobbyHeartbeatInterval)
                {
                    player.LastHeartbeatSentAt = now;
                    outbound.Add(BuildGratuitousPing(player));
                }
            }

            return outbound;
        }

        if (_phase == LobbyPhase.Lobby && _players.Count > 0 && _timings.LobbyHeartbeatInterval > TimeSpan.Zero)
        {
            var outbound = new List<OutboundDatagram>();
            foreach (PlayerSlotState player in _players)
            {
                if (now - player.LastLobbyActivity >= _timings.LobbyHeartbeatInterval &&
                    now - player.LastHeartbeatSentAt >= _timings.LobbyHeartbeatInterval)
                {
                    player.LastHeartbeatSentAt = now;
                    outbound.Add(BuildGratuitousPing(player));
                }
            }

            return outbound;
        }

        return [];
    }

    private OutboundDatagram BuildGratuitousPing(PlayerSlotState player) =>
        BuildControlDatagram(player, MmaPacketType.GratuitousPing, static writer =>
        {
            writer.WriteGmlString("GRATUITOUS_PING");
        });

    private List<OutboundDatagram> HandleConnect(IPEndPoint remoteEndPoint, GmlBufferReader reader)
    {
        var connect = ConnectPacket.ReadPayload(reader);

        // Ignore duplicate Connect requests from an already assigned (IP, ControlPort) peer.
        if (FindByControlEndpoint(remoteEndPoint) is not null)
        {
            return [];
        }

        if (!string.Equals(connect.Version, _options.ExpectedVersion, StringComparison.Ordinal))
        {
            return [BuildDisallowDatagram(remoteEndPoint, DisallowConnectReason.VersionMismatch)];
        }

        if (connect.IsObserver)
        {
            return [BuildDisallowDatagram(remoteEndPoint, DisallowConnectReason.ObserversDisabled)];
        }

        if (_players.Count >= _options.MaxPlayers)
        {
            return [BuildDisallowDatagram(remoteEndPoint, DisallowConnectReason.LobbyFull)];
        }

        if (_phase != LobbyPhase.Lobby)
        {
            return [BuildDisallowDatagram(remoteEndPoint, DisallowConnectReason.MatchAlreadyStarted)];
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        byte assignedSlot = (byte)(_players.Count + 1);
        string initialStage = _modeConfig.OnlineMode switch
        {
            MmaConstants.OnlineModeBoss => connect.BossStage,
            MmaConstants.OnlineModeStage => connect.StageModeStage,
            _ => connect.ArenaStage
        };

        var newPlayer = new PlayerSlotState
        {
            Slot = assignedSlot,
            Ip = remoteEndPoint.Address,
            ControlPort = remoteEndPoint.Port,
            InputPort = connect.ClientUdpPort > 0 && connect.ClientUdpPort <= ushort.MaxValue
                ? (int)connect.ClientUdpPort
                : remoteEndPoint.Port + 1,
            Name = connect.Name,
            CloudId = connect.CloudId,
            Character = connect.Character,
            Color = connect.Color,
            CustomPrimaryColor = connect.CustomPrimaryColor,
            CustomSecondaryColor = connect.CustomSecondaryColor,
            Skin = connect.Skin,
            Stage = initialStage,
            SlideDownJump = connect.SlideDownJump,
            CheatEnabled = connect.CheatEnabled,
            HandicapStock = _modeConfig.StockCount,
            HandicapHealth = MmaConstants.DefaultHealthMax,
            LastLobbyActivity = now,
            LastMatchActivity = now,
            LastHeartbeatSentAt = now
        };

        var existingSummaries = _players.Select(p => p.ToSummary()).ToArray();
        _players.Add(newPlayer);

        _logWriter.WriteLine($"[{now:O}] Player P{assignedSlot} ('{newPlayer.Name}') connected from {remoteEndPoint}");

        var welcome = new NewPlayerWelcomePacket(
            _sessionId,
            assignedSlot,
            ObserverCount: 0,
            existingSummaries,
            Arena8Format: _options.UsesArena8Protocol);

        return [BuildControlDatagram(newPlayer, MmaPacketType.NewPlayerWelcome, welcome.WritePayload)];
    }

    private List<OutboundDatagram> HandleNewPlayerJoinAck(PlayerSlotState sender, GmlBufferReader reader)
    {
        var ack = NewPlayerJoinAckPacket.ReadPayload(reader);
        if (ack.PlayerSlot != sender.Slot || ack.IsObserver || _phase != LobbyPhase.Lobby)
        {
            return [];
        }

        sender.LastLobbyActivity = _timeProvider.GetUtcNow();
        var outbound = new List<OutboundDatagram>();

        // Real GameMaker clients look for up to 6 ConnectAck datagrams to absorb startup packet loss.
        var connectAck = new ConnectAckPacket(_modeConfig.OnlineMode);
        for (int i = 0; i < 6; i++)
        {
            outbound.Add(BuildControlDatagram(sender, MmaPacketType.ConnectAck, connectAck.WritePayload));
        }

        var rulesUpdate = new RulesUpdatePacket(
            Hazards: true,
            PitSpikeHazards: true,
            StockCount: _modeConfig.StockCount,
            EpOption: 0,
            TeamAttack: true,
            StageSelection: MmaConstants.DefaultStageSelection);
        outbound.Add(BuildControlDatagram(sender, MmaPacketType.RulesUpdate, rulesUpdate.WritePayload));

        var lobbyChange = new LobbyChangePacket(
            _modeConfig.Submode,
            _modeConfig.Subsubmode,
            BlindPicks: false,
            HandicapEnabled: false);
        outbound.Add(BuildControlDatagram(sender, MmaPacketType.LobbyChange, lobbyChange.WritePayload));

        if (_modeConfig.Submode == MmaConstants.SubmodeSquads)
        {
            AppendSquadListsToAll(outbound);
        }

        if (_modeConfig.Submode == MmaConstants.SubmodeStage)
        {
            var difficulties = new StageDifficultiesPacket();
            outbound.Add(BuildControlDatagram(sender, MmaPacketType.StageDifficulties, difficulties.WritePayload));
        }

        outbound.Add(BuildControlDatagram(sender, MmaPacketType.Ping, static w => w.WriteGmlString("INITIAL PING")));

        var newPlayerData = new NewPlayerDataPacket(sender.Slot, sender.ToSummary() with { Ready = false });
        var writer = new GmlBufferWriter();
        new MmaPacketHeader(_sessionId, (byte)MmaPacketType.NewPlayerData).WriteTo(writer);
        newPlayerData.WritePayload(writer);
        byte[] newPlayerPayload = writer.ToArray();

        // Players in slots > sender.Slot already received sender's record in their Welcome (4);
        // sending NewPlayerData(k < theirSlot) would overwrite their client onlinePlayerCount backwards.
        foreach (PlayerSlotState recipient in _players)
        {
            if (recipient.Slot <= sender.Slot)
            {
                outbound.Add(new OutboundDatagram(
                    UdpTransportSocket.Control,
                    new IPEndPoint(recipient.Ip, recipient.ControlPort),
                    newPlayerPayload));
            }
        }

        return outbound;
    }

    private List<OutboundDatagram> HandlePing(PlayerSlotState sender, GmlBufferReader reader)
    {
        byte claimedSlot = reader.ReadU8();
        _ = reader.TryReadOptionalGmlString();
        if (claimedSlot != sender.Slot)
        {
            return [];
        }

        return RecordPostMatchOrLobbyKeepalive(sender, echoGratuitous: false);
    }

    private List<OutboundDatagram> HandleGratuitousPing(PlayerSlotState sender, GmlBufferReader reader)
    {
        byte claimedSlot = reader.ReadU8();
        _ = reader.TryReadOptionalGmlString();
        if (claimedSlot != sender.Slot)
        {
            return [];
        }

        return RecordPostMatchOrLobbyKeepalive(sender, echoGratuitous: true);
    }

    private List<OutboundDatagram> HandleGratuitousPingEcho(PlayerSlotState sender, GmlBufferReader reader)
    {
        byte claimedSlot = reader.ReadU8();
        _ = reader.TryReadOptionalGmlString();
        if (claimedSlot != sender.Slot)
        {
            return [];
        }

        return RecordPostMatchOrLobbyKeepalive(sender, echoGratuitous: false);
    }

    private List<OutboundDatagram> RecordPostMatchOrLobbyKeepalive(PlayerSlotState sender, bool echoGratuitous)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        sender.LastLobbyActivity = now;
        sender.LastMatchActivity = now;

        if (_phase == LobbyPhase.InMatch && _matchInputsSeen)
        {
            sender.ReturnedToLobby = true;
            if (_players.All(p => p.ReturnedToLobby))
            {
                ResetMatchStateForRound2(now);
            }
        }

        if (!echoGratuitous)
        {
            return [];
        }

        return
        [
            BuildControlDatagram(sender, MmaPacketType.GratuitousPingEcho, static w => w.WriteGmlString("GRATUITOUS_PING_ECHO"))
        ];
    }

    private List<OutboundDatagram> HandleCharChange(PlayerSlotState sender, GmlBufferReader reader)
    {
        var packet = CharChangePacket.ReadPayload(reader);
        if (packet.PlayerSlot != sender.Slot)
        {
            return [];
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        EnsureLobbyPhaseForRound2(now);
        if (_phase != LobbyPhase.Lobby)
        {
            return [];
        }

        sender.Character = packet.Character;
        sender.Color = packet.Color;
        sender.CustomPrimaryColor = packet.CustomPrimaryColor;
        sender.CustomSecondaryColor = packet.CustomSecondaryColor;
        sender.Skin = packet.Skin;
        sender.SlideDownJump = packet.SlideDownJump;
        sender.LastLobbyActivity = now;

        var outboundPacket = packet with { PacketTag = "CHAR CHANGE" };
        var outbound = new List<OutboundDatagram>(_players.Count);
        AppendBroadcastControl(outbound, MmaPacketType.CharChange, outboundPacket.WritePayload);
        return outbound;
    }

    private List<OutboundDatagram> HandleStageChange(PlayerSlotState sender, GmlBufferReader reader)
    {
        var packet = StageChangePacket.ReadPayload(reader);
        if (packet.PlayerSlot != sender.Slot)
        {
            return [];
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        EnsureLobbyPhaseForRound2(now);
        if (_phase != LobbyPhase.Lobby)
        {
            return [];
        }

        sender.Stage = packet.Stage;
        sender.LastLobbyActivity = now;

        var outboundPacket = packet with { PacketTag = "STAGE CHANGE" };
        var outbound = new List<OutboundDatagram>(_players.Count);
        AppendBroadcastControl(outbound, MmaPacketType.StageChange, outboundPacket.WritePayload);
        return outbound;
    }

    private List<OutboundDatagram> HandleSquadList(PlayerSlotState sender, GmlBufferReader reader)
    {
        if (_modeConfig.Submode != MmaConstants.SubmodeSquads)
        {
            return [];
        }

        var squad = SquadListPacket.ReadPayload(reader);
        if (squad.PlayerSlot != sender.Slot || squad.Characters.Count != _modeConfig.StockCount)
        {
            return [];
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        EnsureLobbyPhaseForRound2(now);
        if (_phase != LobbyPhase.Lobby)
        {
            return [];
        }

        sender.Squad.Clear();
        sender.Squad.AddRange(squad.Characters);
        sender.Color = squad.Color;
        sender.SlideDownJump = squad.SlideDownJump;
        sender.Character = (short)squad.Characters[0];
        sender.LastLobbyActivity = now;

        var outbound = new List<OutboundDatagram>(_players.Count);
        AppendBroadcastControl(outbound, MmaPacketType.SquadList, squad.WritePayload);
        return outbound;
    }

    private List<OutboundDatagram> HandleSquadResend(PlayerSlotState sender, GmlBufferReader reader)
    {
        if (_modeConfig.Submode != MmaConstants.SubmodeSquads)
        {
            return [];
        }

        var resend = SquadResendPacket.ReadPayload(reader);
        if (resend.RequestingSlot != sender.Slot ||
            resend.SquadSlot < 1 ||
            resend.SquadSlot > _players.Count)
        {
            return [];
        }

        sender.LastLobbyActivity = _timeProvider.GetUtcNow();
        PlayerSlotState target = _players[resend.SquadSlot - 1];
        var packet = new SquadListPacket(target.Slot, target.Color, target.SlideDownJump, target.Squad);
        return [BuildControlDatagram(sender, MmaPacketType.SquadList, packet.WritePayload)];
    }

    private List<OutboundDatagram> HandleHandicap(PlayerSlotState sender, GmlBufferReader reader)
    {
        var packet = HandicapPacket.ReadPayload(reader);
        if (packet.PlayerSlot != sender.Slot || _phase != LobbyPhase.Lobby)
        {
            return [];
        }

        sender.HandicapStock = packet.Stock;
        sender.HandicapHealth = packet.Health;
        sender.LastLobbyActivity = _timeProvider.GetUtcNow();

        var outboundPacket = packet with { PacketTag = "HANDICAP" };
        var outbound = new List<OutboundDatagram>(_players.Count);
        AppendBroadcastControl(outbound, MmaPacketType.Handicap, outboundPacket.WritePayload);
        return outbound;
    }

    private List<OutboundDatagram> HandlePlayerReady(PlayerSlotState sender, GmlBufferReader reader)
    {
        var packet = ClientReadyPacket.ReadPayload(reader);
        if (packet.PlayerSlot != sender.Slot)
        {
            return [];
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        EnsureLobbyPhaseForRound2(now);
        if (_phase != LobbyPhase.Lobby)
        {
            return [];
        }

        if (_modeConfig.Submode == MmaConstants.SubmodeSquads && sender.Squad.Count != _modeConfig.StockCount)
        {
            return [];
        }

        sender.Ready = true;
        sender.LastLobbyActivity = now;

        bool allReady = _players.Count == _options.MaxPlayers && _players.All(p => p.Ready);
        if (allReady && _modeConfig.Submode == MmaConstants.SubmodeTeams && !HasDistinctTeams())
        {
            allReady = false;
        }

        var outbound = new List<OutboundDatagram>();
        if (allReady)
        {
            _phase = LobbyPhase.StartingMatch;
            _startingMatchDeadline = now + _timings.StartingMatchTimeout;
            foreach (PlayerSlotState player in _players)
            {
                player.LevelChoice = null;
            }

            if (_modeConfig.Submode == MmaConstants.SubmodeSquads)
            {
                AppendSquadListsToAll(outbound);
            }
        }

        var broadcast = new ServerPlayerReadyPacket(
            allReady,
            sender.Slot,
            sender.Character,
            sender.Color,
            sender.Skin,
            sender.Stage);
        AppendBroadcastControl(outbound, MmaPacketType.PlayerReady, broadcast.WritePayload);
        return outbound;
    }

    private List<OutboundDatagram> HandlePlayerUnready(PlayerSlotState sender, GmlBufferReader reader)
    {
        var packet = PlayerUnreadyPacket.ReadPayload(reader);
        if (packet.PlayerSlot != sender.Slot)
        {
            return [];
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        EnsureLobbyPhaseForRound2(now);
        if (_phase != LobbyPhase.Lobby || !sender.Ready)
        {
            return [];
        }

        sender.Ready = false;
        sender.LastLobbyActivity = now;

        var outboundPacket = new PlayerUnreadyPacket(sender.Slot, "PLAYER UNREADY");
        var outbound = new List<OutboundDatagram>(_players.Count);
        AppendBroadcastControl(outbound, MmaPacketType.PlayerUnready, outboundPacket.WritePayload);
        return outbound;
    }

    private List<OutboundDatagram> HandleMatchReady(PlayerSlotState sender, GmlBufferReader reader)
    {
        var packet = MatchReadyPacket.ReadPayload(reader);
        if (packet.PlayerSlot != sender.Slot || _phase != LobbyPhase.StartingMatch || _gotoLevelSent)
        {
            return [];
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        sender.LevelChoice = packet.StageChoice;
        sender.LastLobbyActivity = now;

        if (_players.Count != _options.MaxPlayers || _players.Any(p => p.LevelChoice is null))
        {
            return [];
        }

        _gotoLevelSent = true;
        _phase = LobbyPhase.InMatch;
        _startingMatchDeadline = null;
        _matchInputsSeen = false;
        _estimatedMatchFrame = 0;

        foreach (PlayerSlotState player in _players)
        {
            player.Forfeited = false;
            player.ForfeitFrame = null;
            player.ReturnedToLobby = false;
            player.CachedInputs.Clear();
            player.LatestClientMatchFrame = 0;
            player.LastRelayedClientMatchFrame = null;
            player.LastRelayedAt = null;
            player.LastMatchActivity = now;
            player.LastHeartbeatSentAt = now;
        }

        var outbound = new List<OutboundDatagram>();
        if (_modeConfig.Submode == MmaConstants.SubmodeSquads)
        {
            AppendSquadListsToAll(outbound);
        }

        foreach (PlayerSlotState player in _players)
        {
            var handicap = new HandicapPacket(player.Slot, player.HandicapStock, player.HandicapHealth, "GOTO LEVEL");
            AppendBroadcastControl(outbound, MmaPacketType.Handicap, handicap.WritePayload);
        }

        if (_modeConfig.Submode == MmaConstants.SubmodeStage)
        {
            var difficulties = new StageDifficultiesPacket();
            AppendBroadcastControl(outbound, MmaPacketType.StageDifficulties, difficulties.WritePayload);
        }

        byte clampedBuffer = (byte)Math.Clamp(_options.Buffer, 0, MmaConstants.MaxOnlineBuffer);
        ulong seed = _nextRandomSeed();
        var gotoLevel = new GotoLevelPacket(
            _options.ResolvedStage,
            seed,
            clampedBuffer,
            Hazards: true,
            PitSpikes: true,
            StockCount: _modeConfig.StockCount,
            TeamAttack: true);
        AppendBroadcastControl(outbound, MmaPacketType.GotoLevel, gotoLevel.WritePayload);

        _logWriter.WriteLine(
            $"[{now:O}] Starting match ({_options.Mode} / {_players.Count} players) on stage '{_options.ResolvedStage}' (seed={seed}, buffer={clampedBuffer})");

        return outbound;
    }

    private List<OutboundDatagram> HandleForfeit(PlayerSlotState sender, GmlBufferReader reader)
    {
        var packet = ForfeitPacket.ReadPayload(reader);
        if (packet.PlayerSlot != sender.Slot || _phase != LobbyPhase.InMatch)
        {
            return [];
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        sender.LastMatchActivity = now;

        byte clampedBuffer = (byte)Math.Clamp(_options.Buffer, 0, MmaConstants.MaxOnlineBuffer);
        uint frameId = _estimatedMatchFrame + clampedBuffer + 2;
        sender.Forfeited = true;
        sender.ForfeitFrame = frameId;

        var outboundPacket = new MatchForfeitPacket(sender.Slot, frameId);
        var outbound = new List<OutboundDatagram>(_players.Count);
        AppendBroadcastControl(outbound, MmaPacketType.MatchForfeit, outboundPacket.WritePayload);
        return outbound;
    }

    private List<OutboundDatagram> HandleDisconnect(PlayerSlotState sender, GmlBufferReader reader)
    {
        byte claimedSlot = reader.ReadU8();
        _ = reader.TryReadOptionalGmlString();
        if (claimedSlot != sender.Slot)
        {
            return [];
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        _logWriter.WriteLine($"[{now:O}] Player P{sender.Slot} disconnected.");

        if (_phase != LobbyPhase.Lobby)
        {
            return CloseAndResetRoom();
        }

        int removedIndex = sender.Slot - 1;
        _players.RemoveAt(removedIndex);
        for (int i = 0; i < _players.Count; i++)
        {
            _players[i].Slot = (byte)(i + 1);
            _players[i].LastLobbyActivity = now;
        }

        var outbound = new List<OutboundDatagram>();
        byte newCount = (byte)_players.Count;
        foreach (PlayerSlotState recipient in _players)
        {
            var portPacket = new ChangePlayerPortPacket(recipient.Slot, newCount);
            outbound.Add(BuildControlDatagram(recipient, MmaPacketType.ChangePlayerPort, portPacket.WritePayload));

            foreach (PlayerSlotState entry in _players)
            {
                var dataPacket = new ChangePlayerDataPacket(
                    entry.Slot,
                    entry.Name,
                    entry.Character,
                    entry.Color,
                    entry.CustomPrimaryColor,
                    entry.CustomSecondaryColor,
                    entry.Skin,
                    entry.Stage,
                    entry.Ready,
                    entry.SlideDownJump,
                    entry.HandicapStock,
                    entry.HandicapHealth,
                    entry.CheatEnabled);
                outbound.Add(BuildControlDatagram(recipient, MmaPacketType.ChangePlayerData, dataPacket.WritePayload));
            }

            outbound.Add(BuildControlDatagram(recipient, MmaPacketType.Ping, static w => w.WriteGmlString("PING")));
        }

        if (_modeConfig.Submode == MmaConstants.SubmodeSquads && _players.Count > 0)
        {
            AppendSquadListsToAll(outbound);
        }

        return outbound;
    }

    private List<OutboundDatagram> HandleControlInput(PlayerSlotState sender, GmlBufferReader reader)
    {
        sender.ObservedInputTransport = PeerInputTransport.Control;
        var input = ClientInputPacket.ReadBody(reader);
        return ProcessClientInput(sender, PeerInputTransport.Control, input);
    }

    private List<OutboundDatagram> HandleFastInput(IPEndPoint remoteEndPoint, GmlBufferReader reader)
    {
        var input = ClientInputPacket.ReadBody(reader);
        if (input.Slot < 1 || input.Slot > _players.Count)
        {
            return [];
        }

        PlayerSlotState sender = _players[input.Slot - 1];
        if (!ValidateAndLatchInputEndpoint(sender, remoteEndPoint))
        {
            return [];
        }

        sender.ObservedInputTransport = PeerInputTransport.FastInput;
        return ProcessClientInput(sender, PeerInputTransport.FastInput, input);
    }

    private List<OutboundDatagram> HandlePortDiscovery(IPEndPoint remoteEndPoint, GmlBufferReader reader)
    {
        byte playerSlot = reader.ReadU8();
        if (playerSlot >= 1 && playerSlot <= _players.Count)
        {
            PlayerSlotState candidate = _players[playerSlot - 1];
            if (ValidateAndLatchInputEndpoint(candidate, remoteEndPoint))
            {
                candidate.ObservedInputTransport = PeerInputTransport.FastInput;
            }
        }

        return [new OutboundDatagram(UdpTransportSocket.Input, remoteEndPoint, [(byte)MmaPacketType.PortDiscoveryAck])];
    }

    private List<OutboundDatagram> HandleInputPing(IPEndPoint remoteEndPoint, GmlBufferReader reader)
    {
        _ = reader.TryReadOptionalGmlString();
        PlayerSlotState? matched = FindBestFastInputCandidate(remoteEndPoint);
        if (matched is not null)
        {
            matched.InputPort = remoteEndPoint.Port;
            matched.HasObservedInputPort = true;
            matched.ObservedInputTransport = PeerInputTransport.FastInput;
            DateTimeOffset now = _timeProvider.GetUtcNow();
            matched.LastLobbyActivity = now;
            matched.LastMatchActivity = now;

            if (_phase == LobbyPhase.InMatch && _matchInputsSeen)
            {
                matched.ReturnedToLobby = true;
                if (_players.All(p => p.ReturnedToLobby))
                {
                    ResetMatchStateForRound2(now);
                }
            }
        }

        return [];
    }

    private List<OutboundDatagram> ProcessClientInput(
        PlayerSlotState sender,
        PeerInputTransport senderTransport,
        ClientInputPacket packet)
    {
        if (packet.Slot != sender.Slot || _phase != LobbyPhase.InMatch)
        {
            return [];
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        sender.LastMatchActivity = now;
        sender.ReturnedToLobby = false;
        _matchInputsSeen = true;
        if (packet.ClientMatchFrame > _estimatedMatchFrame)
        {
            _estimatedMatchFrame = packet.ClientMatchFrame;
        }
        if (packet.ClientMatchFrame > sender.LatestClientMatchFrame)
        {
            sender.LatestClientMatchFrame = packet.ClientMatchFrame;
        }

        var deduplicated = new List<InputFrameEntry>(packet.Frames.Count);
        var seenInPacket = new Dictionary<uint, int>(packet.Frames.Count);
        int keepWindow = Math.Clamp(_options.Buffer * 2 + 8, 24, 96);

        uint maxFrameSeen = sender.LatestClientMatchFrame;
        foreach (InputFrameEntry entry in packet.Frames)
        {
            if (entry.FrameId > maxFrameSeen)
            {
                maxFrameSeen = entry.FrameId;
            }

            sender.CachedInputs[entry.FrameId] = entry.Bitmask;
            if (seenInPacket.TryGetValue(entry.FrameId, out int existingIndex))
            {
                deduplicated[existingIndex] = entry;
            }
            else
            {
                seenInPacket[entry.FrameId] = deduplicated.Count;
                deduplicated.Add(entry);
            }
        }

        PruneOldCachedInputs(sender.CachedInputs, maxFrameSeen, keepWindow);

        IReadOnlyList<InputFrameEntry> framesToRelay;
        if (deduplicated.Count == 0)
        {
            if (sender.CachedInputs.Count == 0 ||
                (sender.LastRelayedAt is not null && now - sender.LastRelayedAt.Value < _timings.FreezeRetransmitCooldown))
            {
                return [];
            }

            framesToRelay = SnapshotRecentCachedInputs(sender.CachedInputs, maxCount: 24);
        }
        else
        {
            int digest = ComputeFrameDigest(deduplicated);
            if (sender.LastRelayedClientMatchFrame == packet.ClientMatchFrame &&
                sender.LastRelayDigest == digest &&
                sender.LastRelayedAt is not null &&
                now - sender.LastRelayedAt.Value < _timings.FreezeRetransmitCooldown)
            {
                return [];
            }

            sender.LastRelayDigest = digest;
            framesToRelay = deduplicated;
        }

        sender.LastRelayedClientMatchFrame = packet.ClientMatchFrame;
        sender.LastRelayedAt = now;

        var relay = new RelayInputPacket(sender.Slot, framesToRelay);
        var outbound = new List<OutboundDatagram>(Math.Max(0, _players.Count - 1));
        foreach (PlayerSlotState recipient in _players)
        {
            if (recipient.Slot == sender.Slot)
            {
                continue;
            }

            PeerInputTransport targetTransport = recipient.ObservedInputTransport ?? senderTransport;
            var writer = new GmlBufferWriter();
            if (targetTransport == PeerInputTransport.Control)
            {
                relay.WriteToControl(writer, _sessionId);
                outbound.Add(new OutboundDatagram(
                    UdpTransportSocket.Control,
                    new IPEndPoint(recipient.Ip, recipient.ControlPort),
                    writer.ToArray()));
            }
            else
            {
                relay.WriteToFastInput(writer);
                outbound.Add(new OutboundDatagram(
                    UdpTransportSocket.Input,
                    new IPEndPoint(recipient.Ip, recipient.InputPort),
                    writer.ToArray()));
            }
        }

        return outbound;
    }

    private List<OutboundDatagram> CloseAndResetRoom()
    {
        var outbound = new List<OutboundDatagram>(_players.Count);
        AppendBroadcastControl(outbound, MmaPacketType.LobbyClosed, static w => w.WriteGmlString("LOBBY_CLOSED"));
        _players.Clear();
        _phase = LobbyPhase.Lobby;
        _startingMatchDeadline = null;
        _gotoLevelSent = false;
        _matchInputsSeen = false;
        _estimatedMatchFrame = 0;
        _sessionId = _nextSessionId();
        return outbound;
    }

    private void EnsureLobbyPhaseForRound2(DateTimeOffset now)
    {
        if (_phase == LobbyPhase.InMatch)
        {
            ResetMatchStateForRound2(now);
        }
    }

    private void ResetMatchStateForRound2(DateTimeOffset now)
    {
        _phase = LobbyPhase.Lobby;
        _gotoLevelSent = false;
        _matchInputsSeen = false;
        _startingMatchDeadline = null;
        _estimatedMatchFrame = 0;

        foreach (PlayerSlotState player in _players)
        {
            player.Ready = false;
            player.LevelChoice = null;
            player.Forfeited = false;
            player.ForfeitFrame = null;
            player.ReturnedToLobby = false;
            player.CachedInputs.Clear();
            player.LatestClientMatchFrame = 0;
            player.LastRelayedClientMatchFrame = null;
            player.LastRelayedAt = null;
            player.LastLobbyActivity = now;
            player.LastHeartbeatSentAt = now;
        }

        _logWriter.WriteLine($"[{now:O}] Round complete; room returned to Lobby for Round 2.");
    }

    private bool HasDistinctTeams()
    {
        if (_players.Count < 2)
        {
            return false;
        }

        int firstTeam = GetTeamBucket(_players[0]);
        for (int i = 1; i < _players.Count; i++)
        {
            if (GetTeamBucket(_players[i]) != firstTeam)
            {
                return true;
            }
        }

        return false;
    }

    private static int GetTeamBucket(PlayerSlotState player)
    {
        // Random-team slot 9004 with color 3 resolves to neutral gray team 5 in objOnlineControl_Other_68.
        if (player.Character == 9004 && player.Color == 3)
        {
            return 5;
        }

        return player.Color / 3;
    }

    private PlayerSlotState? FindByControlEndpoint(IPEndPoint endpoint)
    {
        foreach (PlayerSlotState player in _players)
        {
            if (player.Ip.Equals(endpoint.Address) && player.ControlPort == endpoint.Port)
            {
                return player;
            }
        }

        return null;
    }

    private PlayerSlotState? FindBestFastInputCandidate(IPEndPoint endpoint)
    {
        PlayerSlotState? exactMatch = null;
        PlayerSlotState? plusOneMatch = null;
        PlayerSlotState? singleOnIp = null;
        int ipCount = 0;

        foreach (PlayerSlotState player in _players)
        {
            if (!player.Ip.Equals(endpoint.Address))
            {
                continue;
            }

            ipCount++;
            singleOnIp = player;
            if (player.InputPort == endpoint.Port)
            {
                exactMatch = player;
            }
            else if (player.ControlPort + 1 == endpoint.Port)
            {
                plusOneMatch = player;
            }
        }

        return exactMatch ?? plusOneMatch ?? (ipCount == 1 ? singleOnIp : null);
    }

    private bool ValidateAndLatchInputEndpoint(PlayerSlotState candidate, IPEndPoint endpoint)
    {
        if (!candidate.Ip.Equals(endpoint.Address))
        {
            return false;
        }

        if (candidate.HasObservedInputPort)
        {
            return candidate.InputPort == endpoint.Port;
        }

        // Prevent spoofing another slot's registered control or observed input port on the same IP.
        foreach (PlayerSlotState other in _players)
        {
            if (ReferenceEquals(other, candidate) || !other.Ip.Equals(endpoint.Address))
            {
                continue;
            }

            if (other.ControlPort == endpoint.Port ||
                (other.HasObservedInputPort && other.InputPort == endpoint.Port))
            {
                return false;
            }
        }

        candidate.InputPort = endpoint.Port;
        candidate.HasObservedInputPort = true;
        return true;
    }

    private void AppendSquadListsToAll(List<OutboundDatagram> outbound)
    {
        foreach (PlayerSlotState recipient in _players)
        {
            foreach (PlayerSlotState squadOwner in _players)
            {
                var packet = new SquadListPacket(
                    squadOwner.Slot,
                    squadOwner.Color,
                    squadOwner.SlideDownJump,
                    squadOwner.Squad);
                outbound.Add(BuildControlDatagram(recipient, MmaPacketType.SquadList, packet.WritePayload));
            }
        }
    }

    private void AppendBroadcastControl(
        List<OutboundDatagram> outbound,
        MmaPacketType packetType,
        Action<GmlBufferWriter> writePayload,
        byte? excludeSlot = null)
    {
        var writer = new GmlBufferWriter();
        new MmaPacketHeader(_sessionId, (byte)packetType).WriteTo(writer);
        writePayload(writer);
        byte[] payload = writer.ToArray();

        foreach (PlayerSlotState recipient in _players)
        {
            if (excludeSlot is not null && recipient.Slot == excludeSlot.Value)
            {
                continue;
            }

            outbound.Add(new OutboundDatagram(
                UdpTransportSocket.Control,
                new IPEndPoint(recipient.Ip, recipient.ControlPort),
                payload));
        }
    }

    private OutboundDatagram BuildControlDatagram(
        PlayerSlotState recipient,
        MmaPacketType packetType,
        Action<GmlBufferWriter> writePayload)
    {
        var writer = new GmlBufferWriter();
        new MmaPacketHeader(_sessionId, (byte)packetType).WriteTo(writer);
        writePayload(writer);
        return new OutboundDatagram(
            UdpTransportSocket.Control,
            new IPEndPoint(recipient.Ip, recipient.ControlPort),
            writer.ToArray());
    }

    private OutboundDatagram BuildDisallowDatagram(IPEndPoint target, DisallowConnectReason reason)
    {
        var writer = new GmlBufferWriter();
        new MmaPacketHeader(_sessionId, (byte)MmaPacketType.DisallowConnect).WriteTo(writer);
        new DisallowConnectPacket(reason).WritePayload(writer);
        return new OutboundDatagram(UdpTransportSocket.Control, target, writer.ToArray());
    }

    private static void PruneOldCachedInputs(SortedDictionary<uint, uint> cache, uint maxFrameSeen, int keepWindow)
    {
        if (maxFrameSeen <= (uint)keepWindow)
        {
            return;
        }

        uint minKeptFrame = maxFrameSeen - (uint)keepWindow;
        while (cache.Count > 0)
        {
            using var enumerator = cache.Keys.GetEnumerator();
            if (!enumerator.MoveNext() || enumerator.Current >= minKeptFrame)
            {
                break;
            }

            cache.Remove(enumerator.Current);
        }
    }

    private static List<InputFrameEntry> SnapshotRecentCachedInputs(SortedDictionary<uint, uint> cache, int maxCount)
    {
        int skip = Math.Max(0, cache.Count - maxCount);
        var entries = new List<InputFrameEntry>(Math.Min(cache.Count, maxCount));
        int index = 0;
        foreach (KeyValuePair<uint, uint> kvp in cache)
        {
            if (index++ >= skip)
            {
                entries.Add(new InputFrameEntry(kvp.Key, kvp.Value));
            }
        }

        return entries;
    }

    private static int ComputeFrameDigest(IReadOnlyList<InputFrameEntry> frames)
    {
        var hash = new HashCode();
        hash.Add(frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            hash.Add(frames[i].FrameId);
            hash.Add(frames[i].Bitmask);
        }

        return hash.ToHashCode();
    }

    private static ulong GenerateNonZeroSeed()
    {
        ulong value = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
        return value == 0 ? 1UL : value;
    }
}
