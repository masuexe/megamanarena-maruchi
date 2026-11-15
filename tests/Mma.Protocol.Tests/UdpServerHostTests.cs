using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Mma.Protocol;
using Mma.Server;

namespace Mma.Protocol.Tests;

public sealed class UdpServerHostTests
{
    [Fact]
    public async Task RespondsToHealthProbe_AndShutsDownCleanly()
    {
        var options = new ServerOptions(IPAddress.Loopback, 0, MmaConstants.DefaultMaxPlayers, MmaConstants.DefaultMode);
        using var logWriter = new StringWriter();
        using var host = new UdpServerHost(options, logWriter);
        Assert.True(host.BoundPort > 0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task serverTask = host.RunAsync(cts.Token);

        using var client = new UdpClient();
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, host.BoundPort);

        byte[] ping = Encoding.ASCII.GetBytes(MmaConstants.ProbePing);
        await client.SendAsync(ping, serverEndPoint, cts.Token);

        UdpReceiveResult response = await client.ReceiveAsync(cts.Token);
        string pong = Encoding.ASCII.GetString(response.Buffer);
        Assert.Equal("MMA_PONG:Arena:8", pong);

        var writer = new GmlBufferWriter();
        new MmaPacketHeader(7087, (byte)MmaPacketType.Connect).WriteTo(writer);
        writer.WriteU8(1);
        await client.SendAsync(writer.WrittenMemory, serverEndPoint, cts.Token);

        // Give the server loop a brief moment to log the second datagram before canceling.
        await Task.Delay(50, cts.Token);
        cts.Cancel();
        await serverTask;

        string logs = logWriter.ToString();
        Assert.Contains("sessionId=7087", logs);
        Assert.Contains("dataType=1 (Connect)", logs);
    }

    [Fact]
    public void ServerOptions_ParsesEnvironmentAndCliOverrides()
    {
        var env = new Dictionary<string, string?>
        {
            ["MMA_BIND"] = "127.0.0.1",
            ["MMA_PORT"] = "6528",
            ["MMA_MAX_PLAYERS"] = "6",
            ["MMA_MODE"] = "Teams"
        };

        var options = ServerOptions.Parse(
            ["--port", "16520", "--max-players=12", "--mode", "Arena"],
            key => env.GetValueOrDefault(key));

        Assert.Equal(IPAddress.Loopback, options.BindAddress);
        Assert.Equal(16520, options.Port);
        Assert.Equal(12, options.MaxPlayers);
        Assert.Equal("Arena", options.Mode);
    }
}
