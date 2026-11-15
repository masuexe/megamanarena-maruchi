using System.Runtime.InteropServices;
using Mma.Server;

ServerOptions options;
try
{
    options = ServerOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    return 1;
}

using var cts = new CancellationTokenSource();

ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};
Console.CancelKeyPress += cancelHandler;

using var sigtermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    cts.Cancel();
});

try
{
    using var host = new UdpServerHost(options);
    Console.WriteLine(
        $"MMA Headless Server listening on UDP {options.BindAddress}:{host.BoundPort} (mode={options.Mode}, maxPlayers={options.MaxPlayers})");
    Console.Out.Flush();

    await host.RunAsync(cts.Token);

    Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] MMA Headless Server stopped cleanly.");
    Console.Out.Flush();
    return 0;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
