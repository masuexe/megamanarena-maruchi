using System.Net;
using Mma.Protocol;

namespace Mma.Server;

/// <summary>
/// Transport framing and reliability layer over <see cref="MmaServerEngine"/> implementing GameMaker's
/// 12-byte RUDP protocol on the control socket while preserving raw fallback for unframed probe/diagnostic callers.
/// </summary>
public sealed class GmRudpChannelHost
{
    private const int MaxInboundSeqWindow = 4096;
    private static readonly TimeSpan ClosingLingerDuration = TimeSpan.FromSeconds(3);

    private sealed class PeerRudpState
    {
        private readonly HashSet<uint> _receivedSeqs = [];
        private uint _maxReceivedSeq;

        public PeerRudpState(IPEndPoint endpoint, DateTimeOffset now)
        {
            Endpoint = endpoint;
            LastInboundAt = now;
            LastAckFlushAt = now;
        }

        public IPEndPoint Endpoint { get; }
        public DateTimeOffset LastInboundAt { get; set; }
        public DateTimeOffset LastAckFlushAt { get; set; }
        public DateTimeOffset? ClosingExpireAt { get; set; }
        public List<uint> PendingAcks { get; } = [];
        public SortedDictionary<uint, UnackedPacket> UnackedOutbound { get; } = [];

        public bool TryMarkReceived(uint seq)
        {
            if (_maxReceivedSeq > MaxInboundSeqWindow && seq < _maxReceivedSeq - MaxInboundSeqWindow)
            {
                return false;
            }

            if (!_receivedSeqs.Add(seq))
            {
                return false;
            }

            if (seq > _maxReceivedSeq)
            {
                _maxReceivedSeq = seq;
            }

            if (_receivedSeqs.Count > MaxInboundSeqWindow + 512)
            {
                uint cutoff = _maxReceivedSeq - MaxInboundSeqWindow;
                _receivedSeqs.RemoveWhere(s => s < cutoff);
            }

            return true;
        }

        public void ResetForFreshConnect(DateTimeOffset now)
        {
            _receivedSeqs.Clear();
            _maxReceivedSeq = 0;
            PendingAcks.Clear();
            UnackedOutbound.Clear();
            ClosingExpireAt = null;
            LastInboundAt = now;
            LastAckFlushAt = now;
        }
    }

    private sealed class UnackedPacket(byte[] frame, DateTimeOffset createdAt)
    {
        public byte[] Frame { get; } = frame;
        public DateTimeOffset LastSentAt { get; set; } = createdAt;
        public int RetryCount { get; set; }
    }

    private readonly MmaServerEngine _engine;
    private readonly TimeProvider _timeProvider;
    private readonly ServerTimingOptions _timings;
    private readonly Dictionary<IPEndPoint, PeerRudpState> _rudpPeers = [];
    private uint _nextOutboundSeq;

    public GmRudpChannelHost(
        MmaServerEngine engine,
        TimeProvider? timeProvider = null,
        ServerTimingOptions? timings = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timings = timings ?? ServerTimingOptions.Default;
    }

    public MmaServerEngine Engine => _engine;

    public uint CurrentOutboundSequence => _nextOutboundSeq;

    public bool IsKnownRudpPeer(IPEndPoint endpoint) => _rudpPeers.ContainsKey(endpoint);

    public IReadOnlyList<OutboundDatagram> ProcessControlDatagram(IPEndPoint remoteEndPoint, ReadOnlyMemory<byte> datagram)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (!GmRudpCodec.TryDecode(datagram, out GmRudpFrame frame))
        {
            // Never accept corrupt / unframed packets from peers known to speak GM RUDP.
            if (_rudpPeers.ContainsKey(remoteEndPoint))
            {
                return [];
            }

            // Raw GML fallback (e.g., short legacy diagnostic/probe datagrams with < 12 bytes).
            return WrapEngineOutbound(_engine.HandleControlDatagram(remoteEndPoint, datagram), now);
        }

        if (!_rudpPeers.TryGetValue(remoteEndPoint, out PeerRudpState? peer))
        {
            peer = new PeerRudpState(remoteEndPoint, now);
            _rudpPeers[remoteEndPoint] = peer;
        }

        peer.LastInboundAt = now;

