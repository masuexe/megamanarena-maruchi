using System.Runtime.InteropServices;
using Mma.Server;

if (ServerOptions.IsHelpRequested(args))
{
    Console.WriteLine(ServerOptions.GetHelpText());
    return 0;
}

ServerOptions options;
string? activeConfigPath;
try
{
    string? defaultConfigPath = ServerOptions.ResolveDefaultConfigPath();
    options = ServerOptions.Parse(args, defaultConfigPath: defaultConfigPath);
    activeConfigPath = ServerOptions.ResolveConfigPath(args, defaultConfigPath: defaultConfigPath);
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

using PosixSignalRegistration? sigtermRegistration = TryRegisterSignal(PosixSignal.SIGTERM, cts);
using PosixSignalRegistration? sighupRegistration = OperatingSystem.IsWindows()
    ? TryRegisterSignal(PosixSignal.SIGHUP, cts)
    : null;

try
{
    if (activeConfigPath is not null)
    {
        Console.WriteLine($"Loaded configuration from {activeConfigPath}");
    }

    using var host = new UdpServerHost(options);
    Console.WriteLine(
        $"MMA Headless Server listening on UDP {options.BindAddress}:{host.BoundPort} (inputPort={host.BoundInputPort}, mode={options.Mode}, maxPlayers={options.MaxPlayers}, stage={options.ResolvedStage}, buffer={options.Buffer}, protocol={(options.UsesArena8Protocol ? "Arena8" : "Stock4P")})");
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

static PosixSignalRegistration? TryRegisterSignal(PosixSignal signal, CancellationTokenSource cts)
{
    try
    {
        return PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            cts.Cancel();
        });
    }
    catch (PlatformNotSupportedException)
    {
        return null;
    }
}
