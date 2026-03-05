using ArchiveTUI.Application;
using ArchiveTUI.Application.Ports;
using Terminal.Gui;
using System.Threading;
using GuiApp = Terminal.Gui.Application;

namespace ArchiveTUI.Cli.Adapters;

public sealed class TuiAdapter
{
    private readonly IBackupPort _backupPort;
    private readonly IFileWatcherPort _fileWatcherPort;
    private readonly ISettingsPort _settingsPort;
    private readonly string _defaultSource;
    private readonly string _defaultDestinations;
    private readonly string _databaseInfo;
    private readonly int _defaultSamplePercent;
    private readonly bool _defaultAgeVerify;
    private IDisposable? _watchSubscription;
    private Timer? _watchDebounce;
    private int _lastSamplePercent;
    private bool _lastAgeVerify;

    public TuiAdapter(
        IBackupPort backupPort,
        IFileWatcherPort fileWatcherPort,
        string defaultSource,
        IReadOnlyCollection<string> defaultDestinations,
        int defaultSamplePercent,
        bool defaultAgeVerify,
        ISettingsPort settingsPort,
        string databaseInfo)
    {
        _backupPort = backupPort;
        _fileWatcherPort = fileWatcherPort;
        _defaultSource = defaultSource;
        _defaultDestinations = string.Join(";", defaultDestinations);
        _defaultSamplePercent = defaultSamplePercent;
        _defaultAgeVerify = defaultAgeVerify;
        _settingsPort = settingsPort;
        _databaseInfo = databaseInfo;
        _lastSamplePercent = defaultSamplePercent;
        _lastAgeVerify = defaultAgeVerify;
    }

    public void Run()
    {
        GuiApp.Init();

        var top = GuiApp.Top;
        var window = new Window("ArchiveTUI Backup")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        var sourceLabel = new Label(1, 1, "Source:");
        var sourceField = new TextField(_defaultSource)
        {
            X = 16,
            Y = Pos.Top(sourceLabel),
            Width = Dim.Fill(2)
        };

        var destinationLabel = new Label(1, 3, "Destinations:");
        var destinationField = new TextField(_defaultDestinations)
        {
            X = 16,
            Y = Pos.Top(destinationLabel),
            Width = Dim.Fill(2)
        };

        var sampleLabel = new Label(1, 5, "Verify sample %:");
        var sampleField = new TextField(_defaultSamplePercent.ToString())
        {
            X = 16,
            Y = Pos.Top(sampleLabel),
            Width = 6
        };

        var ageLabel = new Label(1, 7, "Age verify (7-day refresh):");
        var ageToggle = new CheckBox()
        {
            X = 28,
            Y = Pos.Top(ageLabel),
            Checked = _defaultAgeVerify
        };

        var watchLabel = new Label(1, 9, "Watch changes (keeps running):");
        var watchToggle = new CheckBox()
        {
            X = 16,
            Y = Pos.Top(watchLabel),
            Checked = true
        };

        var dbLabel = new Label(1, 11, $"Database: PostgreSQL ({_databaseInfo})");

        var startButton = new Button(1, 13, "Start Backup");
        var quitButton = new Button(16, 13, "Quit");

        var statusView = new TextView
        {
            X = 1,
            Y = 15,
            Width = Dim.Fill(2),
            Height = Dim.Fill(1),
            ReadOnly = true,
            Text = "Enter source and destination roots. Separate destinations with ';'"
        };

        startButton.Clicked += () =>
        {
            var sourceRoot = ReadText(sourceField);
            var destinationRoots = ParseDestinations(ReadText(destinationField));
            var samplePercent = int.TryParse(ReadText(sampleField), out var parsed)
                ? Math.Clamp(parsed, 0, 100)
                : _defaultSamplePercent;
            var ageVerify = ageToggle.Checked;
            _lastSamplePercent = samplePercent;
            _lastAgeVerify = ageVerify;
            _ = RunBackupAsync(sourceRoot, destinationRoots, samplePercent, ageVerify, watchToggle.Checked, statusView, startButton);
        };

        quitButton.Clicked += () => GuiApp.RequestStop();

        window.Add(
            sourceLabel,
            sourceField,
            destinationLabel,
            destinationField,
            sampleLabel,
            sampleField,
            ageLabel,
            ageToggle,
            watchLabel,
            watchToggle,
            dbLabel,
            startButton,
            quitButton,
            statusView);
        top.Add(window);

        GuiApp.Run();
        GuiApp.Shutdown();
    }