        if (frame.Kind == GmRudpKind.Ack)
        {
            foreach (uint ackedSeq in frame.AckedSequences)
            {
                peer.UnackedOutbound.Remove(ackedSeq);
            }

            // GM peers treat kind=2 (ACK) sequence numbers as reliable too and retransmit every 3s until ACKed.
            QueuePendingAck(peer, frame.SequenceNumber);

            var ackOutbound = new List<OutboundDatagram>();
            bool isNewAck = peer.TryMarkReceived(frame.SequenceNumber);
            if (!isNewAck || peer.PendingAcks.Count >= GmRudpCodec.MaxAckEntriesPerPacket)
            {
                FlushAcksForPeer(peer, ackOutbound, now);
            }

            if (peer.ClosingExpireAt is not null && peer.UnackedOutbound.Count == 0 && peer.PendingAcks.Count == 0)
            {
                _rudpPeers.Remove(remoteEndPoint);
            }

            return ackOutbound;
        }

        // Handle a restarted GM client reconnecting from seq=1 on the same (IP, port) after a previous session ended.
        if (frame.SequenceNumber == 1 && IsFreshConnectPayload(frame.Payload.Span) && !IsActiveEnginePeer(remoteEndPoint))
        {
            peer.ResetForFreshConnect(now);
        }

        QueuePendingAck(peer, frame.SequenceNumber);

        var outbound = new List<OutboundDatagram>();
        if (!peer.TryMarkReceived(frame.SequenceNumber))
        {
            // Duplicate DATA retransmit: send ACK right away so the peer clears its retransmit buffer.
            FlushAcksForPeer(peer, outbound, now);
            return outbound;
        }

        IReadOnlyList<OutboundDatagram> engineOutbound = _engine.HandleControlDatagram(remoteEndPoint, frame.Payload);
        AppendWrappedOutbound(engineOutbound, outbound, now);

        if (peer.PendingAcks.Count >= GmRudpCodec.MaxAckEntriesPerPacket)
        {
            FlushAcksForPeer(peer, outbound, now);
        }

