using System.Net;
using Mma.Protocol;

namespace Mma.Server;

/// <summary>
/// Server-side authoritative deadline timeline for 5..8 player Arena8 matches (Packet 240).
/// Advances frame commits at 60 Hz once match input begins, filling missed player proposals
/// with neutral inputs (0) and serving ring-buffer repair batches so single-peer lag never freezes the room.
/// </summary>
internal sealed class DeadlineTimelineManager
{
    private const double FrameRateHz = 60.0;
    private const double MaxPausedBudget = 1.5;
    private static readonly TimeSpan HeartbeatBroadcastInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RetiredRetentionDuration = TimeSpan.FromSeconds(18);

    private sealed class PendingRow
    {
        public PendingRow(int playerCount)
        {
            Masks = new long[playerCount];
            Array.Fill(Masks, -1L);
        }

        public uint FrameId { get; set; }
        public long[] Masks { get; }

        public void Reset(uint frameId)
        {
            FrameId = frameId;
            Array.Fill(Masks, -1L);
        }
    }

    private sealed record RetiredPhaseState(
        ushort Phase,
        uint StartFrame,
        uint LastFrame,
        DeadlineFrameSnapshot?[] Frames,
        DateTimeOffset ExpiresAt);

    private readonly bool[] _departed;
    private readonly uint[] _ack;
    private readonly PendingRow?[] _pending = new PendingRow?[MmaConstants.DeadlineQueueSize];
    private DeadlineFrameSnapshot?[] _frames = new DeadlineFrameSnapshot?[MmaConstants.DeadlineHistorySize];
    private RetiredPhaseState? _retired;

    private DateTimeOffset _lastTickAt;
    private DateTimeOffset _nextBroadcastAt;
    private double _frameBudget;

    public DeadlineTimelineManager(ulong randomSeed, byte buffer, int playerCount, DateTimeOffset now)
    {
        Token = (uint)(randomSeed % 4294967296UL);
        Buffer = buffer;
        PlayerCount = playerCount;
        ConfirmLead = (uint)Math.Max(2, (int)Math.Ceiling(buffer / 3.0));
        ActivePhase = 1;
        Committed = buffer;
        MaxProposedFrame = buffer;
        _departed = new bool[playerCount];
        _ack = new uint[playerCount];
        Array.Fill(_ack, (uint)buffer);
        _lastTickAt = now;
        _nextBroadcastAt = now;
    }

    public uint Token { get; }
    public byte Buffer { get; }
    public int PlayerCount { get; }
    public uint ConfirmLead { get; }
    public ushort ActivePhase { get; private set; }
    public uint Committed { get; private set; }
    public uint MaxProposedFrame { get; private set; }
    public bool HasSeenClientPacket { get; private set; }
    public bool HasAnyDeparted => Array.Exists(_departed, static d => d);

    public bool IsDeparted(byte slot)
    {
        int idx = slot - 1;
        return idx >= 0 && idx < _departed.Length && _departed[idx];
    }

    /// <summary>
    /// Marks a slot as departed so its future proposals are ignored and committed as neutral (0).
    /// Returns true if this changed state from active to departed.
    /// </summary>
    public bool MarkDeparted(byte slot)
    {
        int idx = slot - 1;
        if (idx < 0 || idx >= _departed.Length || _departed[idx])
        {
            return false;
        }

        _departed[idx] = true;
        return true;
    }

