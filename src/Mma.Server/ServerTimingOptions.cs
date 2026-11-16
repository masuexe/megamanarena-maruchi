namespace Mma.Server;

/// <summary>
/// Configurable timing thresholds for lobby deadlines, match stall detection, keepalives, and GM RUDP reliability.
/// </summary>
public sealed record ServerTimingOptions(
    TimeSpan StartingMatchTimeout,
    TimeSpan MatchStallTimeout,
    TimeSpan TickInterval,
    TimeSpan LobbyHeartbeatInterval,
    TimeSpan FreezeRetransmitCooldown,
    TimeSpan? RudpRetransmitInterval = null,
    TimeSpan? RudpAckFlushInterval = null,
    int RudpMaxRetries = 12)
{
    public TimeSpan EffectiveRudpRetransmitInterval => RudpRetransmitInterval ?? TimeSpan.FromMilliseconds(1200);

    public TimeSpan EffectiveRudpAckFlushInterval => RudpAckFlushInterval ?? TimeSpan.FromMilliseconds(100);

    public static ServerTimingOptions Default { get; } = new(
        StartingMatchTimeout: TimeSpan.FromSeconds(10),
        MatchStallTimeout: TimeSpan.FromSeconds(15),
        TickInterval: TimeSpan.FromMilliseconds(16),
        LobbyHeartbeatInterval: TimeSpan.FromSeconds(3),
        FreezeRetransmitCooldown: TimeSpan.FromMilliseconds(12),
        RudpRetransmitInterval: TimeSpan.FromMilliseconds(1200),
        RudpAckFlushInterval: TimeSpan.FromMilliseconds(100),
        RudpMaxRetries: 12);
}
