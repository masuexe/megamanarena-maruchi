using System.Net;
using System.Net.Sockets;
using System.Text;
using Mma.Protocol;

namespace Mma.Server;

/// <summary>
/// Cross-platform async IPv4 UDP host for Mega Man Arena headless sessions.
/// </summary>
public sealed class UdpServerHost : IDisposable
{
    private const int SioUdpConnReset = -1744830452; // 0x9800000C

    private readonly ServerOptions _options;
    private readonly Socket _socket;
    private readonly TextWriter _logWriter;
    private readonly byte[] _pongPayload;
    private bool _disposed;

    public UdpServerHost(ServerOptions options, TextWriter? logWriter = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logWriter = logWriter ?? Console.Out;
        _pongPayload = Encoding.ASCII.GetBytes($"{MmaConstants.ProbePongPrefix}:{options.Mode}:{options.MaxPlayers}");

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            // Ignore ICMP Port Unreachable errors on Windows UDP sockets.
            _socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }

        _socket.Bind(new IPEndPoint(options.BindAddress, options.Port));
        BoundPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

    public int BoundPort { get; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEndPoint, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }

            var payload = buffer.AsMemory(0, result.ReceivedBytes);
            await HandleDatagramAsync(payload, result.RemoteEndPoint, cancellationToken);
        }
    }

    private async Task HandleDatagramAsync(
        ReadOnlyMemory<byte> payload,
        EndPoint sender,
        CancellationToken cancellationToken)
    {
        if (IsProbePing(payload.Span))
        {
            await _socket.SendToAsync(_pongPayload, SocketFlags.None, sender, cancellationToken);
            Log($"Health probe {MmaConstants.ProbePing} from {sender}; replied {MmaConstants.ProbePongPrefix}:{_options.Mode}:{_options.MaxPlayers}");
            return;
        }

        if (payload.Length >= MmaPacketHeader.ByteSize)
        {
            var reader = new GmlBufferReader(payload);
            var header = MmaPacketHeader.ReadFrom(reader);
            Log($"Datagram from {sender}: sessionId={header.SessionId}, dataType={header.DataType} ({header.KnownType}), length={payload.Length}");
        }
        else
        {
            Log($"Short datagram from {sender}: length={payload.Length}");
        }
    }

    private static bool IsProbePing(ReadOnlySpan<byte> span)
    {
        ReadOnlySpan<byte> pingBytes = "MMA_PING"u8;
        return span.SequenceEqual(pingBytes);
    }

    private void Log(string message)
    {
        _logWriter.WriteLine($"[{DateTimeOffset.UtcNow:O}] {message}");
        _logWriter.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _socket.Dispose();
    }
}