    public IReadOnlyList<OutboundDatagram> HandleProposal(
        PlayerSlotState sender,
        ReadOnlySpan<byte> fullDatagram,
        ushort sessionId,
        DateTimeOffset now)
    {
        if (!DeadlineProposalPacket.TryReadPayload(fullDatagram, out DeadlineProposalPacket? packet) || packet is null)
        {
            return [];
        }

        if (packet.Token != Token)
        {
            return [];
        }

        if (packet.Phase < ActivePhase)
        {
            if (_retired is { } retired &&
                packet.Phase == retired.Phase &&
                now < retired.ExpiresAt &&
                packet.AckFrame <= retired.LastFrame)
            {
                uint oldest = retired.LastFrame >= (uint)MmaConstants.DeadlineHistorySize
                    ? (retired.LastFrame - (uint)MmaConstants.DeadlineHistorySize) + 1u
                    : retired.StartFrame;
                uint first = Math.Max(retired.StartFrame, Math.Max(oldest, packet.AckFrame + 1u));
                uint end = first <= retired.LastFrame ? Math.Min(first + 19u, retired.LastFrame) : retired.LastFrame;
                byte[] replyBytes = EncodeBatch(
                    retired.Frames,
                    Token,
                    retired.Phase,
                    retired.LastFrame,
                    PlayerCount,
                    sessionId,
                    first,
                    end);
                return
                [
                    new OutboundDatagram(
                        UdpTransportSocket.Control,
                        new IPEndPoint(sender.Ip, sender.ControlPort),
                        replyBytes)
                ];
            }

            return [];
        }

        if (packet.Phase == (ushort)(ActivePhase + 1) && ActivePhase < ushort.MaxValue)
        {
            PromoteToPhase(packet.Phase, now);
        }
        else if (packet.Phase != ActivePhase)
        {
            return [];
        }

        int slotIndex = sender.Slot - 1;
        if (slotIndex < 0 || slotIndex >= PlayerCount || _departed[slotIndex] || packet.AckFrame > Committed)
        {
            return [];
        }

        HasSeenClientPacket = true;
        if (packet.AckFrame > _ack[slotIndex])
        {
            _ack[slotIndex] = packet.AckFrame;
        }

        uint maxAcceptable = Committed + (uint)(MmaConstants.DeadlineQueueSize - 1);
        for (int i = 0; i < packet.Proposals.Count; i++)
        {
            InputFrameEntry proposal = packet.Proposals[i];
            if (proposal.FrameId <= Committed || proposal.FrameId > maxAcceptable)
            {
                continue;
            }

            bool wasPreStart = MaxProposedFrame <= Buffer;
            if (proposal.FrameId > MaxProposedFrame)
            {
                MaxProposedFrame = proposal.FrameId;
                if (wasPreStart)
                {
                    _lastTickAt = now;
                    _frameBudget = ConfirmLead;
                }
            }

            int ringIndex = (int)(proposal.FrameId % MmaConstants.DeadlineQueueSize);
            PendingRow? row = _pending[ringIndex];
            if (row is null)
            {
                row = new PendingRow(PlayerCount);
                _pending[ringIndex] = row;
            }

            if (row.FrameId != proposal.FrameId)
            {
                row.Reset(proposal.FrameId);
            }

            if (row.Masks[slotIndex] == -1L)
            {
                row.Masks[slotIndex] = proposal.Bitmask;
            }
        }

        return [];
    }

