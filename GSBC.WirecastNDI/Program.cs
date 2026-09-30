using GSBC.WirecastNDI;
using GSBC.WirecastNDI.Capture;
using GSBC.WirecastNDI.Commands;
using GSBC.WirecastNDI.Configuration;
using GSBC.WirecastNDI.Workers;
using Serilog;

string? command = args.FirstOrDefault()?.ToLowerInvariant();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Started by Task Scheduler with an arbitrary working directory; config lives next to the exe.
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

var config = builder.Configuration.GetSection(BridgeConfig.Section).Get<BridgeConfig>() ?? new BridgeConfig();

switch (command)
{
    case "list-devices":
    {
        ConsoleHelper.AttachToParentConsole();
        string ffmpeg = Ffmpeg.Resolve(config.Capture.FfmpegPath);
        IReadOnlyList<CaptureDevice> devices = await Ffmpeg.ListDevicesAsync(ffmpeg, CancellationToken.None);
        Console.WriteLine($"Capture devices seen by {ffmpeg}:");
        foreach (CaptureDevice d in devices)
            Console.WriteLine($"  [{d.Kind}] {d.Name}");
        if (devices.Count == 0)
            Console.WriteLine("  (none)");
        return 0;
    }

    case "probe":
        ConsoleHelper.AttachToParentConsole();
        return ProbeCommand.Run(args[1..], config.Ndi.LibraryPath);

    case "help" or "--help" or "-h" or "/?":
        ConsoleHelper.AttachToParentConsole();
        Console.WriteLine("""
            GSBC.WirecastNDI - publishes Wirecast's virtual camera + microphone as an NDI source.

              GSBC.WirecastNDI                  run the bridge (what the logon task does)
              GSBC.WirecastNDI list-devices     show capture devices ffmpeg can see
              GSBC.WirecastNDI probe [name] [--seconds N]
                                                list NDI sources; receive from one and report
                                                resolution / fps / audio level

            Settings: appsettings.json next to the exe. Logs: see Logging:Directory in that file.
            """);
        return 0;
}

// Two copies would publish two NDI sources with the same name and fight over the devices.
using var singleInstance = new Mutex(initiallyOwned: true, @"Global\GSBC.WirecastNDI", out bool isFirstInstance);
if (!isFirstInstance)
    return 0;

string logDirectory = builder.Configuration["Logging:Directory"] is { Length: > 0 } dir
    ? Environment.ExpandEnvironmentVariables(dir)
    : DefaultLogDirectory();

builder.Services.AddSerilog(logger => logger
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(logDirectory, "wirecastndi-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"));

builder.Services.Configure<BridgeConfig>(builder.Configuration.GetSection(BridgeConfig.Section));
builder.Services.AddHostedService<BridgeWorker>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));

using IHost host = builder.Build();
var log = host.Services.GetRequiredService<ILogger<Program>>();
log.LogInformation("GSBC.WirecastNDI {Version} starting, logging to {Directory}",
    typeof(Program).Assembly.GetName().Version, logDirectory);

try
{
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    // Non-zero exit so Task Scheduler's restart-on-failure kicks in.
    log.LogCritical(ex, "Fatal error");
    return 1;
}

static string DefaultLogDirectory() => OperatingSystem.IsWindows()
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GSBC.WirecastNDI", "logs")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GSBC.WirecastNDI", "logs");
