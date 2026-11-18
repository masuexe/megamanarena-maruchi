using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Mma.Protocol;

var config = BotConfig.Parse(args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var serverControlEp = new IPEndPoint(IPAddress.Parse(config.Host), config.Port);
Console.WriteLine(
    $"[Arena8BotRepro] Target={serverControlEp}, Bots={config.BotCount}, LagBots={config.LagBots}, " +
    $"DelayMs={config.DelayMs}, DropRate={config.DropRate:P0}, Stall={config.StallEverySec}s/{config.StallDurationMs}ms, ActiveInputs={config.ActiveInputs}");

var bots = new List<VirtualBot>(config.BotCount);
for (int i = 0; i < config.BotCount; i++)
{
    bool isLagged = i >= config.BotCount - config.LagBots;
    bots.Add(new VirtualBot(i + 1, serverControlEp, config, isLagged));
}

try
{
    for (int i = 0; i < bots.Count; i++)
    {
        await bots[i].ConnectAsync(cts.Token);
        await Task.Delay(120, cts.Token);
    }

    Console.WriteLine($"[Arena8BotRepro] All {bots.Count} bots connected and ready in lobby. Waiting for real client(s)...");

    var tasks = bots.Select(b => b.RunLoopAsync(cts.Token)).ToList();
    var telemetryTask = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            await Task.Delay(2000, cts.Token);
            int inMatch = bots.Count(b => b.InMatch);
            uint minCommit = bots.Where(b => b.InMatch).Select(b => b.HighestCommitted).DefaultIfEmpty(0u).Min();
            uint maxCommit = bots.Where(b => b.InMatch).Select(b => b.HighestCommitted).DefaultIfEmpty(0u).Max();
            long sent = bots.Sum(b => b.ProposalsSent);
            long dropped = bots.Sum(b => b.ProposalsDropped);
            Console.WriteLine(
                $"[Telemetry] InMatch={inMatch}/{bots.Count}, Committed={minCommit}..{maxCommit}, ProposalsSent={sent}, SimulatedDrops={dropped}");
        }
    }, cts.Token);

    await Task.WhenAll(tasks);
}
catch (OperationCanceledException)
{
    Console.WriteLine("[Arena8BotRepro] Stopping bots...");
}
finally
{
    foreach (var bot in bots)
    {
        await bot.DisposeAsync();
    }
}

sealed record BotConfig(
    string Host,
    int Port,
    int BotCount,
    int ReadyDelayMs,
    int LagBots,
    int DelayMs,
    double DropRate,
    double StallEverySec,
    int StallDurationMs,
    bool ActiveInputs)
{
    public static BotConfig Parse(string[] args)
    {
        string host = "127.0.0.1";
        int port = MmaConstants.Arena8Port;
        int bots = 7;
        int readyDelayMs = 1200;
        int lagBots = 0;
        int delayMs = 0;
        double dropRate = 0;
        double stallEverySec = 0;
        int stallDurationMs = 400;
        bool activeInputs = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {a}");
            switch (a.ToLowerInvariant())
            {
                case "--host": host = Next(); break;
                case "--port": port = int.Parse(Next()); break;
                case "--bots": bots = Math.Clamp(int.Parse(Next()), 1, 8); break;
                case "--ready-delay-ms": readyDelayMs = int.Parse(Next()); break;
                case "--lag-bots": lagBots = Math.Max(0, int.Parse(Next())); break;
                case "--delay-ms": delayMs = Math.Max(0, int.Parse(Next())); break;
                case "--drop-rate": dropRate = Math.Clamp(double.Parse(Next()), 0.0, 1.0); break;
                case "--stall-every-sec": stallEverySec = Math.Max(0.0, double.Parse(Next())); break;
                case "--stall-duration-ms": stallDurationMs = Math.Max(0, int.Parse(Next())); break;
                case "--active-inputs": activeInputs = true; break;
                default: throw new ArgumentException($"Unknown argument: {a}");
            }
        }

        return new BotConfig(
            host,
            port,
            bots,
            readyDelayMs,
            Math.Min(lagBots, bots),
            delayMs,
            dropRate,
            stallEverySec,
            stallDurationMs,
            activeInputs);
    }
}

sealed class VirtualBot : IAsyncDisposable
{
    private readonly int _index;
    private readonly IPEndPoint _serverControlEp;
    private readonly BotConfig _config;
    private readonly bool _isLagged;
    private readonly UdpClient _controlUdp;
    private readonly UdpClient _inputUdp;
    private readonly Random _rng;
    private readonly HashSet<uint> _receivedServerSeqs = [];