    public List<OutboundDatagram> Tick(
        IReadOnlyList<PlayerSlotState> players,
        ushort sessionId,
        DateTimeOffset now,
        out uint committedFrame)
    {
        if (!HasSeenClientPacket || players.Count == 0)
        {
            committedFrame = Committed;
            return [];
        }

        if (MaxProposedFrame > Buffer)
        {
            double elapsedSeconds = Math.Max(0.0, (now - _lastTickAt).TotalSeconds);
            _frameBudget += elapsedSeconds * FrameRateHz;
        }

        _lastTickAt = now;

        bool advanced = false;
        while (Committed < MaxProposedFrame && _frameBudget >= 1.0)
        {
            _frameBudget -= 1.0;
            uint frame = ++Committed;
            var masks = new uint[PlayerCount];
            PendingRow? row = _pending[frame % MmaConstants.DeadlineQueueSize];
            if (row is not null && row.FrameId == frame)
            {
                for (int slot = 0; slot < PlayerCount; slot++)
                {
                    if (!_departed[slot] && row.Masks[slot] >= 0)
                    {
                        masks[slot] = (uint)row.Masks[slot];
                    }
                }
            }

            _frames[frame % MmaConstants.DeadlineHistorySize] = new DeadlineFrameSnapshot(frame, masks);
            advanced = true;
        }

        if (Committed >= MaxProposedFrame && _frameBudget > MaxPausedBudget)
        {
            _frameBudget = MaxPausedBudget;
        }

        committedFrame = Committed;

        if (!advanced && now < _nextBroadcastAt)
        {
            return [];
        }

        _nextBroadcastAt = now + HeartbeatBroadcastInterval;

        uint last = Committed;
        uint minFrame = (uint)Buffer + 1u;
        uint recent = Math.Max(minFrame, last >= 7u ? last - 7u : minFrame);
        uint oldest = Math.Max(
            minFrame,
            last >= (uint)MmaConstants.DeadlineHistorySize ? (last - (uint)MmaConstants.DeadlineHistorySize) + 1u : minFrame);

        byte[] recentBatch = EncodeBatch(_frames, Token, ActivePhase, last, PlayerCount, sessionId, recent, last);
        var outbound = new List<OutboundDatagram>(PlayerCount);
        int peerLimit = Math.Min(players.Count, PlayerCount);
        for (int peer = 0; peer < peerLimit; peer++)
        {
            if (_departed[peer])
            {
                continue;
            }

            var endpoint = new IPEndPoint(players[peer].Ip, players[peer].ControlPort);
            outbound.Add(new OutboundDatagram(UdpTransportSocket.Control, endpoint, recentBatch));

            uint nextRepair = _ack[peer] + 1u;
            if (nextRepair >= oldest && nextRepair < recent)
            {
                uint repairEnd = Math.Min(nextRepair + 19u, recent - 1u);
                byte[] repairBatch = EncodeBatch(
                    _frames,
                    Token,
                    ActivePhase,
                    last,
                    PlayerCount,
                    sessionId,
                    nextRepair,
                    repairEnd);
                outbound.Add(new OutboundDatagram(UdpTransportSocket.Control, endpoint, repairBatch));
            }
        }

        return outbound;
    }

    private void PromoteToPhase(ushort newPhase, DateTimeOffset now)
    {
        if (HasSeenClientPacket && Committed > Buffer)
        {
            _retired = new RetiredPhaseState(
                ActivePhase,
                (uint)Buffer + 1u,
                Committed,
                _frames,
                now + RetiredRetentionDuration);
        }

        ActivePhase = newPhase;
        _frames = new DeadlineFrameSnapshot?[MmaConstants.DeadlineHistorySize];
        Array.Clear(_pending);
        Committed = Buffer;
        MaxProposedFrame = Buffer;
        HasSeenClientPacket = false;
        Array.Fill(_ack, (uint)Buffer);
        _frameBudget = 0.0;
        _lastTickAt = now;
        _nextBroadcastAt = now;
    }

    private static byte[] EncodeBatch(
        DeadlineFrameSnapshot?[] ring,
        uint token,
        ushort phase,
        uint highestCommitted,
        int playerCount,
        ushort sessionId,
        uint startFrame,
        uint endFrame)
    {
        int count = endFrame >= startFrame ? (int)(endFrame - startFrame + 1u) : 0;
        var snapshots = new DeadlineFrameSnapshot[count];
        for (int i = 0; i < count; i++)
        {
            uint frame = startFrame + (uint)i;
            DeadlineFrameSnapshot? stored = ring[frame % MmaConstants.DeadlineHistorySize];
            snapshots[i] = stored is { } match && match.FrameId == frame
                ? match
                : new DeadlineFrameSnapshot(frame, new uint[playerCount]);
        }

        var packet = new DeadlineBatchPacket(token, phase, highestCommitted, playerCount, snapshots);
        var writer = new GmlBufferWriter(MmaConstants.DeadlineHeaderSize + (count * (4 + (playerCount * 4))));
        packet.WriteTo(writer, sessionId);
        return writer.ToArray();
    }
}