    private async Task RunBackupAsync(string sourceRoot, IReadOnlyCollection<string> destinationRoots, int samplePercent, bool ageVerify, bool enableWatch, TextView statusView, Button startButton)
    {
        GuiApp.MainLoop?.Invoke(() =>
        {
            startButton.Enabled = false;
            statusView.Text = "Backup running...";
        });

        try
        {
            var result = await _backupPort.RunAsync(new BackupRequest(sourceRoot, destinationRoots, samplePercent, ageVerify)).ConfigureAwait(false);
            var output = BuildSummary(result);
            GuiApp.MainLoop?.Invoke(() => statusView.Text = output);

            await _settingsPort.SaveAsync(sourceRoot, destinationRoots, samplePercent, ageVerify).ConfigureAwait(false);

            if (enableWatch && _watchSubscription is null)
            {
                StartWatcher(sourceRoot, destinationRoots, samplePercent, ageVerify, statusView);
                GuiApp.MainLoop?.Invoke(() => statusView.Text += $"{Environment.NewLine}Watching for changes...");
            }
        }
        catch (Exception ex)
        {
            GuiApp.MainLoop?.Invoke(() => statusView.Text = $"Backup failed: {ex.Message}");
        }
        finally
        {
            GuiApp.MainLoop?.Invoke(() => startButton.Enabled = true);
        }
    }

    private static string ReadText(TextField field)
    {
        return field.Text?.ToString()?.Trim() ?? string.Empty;
    }

    private static IReadOnlyCollection<string> ParseDestinations(string value)
    {
        return value
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static string BuildSummary(BackupResult result)
    {
        var lines = new List<string>
        {
            $"Scanned: {result.ScannedFiles}",
            $"Copied: {result.CopiedFiles}",
            $"Skipped: {result.SkippedFiles}",
            $"Failed: {result.FailedFiles}"
        };

        if (result.Infos.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Info:");
            lines.AddRange(result.Infos.Select(info => $"- {info}"));
        }

        if (result.Errors.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Errors:");
            lines.AddRange(result.Errors.Select(error => $"- {error}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void StartWatcher(string sourceRoot, IReadOnlyCollection<string> destinationRoots, int samplePercent, bool ageVerify, TextView statusView)
    {
        _watchSubscription = _fileWatcherPort.Watch(
            sourceRoot,
            destinationRoots,
            () => ScheduleWatchRun(sourceRoot, destinationRoots, samplePercent, ageVerify, statusView),
            ex =>
            {
                var msg = ex is null ? "Watcher error; scheduling rescan." : $"Watcher error: {ex.Message}";
                GuiApp.MainLoop?.Invoke(() => statusView.Text = msg);
                ScheduleWatchRun(sourceRoot, destinationRoots, samplePercent, ageVerify, statusView);
            });
    }

    private void ScheduleWatchRun(string sourceRoot, IReadOnlyCollection<string> destinationRoots, int samplePercent, bool ageVerify, TextView statusView)
    {
        lock (this)
        {
            _watchDebounce?.Dispose();
            _watchDebounce = new Timer(async _ =>
            {
                try
                {
                    var result = await _backupPort.RunAsync(new BackupRequest(sourceRoot, destinationRoots, samplePercent, ageVerify)).ConfigureAwait(false);
                    var summary = BuildSummary(result);
                    GuiApp.MainLoop?.Invoke(() => statusView.Text = $"Change detected.{Environment.NewLine}{summary}");
                }
                catch (Exception ex)
                {
                    GuiApp.MainLoop?.Invoke(() => statusView.Text = $"Watch backup failed: {ex.Message}");
                }
            }, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
    }
}
