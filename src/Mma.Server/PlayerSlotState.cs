using System.Net;
using Mma.Protocol;

namespace Mma.Server;

internal enum PeerInputTransport
{
    Control = 0,
    FastInput = 1
}

internal sealed class PlayerSlotState
{
    public required byte Slot { get; set; }
    public required IPAddress Ip { get; init; }
    public required int ControlPort { get; init; }
    public int InputPort { get; set; }
    public bool HasObservedInputPort { get; set; }
    public PeerInputTransport? ObservedInputTransport { get; set; }

    public string Name { get; set; } = string.Empty;
    public string CloudId { get; set; } = string.Empty;
    public short Character { get; set; }
    public byte Color { get; set; }
    public uint CustomPrimaryColor { get; set; }
    public uint CustomSecondaryColor { get; set; }
    public byte Skin { get; set; }
    public string Stage { get; set; } = string.Empty;
    public byte SlideDownJump { get; set; }
    public bool CheatEnabled { get; set; }

    public bool Ready { get; set; }
    public string? LevelChoice { get; set; }
    public byte HandicapStock { get; set; }
    public byte HandicapHealth { get; set; }
    public List<ushort> Squad { get; } = [];

    public SortedDictionary<uint, uint> CachedInputs { get; } = [];
    public uint LatestClientMatchFrame { get; set; }
    public uint? LastRelayedClientMatchFrame { get; set; }
    public DateTimeOffset? LastRelayedAt { get; set; }
    public int LastRelayDigest { get; set; }

    public bool Forfeited { get; set; }
    public uint? ForfeitFrame { get; set; }
    public bool ReturnedToLobby { get; set; }

    public DateTimeOffset LastLobbyActivity { get; set; }
    public DateTimeOffset LastMatchActivity { get; set; }
    public DateTimeOffset LastHeartbeatSentAt { get; set; }

    public PlayerSummary ToSummary() => new(
        Name,
        Character,
        Color,
        CustomPrimaryColor,
        CustomSecondaryColor,
        Skin,
        Stage,
        Ready,
        SlideDownJump,
        CheatEnabled);
}
