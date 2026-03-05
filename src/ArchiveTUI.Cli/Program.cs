using ArchiveTUI.Application;
using ArchiveTUI.Application.Ports;
using ArchiveTUI.Application.Services;
using ArchiveTUI.Application.UseCases;
using ArchiveTUI.Cli.Adapters;
using ArchiveTUI.Infrastructure.Adapters;
using Npgsql;
using System.Threading;

var options = ParseArguments(args);
if (options is null)
{
    PrintUsage();
    return;
}

string connectionString;
(string ConnectionHost, int ConnectionPort, string DatabaseName, string DatabaseInfo, string LegacySqlitePath, string Username, string Password, string SslMode) dbConfig;
try
{
    dbConfig = ReadDatabaseConfiguration();
    connectionString = BuildConnectionString(dbConfig);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    PrintUsage();
    return;
}

var filePort = new FileSystemAdapter();
var databasePort = new PostgresMetadataRepository(connectionString, dbConfig.LegacySqlitePath);
var fileWatcherPort = new FileWatcherAdapter();
var settingsPort = (ISettingsPort)databasePort;

var fileService = new FileService(filePort);
var metadataService = new MetadataService(filePort, databasePort);
var backupManager = new BackupManager(fileService, metadataService);

await databasePort.InitializeAsync();
var persistedSettings = await settingsPort.LoadAsync();
var defaultSource = string.IsNullOrWhiteSpace(options.Value.SourceRoot)
    ? persistedSettings?.SourceRoot ?? string.Empty
    : options.Value.SourceRoot;
var defaultDestinations = options.Value.DestinationRoots.Any()
    ? options.Value.DestinationRoots
    : persistedSettings?.DestinationRoots ?? Array.Empty<string>();
var defaultSamplePercent = options.Value.VerifySamplePercent != 2
    ? options.Value.VerifySamplePercent
    : persistedSettings?.VerifySamplePercent ?? 2;
var defaultAgeVerify = options.Value.AgeVerificationEnabled ?? persistedSettings?.AgeVerificationEnabled ?? true;

if (options.Value.Headless)
{
    if (string.IsNullOrWhiteSpace(defaultSource) || !defaultDestinations.Any())
    {
        PrintUsage();
        return;
    }

    await RunHeadlessAsync(backupManager, settingsPort, fileWatcherPort, (defaultSource, defaultDestinations, options.Value.Headless, options.Value.Watch, defaultSamplePercent, defaultAgeVerify));
    return;
}

var tui = new TuiAdapter(
    backupManager,
    fileWatcherPort,
    defaultSource,
    defaultDestinations,
    defaultSamplePercent,
    defaultAgeVerify,
    settingsPort,
    dbConfig.DatabaseInfo);

tui.Run();

static (string SourceRoot, IReadOnlyCollection<string> DestinationRoots, bool Headless, bool Watch, int VerifySamplePercent, bool? AgeVerificationEnabled)? ParseArguments(string[] args)
{
    var headless = false;
    var watch = false;
    var verifySamplePercent = 2;
    bool? ageVerificationEnabled = null;
    var positional = new List<string>();

    for (var index = 0; index < args.Length; index++)
    {
        if (string.Equals(args[index], "--headless", StringComparison.OrdinalIgnoreCase))
        {
            headless = true;
            continue;
        }

        if (string.Equals(args[index], "--watch", StringComparison.OrdinalIgnoreCase))
        {
            watch = true;
            continue;
        }

        if (string.Equals(args[index], "--disable-age-verify", StringComparison.OrdinalIgnoreCase))
        {
            ageVerificationEnabled = false;
            continue;
        }

        if (string.Equals(args[index], "--enable-age-verify", StringComparison.OrdinalIgnoreCase))
        {
            ageVerificationEnabled = true;
            continue;
        }

        if (string.Equals(args[index], "--verify-sample", StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= args.Length)
            {
                return null;
            }

            if (int.TryParse(args[index + 1], out var parsed))
            {
                verifySamplePercent = Math.Clamp(parsed, 0, 100);
            }
            index++;
            continue;
        }

        positional.Add(args[index]);
    }

    if (positional.Count >= 2)
    {
        return (positional[0], positional.Skip(1).ToArray(), headless, watch, verifySamplePercent, ageVerificationEnabled);
    }

    return (string.Empty, Array.Empty<string>(), headless, watch, verifySamplePercent, ageVerificationEnabled);
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  Interactive TUI: ArchiveTUI.Cli");
    Console.WriteLine("  Headless run:    ArchiveTUI.Cli <sourceRoot> <destination1> [destination2 ...] [--verify-sample <0-100>] [--disable-age-verify] --headless [--watch]");
    Console.WriteLine();
    Console.WriteLine("Environment variables:");
    Console.WriteLine("  ARCHIVETUI_DB_HOST             (default: 127.0.0.1)");
    Console.WriteLine("  ARCHIVETUI_DB_PORT             (default: 5432)");
    Console.WriteLine("  ARCHIVETUI_DB_NAME             (default: archivetui)");
    Console.WriteLine("  ARCHIVETUI_DB_USER             (required)");
    Console.WriteLine("  ARCHIVETUI_DB_PASSWORD         (required)");
    Console.WriteLine("  ARCHIVETUI_DB_SSLMODE          (default: Disable)");
    Console.WriteLine("  ARCHIVETUI_LEGACY_SQLITE_PATH  (default: archive-metadata.db)");
}