    private uint _sendSeq;
    private ushort _sessionId;
    private byte _slot;
    private byte _buffer = MmaConstants.DefaultBuffer;
    private bool _sentReady;
    private bool _sentMatchReady;

    private uint _deadlineToken;
    private ushort _deadlinePhase = 1;
    private uint _localFrame;
    private uint _ackFrame;

    public VirtualBot(int index, IPEndPoint serverControlEp, BotConfig config, bool isLagged)
    {
        _index = index;
        _serverControlEp = serverControlEp;
        _config = config;
        _isLagged = isLagged;
        _rng = new Random(1000 + index);

        _controlUdp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _inputUdp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
    }

    public bool InMatch { get; private set; }
    public uint HighestCommitted { get; private set; }
    public long ProposalsSent { get; private set; }
    public long ProposalsDropped { get; private set; }

    public async Task ConnectAsync(CancellationToken ct)
    {
        int controlPort = ((IPEndPoint)_controlUdp.Client.LocalEndPoint!).Port;
        int inputPort = ((IPEndPoint)_inputUdp.Client.LocalEndPoint!).Port;

        var connect = new ConnectPacket(
            MmaConstants.Arena8Version,
            (uint)controlPort,
            (uint)inputPort,
            false,
            $"Bot{_index}",
            $"bot-{_index}",
            (short)(_index % 6 + 1),
            (byte)(_index % 8),
            0,
            0,
            0,
            "rmCutman",
            "rmGutsmanBoss",
            "rmCutmanStageMode0",
            1,
            false);

        await SendControlAsync(MmaPacketType.Connect, connect.WritePayload, ct);

        while (true)
        {
            var (header, body) = await ReceiveControlAsync(ct);
            if (header.KnownType == MmaPacketType.DisallowConnect)
            {
                var reason = DisallowConnectPacket.ReadPayload(new GmlBufferReader(body));
                throw new InvalidOperationException($"Bot{_index} disallowed: {reason.Reason}");
            }

            if (header.KnownType == MmaPacketType.NewPlayerWelcome)
            {
                var welcome = NewPlayerWelcomePacket.ReadPayload(new GmlBufferReader(body), arena8Format: true);
                _sessionId = welcome.AssignedSessionId;
                _slot = welcome.PlayerSlot;
                await SendControlAsync(
                    MmaPacketType.NewPlayerJoinAck,
                    new NewPlayerJoinAckPacket(_slot, false).WritePayload,
                    ct);
                Console.WriteLine($"[Bot{_index}] Joined as P{_slot} (session={_sessionId}, lagged={_isLagged})");
                break;
            }
        }
    }

    public async Task RunLoopAsync(CancellationToken ct)
    {
        var recvTask = ReceiveLoopAsync(ct);
        var tickTask = TickLoopAsync(ct);
        await Task.WhenAll(recvTask, tickTask);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (header, body) = await ReceiveControlAsync(ct);
            switch (header.KnownType)
            {
                case MmaPacketType.BufferChange:
                    {
                        var r = new GmlBufferReader(body);
                        _buffer = r.ReadU8();
                        break;
                    }
                case MmaPacketType.PlayerReady:
                    {
                        var ready = ServerPlayerReadyPacket.ReadPayload(new GmlBufferReader(body));
                        if (ready.AllReady && !_sentMatchReady)
                        {
                            _sentMatchReady = true;
                            await SendControlAsync(
                                MmaPacketType.GotoLevel,
                                new MatchReadyPacket(_slot, "rmWilyBoss").WritePayload,
                                ct);
                        }
                        break;
                    }
                case MmaPacketType.GotoLevel:
                    {
                        var gotoLevel = GotoLevelPacket.ReadPayload(new GmlBufferReader(body));
                        _buffer = gotoLevel.Buffer;
                        _deadlineToken = (uint)(gotoLevel.RandomSeed % 4294967296UL);
                        _deadlinePhase = 1;
                        _localFrame = _buffer;
                        _ackFrame = _buffer;
                        HighestCommitted = _buffer;
                        InMatch = true;
                        Console.WriteLine($"[Bot{_index}] Match started! Stage={gotoLevel.Stage}, Buffer={_buffer}, Token={_deadlineToken}");
                        break;
                    }
                case MmaPacketType.DeadlineTimeline:
                    {
                        if (DeadlineBatchPacket.TryReadBody(body, 8, out var batch8) && batch8 is not null)
                        {
                            HighestCommitted = Math.Max(HighestCommitted, batch8.HighestCommitted);
                            _ackFrame = Math.Max(_ackFrame, batch8.HighestCommitted);
                        }
                        break;
                    }
            }
        }
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        await Task.Delay(_config.ReadyDelayMs, ct);
        if (!_sentReady)
        {
            _sentReady = true;
            await SendControlAsync(MmaPacketType.PlayerReady, new ClientReadyPacket(_slot).WritePayload, ct);
        }

