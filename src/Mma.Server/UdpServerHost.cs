using System.Net;
using System.Net.Sockets;
using System.Text;
using Mma.Protocol;

namespace Mma.Server;

/// <summary>
/// Cross-platform async dual-socket IPv4 UDP host for Mega Man Arena headless sessions.
/// Binds Port (control/RUDP + MMA_PING) and Port + 1 (fast input UDP).
/// </summary>
public sealed class UdpServerHost : IDisposable
{
    private const int SioUdpConnReset = -1744830452; // 0x9800000C
    private const int EphemeralPairMaxAttempts = 64;

    private readonly ServerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ServerTimingOptions _timings;
    private readonly TextWriter _logWriter;
    private readonly Socket _controlSocket;
    private readonly Socket _inputSocket;
    private readonly byte[] _pongPayload;
    private readonly object _engineLock = new();
    private bool _disposed;

    public UdpServerHost(ServerOptions options, TextWriter? logWriter = null)
        : this(options, timeProvider: null, timings: null, logWriter: logWriter)
    {
    }

    public UdpServerHost(
        ServerOptions options,
        TimeProvider? timeProvider,
        ServerTimingOptions? timings = null,
        TextWriter? logWriter = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Port < 0 || options.Port >= ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Port must be between 0 and {ushort.MaxValue - 1} so Port + 1 fits in valid UDP range.");
        }

        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timings = timings ?? ServerTimingOptions.Default;
        _logWriter = logWriter ?? Console.Out;
        _pongPayload = Encoding.ASCII.GetBytes($"{MmaConstants.ProbePongPrefix}:{options.Mode}:{options.MaxPlayers}");

        (_controlSocket, _inputSocket, int boundPort, int boundInputPort) = BindDualSockets(options.BindAddress, options.Port);
        BoundPort = boundPort;
        BoundInputPort = boundInputPort;

        Engine = new MmaServerEngine(options, _timeProvider, _timings, _logWriter);
        RudpChannel = new GmRudpChannelHost(Engine, _timeProvider, _timings);
    }

    public int BoundPort { get; }

    public int BoundInputPort { get; }

    public MmaServerEngine Engine { get; }

    public GmRudpChannelHost RudpChannel { get; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Task controlTask = RunReceiveLoopAsync(_controlSocket, isControlSocket: true, cancellationToken);
        Task inputTask = RunReceiveLoopAsync(_inputSocket, isControlSocket: false, cancellationToken);
        Task tickTask = RunTickLoopAsync(cancellationToken);
        await Task.WhenAll(controlTask, inputTask, tickTask);
    }

    /// <summary>
    /// Executes one deterministic timer tick and dispatches any produced datagrams.
    /// </summary>
    public async Task TickOnceAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OutboundDatagram> outbound;
        lock (_engineLock)
        {
            outbound = RudpChannel.Tick();
            _logWriter.Flush();
        }

        await SendDatagramsAsync(outbound, cancellationToken);
    }

    private async Task RunReceiveLoopAsync(
        Socket socket,
        bool isControlSocket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEndPoint, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || _disposed)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;
            }

            if (result.RemoteEndPoint is not IPEndPoint ipEndPoint)
            {
                continue;
            }

            var payload = buffer.AsMemory(0, result.ReceivedBytes);
            if (isControlSocket && IsProbePing(payload.Span))
            {
                await _controlSocket.SendToAsync(_pongPayload, SocketFlags.None, ipEndPoint, cancellationToken);
                Log($"Health probe {MmaConstants.ProbePing} from {ipEndPoint}; replied {MmaConstants.ProbePongPrefix}:{_options.Mode}:{_options.MaxPlayers}");
                continue;
            }

            IReadOnlyList<OutboundDatagram> outbound;
            lock (_engineLock)
            {
                outbound = isControlSocket
                    ? RudpChannel.ProcessControlDatagram(ipEndPoint, payload)
                    : RudpChannel.ProcessInputDatagram(ipEndPoint, payload);
                _logWriter.Flush();
            }

            await SendDatagramsAsync(outbound, cancellationToken);
        }
    }

    private async Task RunTickLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_timings.TickInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await TickOnceAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task SendDatagramsAsync(
        IReadOnlyList<OutboundDatagram> outbound,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < outbound.Count; i++)
        {
            OutboundDatagram datagram = outbound[i];
            Socket socket = datagram.Socket == UdpTransportSocket.Control ? _controlSocket : _inputSocket;
            try
            {
                await socket.SendToAsync(datagram.Payload, SocketFlags.None, datagram.Target, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || _disposed)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Ignore transient ICMP unreachable responses from a disconnected UDP client.
            }
        }
    }

    private static (Socket ControlSocket, Socket InputSocket, int ControlPort, int InputPort) BindDualSockets(
        IPAddress bindAddress,
        int requestedPort)
    {
        if (requestedPort > 0)
        {
            Socket control = CreateConfiguredUdpSocket();
            Socket? input = null;
            try
            {
                control.Bind(new IPEndPoint(bindAddress, requestedPort));
                int controlPort = ((IPEndPoint)control.LocalEndPoint!).Port;
                input = CreateConfiguredUdpSocket();
                input.Bind(new IPEndPoint(bindAddress, controlPort + 1));
                int inputPort = ((IPEndPoint)input.LocalEndPoint!).Port;
                return (control, input, controlPort, inputPort);
            }
            catch
            {
                input?.Dispose();
                control.Dispose();
                throw;
            }
        }

        for (int attempt = 0; attempt < EphemeralPairMaxAttempts; attempt++)
        {
            Socket control = CreateConfiguredUdpSocket();
            Socket? input = null;
            try
            {
                control.Bind(new IPEndPoint(bindAddress, 0));
                int candidatePort = ((IPEndPoint)control.LocalEndPoint!).Port;
                if (candidatePort >= ushort.MaxValue)
                {
                    control.Dispose();
                    continue;
                }

                input = CreateConfiguredUdpSocket();
                input.Bind(new IPEndPoint(bindAddress, candidatePort + 1));
                int inputPort = ((IPEndPoint)input.LocalEndPoint!).Port;
                return (control, input, candidatePort, inputPort);
            }
            catch (SocketException)
            {
                input?.Dispose();
                control.Dispose();
            }
            catch
            {
                input?.Dispose();
                control.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException(
            $"Failed to bind contiguous ephemeral dual UDP ports after {EphemeralPairMaxAttempts} attempts.");
    }

    private static Socket CreateConfiguredUdpSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            // Ignore ICMP Port Unreachable errors on Windows UDP sockets.
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }

        return socket;
    }

    private static bool IsProbePing(ReadOnlySpan<byte> span)
    {
        ReadOnlySpan<byte> pingBytes = "MMA_PING"u8;
        return span.SequenceEqual(pingBytes);
    }

    private void Log(string message)
    {
        lock (_engineLock)
        {
            _logWriter.WriteLine($"[{_timeProvider.GetUtcNow():O}] {message}");
            _logWriter.Flush();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controlSocket.Dispose();
        _inputSocket.Dispose();
    }
}