        UpdateClosingPeersAfterEngineOutput(engineOutbound, now);
        return outbound;
    }

    public IReadOnlyList<OutboundDatagram> ProcessInputDatagram(IPEndPoint remoteEndPoint, ReadOnlyMemory<byte> datagram)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        IReadOnlyList<OutboundDatagram> engineOutbound = _engine.HandleInputDatagram(remoteEndPoint, datagram);
        return WrapEngineOutbound(engineOutbound, now);
    }

    public IReadOnlyList<OutboundDatagram> Tick()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        IReadOnlyList<OutboundDatagram> engineOutbound = _engine.Tick();
        var outbound = new List<OutboundDatagram>(engineOutbound.Count);
        AppendWrappedOutbound(engineOutbound, outbound, now);
        UpdateClosingPeersAfterEngineOutput(engineOutbound, now);

        TimeSpan retransmitInterval = _timings.EffectiveRudpRetransmitInterval;
        TimeSpan ackFlushInterval = _timings.EffectiveRudpAckFlushInterval;

        List<IPEndPoint>? expiredPeers = null;
        foreach (PeerRudpState peer in _rudpPeers.Values)
        {
            if (peer.PendingAcks.Count > 0 && now - peer.LastAckFlushAt >= ackFlushInterval)
            {
                FlushAcksForPeer(peer, outbound, now);
            }

            List<uint>? abandonedSeqs = null;
            foreach (KeyValuePair<uint, UnackedPacket> kvp in peer.UnackedOutbound)
            {
                UnackedPacket pending = kvp.Value;
                if (now - pending.LastSentAt >= retransmitInterval)
                {
                    if (pending.RetryCount >= _timings.RudpMaxRetries)
                    {
                        abandonedSeqs ??= [];
                        abandonedSeqs.Add(kvp.Key);
                        continue;
                    }

                    pending.RetryCount++;
                    pending.LastSentAt = now;
                    outbound.Add(new OutboundDatagram(UdpTransportSocket.Control, peer.Endpoint, pending.Frame));
                }
            }

            if (abandonedSeqs is not null)
            {
                foreach (uint seq in abandonedSeqs)
                {
                    peer.UnackedOutbound.Remove(seq);
                }
            }

            if (peer.ClosingExpireAt is not null &&
                (now >= peer.ClosingExpireAt.Value || (peer.UnackedOutbound.Count == 0 && peer.PendingAcks.Count == 0)))
            {
                expiredPeers ??= [];
                expiredPeers.Add(peer.Endpoint);
            }
        }

        if (expiredPeers is not null)
        {
            foreach (IPEndPoint endpoint in expiredPeers)
            {
                _rudpPeers.Remove(endpoint);
            }
        }

        return outbound;
    }

    private IReadOnlyList<OutboundDatagram> WrapEngineOutbound(IReadOnlyList<OutboundDatagram> engineOutbound, DateTimeOffset now)
    {
        if (engineOutbound.Count == 0)
        {
            return engineOutbound;
        }

        var wrapped = new List<OutboundDatagram>(engineOutbound.Count);
        AppendWrappedOutbound(engineOutbound, wrapped, now);
        return wrapped;
    }

    private void AppendWrappedOutbound(
        IReadOnlyList<OutboundDatagram> engineOutbound,
        List<OutboundDatagram> destination,
        DateTimeOffset now)
    {
        for (int i = 0; i < engineOutbound.Count; i++)
        {
            OutboundDatagram dgram = engineOutbound[i];
            if (dgram.Socket == UdpTransportSocket.Control &&
                _rudpPeers.TryGetValue(dgram.Target, out PeerRudpState? peer))
            {
                uint seq = NextSequenceNumber();
                byte[] rudpBytes = GmRudpCodec.EncodeData(seq, dgram.Payload);
                peer.UnackedOutbound[seq] = new UnackedPacket(rudpBytes, now);
                destination.Add(new OutboundDatagram(UdpTransportSocket.Control, dgram.Target, rudpBytes));
            }
            else
            {
                destination.Add(dgram);
            }
        }
    }

    private void UpdateClosingPeersAfterEngineOutput(IReadOnlyList<OutboundDatagram> engineOutbound, DateTimeOffset now)
    {
        for (int i = 0; i < engineOutbound.Count; i++)
        {
            OutboundDatagram dgram = engineOutbound[i];
            if (dgram.Socket != UdpTransportSocket.Control || dgram.Payload.Length < MmaPacketHeader.ByteSize)
            {
                continue;
            }

            var type = (MmaPacketType)dgram.Payload[2];
            if (type is MmaPacketType.DisallowConnect or MmaPacketType.LobbyClosed &&
                _rudpPeers.TryGetValue(dgram.Target, out PeerRudpState? peer))
            {
                peer.ClosingExpireAt = now + ClosingLingerDuration;
            }
        }
    }

    private static void QueuePendingAck(PeerRudpState peer, uint seq)
    {
        if (!peer.PendingAcks.Contains(seq))
        {
            peer.PendingAcks.Add(seq);
        }
    }

    private void FlushAcksForPeer(PeerRudpState peer, List<OutboundDatagram> destination, DateTimeOffset now)
    {
        if (peer.PendingAcks.Count == 0)
        {
            return;
        }

        int offset = 0;
        while (offset < peer.PendingAcks.Count)
        {
            int count = Math.Min(GmRudpCodec.MaxAckEntriesPerPacket, peer.PendingAcks.Count - offset);
            uint[] chunk = new uint[count];
            peer.PendingAcks.CopyTo(offset, chunk, 0, count);
            byte[] ackPacket = GmRudpCodec.EncodeAck(NextSequenceNumber(), chunk);
            destination.Add(new OutboundDatagram(UdpTransportSocket.Control, peer.Endpoint, ackPacket));
            offset += count;
        }

        peer.PendingAcks.Clear();
        peer.LastAckFlushAt = now;
    }

    private bool IsActiveEnginePeer(IPEndPoint remoteEndPoint)
    {
        // Active connected players remain tracked by MmaServerEngine; once disconnected/reset, allow seq=1 reconnect.
        return _engine.ConnectedPlayerCount > 0 && _engine.IsControlEndpointRegistered(remoteEndPoint);
    }

    private static bool IsFreshConnectPayload(ReadOnlySpan<byte> payload) =>
        payload.Length >= MmaPacketHeader.ByteSize && payload[2] == (byte)MmaPacketType.Connect;

    private uint NextSequenceNumber()
    {
        _nextOutboundSeq++;
        if (_nextOutboundSeq == 0)
        {
            _nextOutboundSeq = 1;
        }

        return _nextOutboundSeq;
    }
}