        var sw = Stopwatch.StartNew();
        long lastPingMs = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
        while (await timer.WaitForNextTickAsync(ct))
        {
            long elapsedMs = sw.ElapsedMilliseconds;
            if (elapsedMs - lastPingMs >= 2000)
            {
                lastPingMs = elapsedMs;
                await SendControlAsync(MmaPacketType.Ping, w =>
                {
                    w.WriteU8(_slot);
                    w.WriteGmlString("PING");
                }, ct);
            }

            if (!InMatch)
            {
                continue;
            }

            _localFrame++;

            if (_isLagged && _config.StallEverySec > 0)
            {
                double cycleMs = _config.StallEverySec * 1000.0;
                if ((elapsedMs % cycleMs) < _config.StallDurationMs)
                {
                    ProposalsDropped++;
                    continue;
                }
            }

            if (_isLagged && _config.DropRate > 0 && _rng.NextDouble() < _config.DropRate)
            {
                ProposalsDropped++;
                continue;
            }

            uint targetFrame = _localFrame + 2;
            uint bitmask = 0;
            if (_config.ActiveInputs)
            {
                uint phase = (_localFrame / 30) % 4;
                bitmask = phase switch
                {
                    0 => 1u, // right
                    2 => 2u, // left
                    _ => 0u
                };
            }

            var entries = new[]
            {
                new InputFrameEntry(targetFrame - 1, bitmask),
                new InputFrameEntry(targetFrame, bitmask)
            };

            var writer = new GmlBufferWriter();
            new DeadlineProposalPacket(_deadlineToken, _deadlinePhase, _ackFrame, entries).WriteTo(writer, _sessionId);
            byte[] payload = writer.WrittenMemory.ToArray();

            if (_isLagged && _config.DelayMs > 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(_config.DelayMs, ct);
                    await SendRawControlAsync(payload, ct);
                }, ct);
            }
            else
            {
                await SendRawControlAsync(payload, ct);
            }

            ProposalsSent++;
        }
    }

    private async Task<(MmaPacketHeader Header, byte[] Body)> ReceiveControlAsync(CancellationToken ct)
    {
        while (true)
        {
            UdpReceiveResult res = await _controlUdp.ReceiveAsync(ct);
            if (!GmRudpCodec.TryDecode(res.Buffer, out GmRudpFrame frame))
            {
                continue;
            }

            if (frame.Kind == GmRudpKind.Ack)
            {
                continue;
            }

            uint ackSeq = Interlocked.Increment(ref _sendSeq);
            byte[] ackBytes = GmRudpCodec.EncodeAck(ackSeq, [frame.SequenceNumber]);
            await _controlUdp.SendAsync(ackBytes, res.RemoteEndPoint, ct);

            if (!_receivedServerSeqs.Add(frame.SequenceNumber))
            {
                continue;
            }

            var reader = new GmlBufferReader(frame.Payload);
            var header = MmaPacketHeader.ReadFrom(reader);
            return (header, frame.Payload.Span[reader.Offset..].ToArray());
        }
    }

    private Task SendControlAsync(MmaPacketType type, Action<GmlBufferWriter> writeBody, CancellationToken ct)
    {
        var writer = new GmlBufferWriter();
        new MmaPacketHeader(_sessionId, (byte)type).WriteTo(writer);
        writeBody(writer);
        return SendRawControlAsync(writer.WrittenMemory.Span, ct);
    }

    private Task SendRawControlAsync(ReadOnlySpan<byte> rawPayload, CancellationToken ct)
    {
        uint seq = Interlocked.Increment(ref _sendSeq);
        byte[] framed = GmRudpCodec.EncodeData(seq, rawPayload);
        return _controlUdp.SendAsync(framed, _serverControlEp, ct).AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_sessionId != 0 && _slot != 0)
            {
                var writer = new GmlBufferWriter();
                new MmaPacketHeader(_sessionId, (byte)MmaPacketType.Disconnect).WriteTo(writer);
                writer.WriteU8(_slot);
                writer.WriteGmlString("DISCONNECT");
                uint seq = Interlocked.Increment(ref _sendSeq);
                byte[] framed = GmRudpCodec.EncodeData(seq, writer.WrittenMemory.Span);
                await _controlUdp.SendAsync(framed, _serverControlEp);
            }
        }
        catch
        {
            // best effort
        }

        _controlUdp.Dispose();
        _inputUdp.Dispose();
    }
}