static async Task RunHeadlessAsync(IBackupPort backupPort, ISettingsPort settingsPort, IFileWatcherPort watcherPort, (string SourceRoot, IReadOnlyCollection<string> DestinationRoots, bool Headless, bool Watch, int VerifySamplePercent, bool AgeVerificationEnabled) options)
{
    async Task ExecuteBackupAsync(string reason)
    {
        var result = await backupPort.RunAsync(new BackupRequest(options.SourceRoot, options.DestinationRoots, options.VerifySamplePercent, options.AgeVerificationEnabled));
        Console.WriteLine($"[{DateTime.Now:T}] {reason}");
        PrintResult(result, indent: "  ");
        await settingsPort.SaveAsync(options.SourceRoot, options.DestinationRoots, options.VerifySamplePercent, options.AgeVerificationEnabled, CancellationToken.None);
    }

    await ExecuteBackupAsync("Initial backup");

    if (!options.Watch)
    {
        return;
    }

    using var cts = new CancellationTokenSource();
    var runLock = new SemaphoreSlim(1, 1);
    var timerGate = new object();
    Timer? debounceTimer = null;

    void ScheduleRun(string reason)
    {
        lock (timerGate)
        {
            debounceTimer?.Dispose();
            debounceTimer = new Timer(async _ =>
            {
                await runLock.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    await ExecuteBackupAsync(reason).ConfigureAwait(false);
                }
                finally
                {
                    runLock.Release();
                }
            }, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
    }

    using var subscription = watcherPort.Watch(
        options.SourceRoot,
        options.DestinationRoots,
        () => ScheduleRun("Change detected"),
        ex =>
        {
            Console.WriteLine(ex is null
                ? "[watcher] Unknown error; scheduling full rescan."
                : $"[watcher] Error: {ex.Message}; scheduling full rescan.");
            ScheduleRun("Watcher error/rescan");
        });

    Console.WriteLine("Watching for changes. Press Ctrl+C to stop.");
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (TaskCanceledException)
    {
        // exiting
    }
    finally
    {
        debounceTimer?.Dispose();
    }
}

static (string ConnectionHost, int ConnectionPort, string DatabaseName, string DatabaseInfo, string LegacySqlitePath, string Username, string Password, string SslMode) ReadDatabaseConfiguration()
{
    var host = ReadEnv("ARCHIVETUI_DB_HOST", "127.0.0.1");
    var port = ReadIntEnv("ARCHIVETUI_DB_PORT", 5432);
    var dbName = ReadEnv("ARCHIVETUI_DB_NAME", "archivetui");
    var username = ReadRequiredEnv("ARCHIVETUI_DB_USER");
    var password = ReadRequiredEnv("ARCHIVETUI_DB_PASSWORD");
    var sslMode = ReadEnv("ARCHIVETUI_DB_SSLMODE", "Disable");
    var legacySqlitePath = ReadEnv("ARCHIVETUI_LEGACY_SQLITE_PATH", "archive-metadata.db");
    var info = $"{host}:{port}/{dbName}";

    return (host, port, dbName, info, legacySqlitePath, username, password, sslMode);
}

static string BuildConnectionString((string ConnectionHost, int ConnectionPort, string DatabaseName, string DatabaseInfo, string LegacySqlitePath, string Username, string Password, string SslMode) config)
{
    if (!Enum.TryParse<SslMode>(config.SslMode, ignoreCase: true, out var sslMode))
    {
        throw new InvalidOperationException($"Invalid ARCHIVETUI_DB_SSLMODE value '{config.SslMode}'.");
    }

    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = config.ConnectionHost,
        Port = config.ConnectionPort,
        Database = config.DatabaseName,
        Username = config.Username,
        Password = config.Password,
        SslMode = sslMode,
        TrustServerCertificate = sslMode is not SslMode.Disable
    };

    return builder.ConnectionString;
}

static string ReadEnv(string key, string fallback)
{
    var value = Environment.GetEnvironmentVariable(key);
    return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

static int ReadIntEnv(string key, int fallback)
{
    var value = Environment.GetEnvironmentVariable(key);
    return int.TryParse(value, out var parsed) ? parsed : fallback;
}

static string ReadRequiredEnv(string key)
{
    var value = Environment.GetEnvironmentVariable(key);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Missing required environment variable '{key}'.");
    }

    return value.Trim();
}

static void PrintResult(BackupResult result, string indent = "")
{
    var prefix = string.IsNullOrEmpty(indent) ? "" : indent;
    Console.WriteLine($"{prefix}Scanned: {result.ScannedFiles}  Copied: {result.CopiedFiles}  Skipped: {result.SkippedFiles}  Failed: {result.FailedFiles}");

    if (result.Infos.Count > 0)
    {
        Console.WriteLine($"{prefix}Info:");
        foreach (var info in result.Infos)
        {
            Console.WriteLine($"{prefix}- {info}");
        }
    }

    if (result.Errors.Count > 0)
    {
        Console.WriteLine($"{prefix}Errors:");
        foreach (var error in result.Errors)
        {
            Console.WriteLine($"{prefix}- {error}");
        }
    }
}
