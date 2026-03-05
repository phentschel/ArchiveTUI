using ArchiveTUI.Application.Ports;
using ArchiveTUI.Infrastructure.Adapters.Watchers;
using System.IO;

namespace ArchiveTUI.Infrastructure.Adapters;

public sealed class FileWatcherAdapter : IFileWatcherPort
{
    private readonly IFileSystemWatcherFactory _factory;

    public FileWatcherAdapter()
        : this(new FileSystemWatcherFactory())
    {
    }

    internal FileWatcherAdapter(IFileSystemWatcherFactory factory)
    {
        _factory = factory;
    }

    public IDisposable Watch(string path, IReadOnlyCollection<string> ignorePrefixes, Action onChange, Action<Exception?> onError)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var normalizedRoot = Path.GetFullPath(path);
        var normalizedIgnores = ignorePrefixes
            .Select(Path.GetFullPath)
            .Select(EnsureTrailingSeparator)
            .ToArray();

        var watcher = _factory.Create(normalizedRoot);

        watcher.IncludeSubdirectories = true;
        watcher.EnableRaisingEvents = true;
        watcher.Filter = "*";
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
        TrySetInternalBuffer(watcher, onError);

        FileSystemEventHandler handler = (_, args) =>
        {
            if (IsIgnored(args.FullPath, normalizedIgnores, comparison))
            {
                return;
            }
            onChange();
        };

        RenamedEventHandler renameHandler = (_, args) =>
        {
            if (IsIgnored(args.FullPath, normalizedIgnores, comparison))
            {
                return;
            }
            onChange();
        };

        ErrorEventHandler errorHandler = (_, exArgs) => onError(exArgs.GetException());

        watcher.Created += handler;
        watcher.Changed += handler;
        watcher.Deleted += handler;
        watcher.Renamed += renameHandler;
        watcher.Error += errorHandler;

        return new DisposableWatcher(watcher, handler, renameHandler, errorHandler);
    }

    private static bool IsIgnored(string fullPath, IReadOnlyCollection<string> ignorePrefixes, StringComparison comparison)
    {
        var normalized = Path.GetFullPath(fullPath);
        foreach (var prefix in ignorePrefixes)
        {
            if (normalized.StartsWith(prefix, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static void TrySetInternalBuffer(IFileSystemWatcherWrapper watcher, Action<Exception?> onError)
    {
        try
        {
            watcher.InternalBufferSize = 64 * 1024;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            onError(ex);
            // leave default
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }

    private sealed class DisposableWatcher : IDisposable
    {
        private readonly IFileSystemWatcherWrapper _watcher;
        private readonly FileSystemEventHandler _handler;
        private readonly RenamedEventHandler _renameHandler;
        private readonly ErrorEventHandler _errorHandler;
        private bool _disposed;

        public DisposableWatcher(
            IFileSystemWatcherWrapper watcher,
            FileSystemEventHandler handler,
            RenamedEventHandler renameHandler,
            ErrorEventHandler errorHandler)
        {
            _watcher = watcher;
            _handler = handler;
            _renameHandler = renameHandler;
            _errorHandler = errorHandler;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _watcher.Created -= _handler;
            _watcher.Changed -= _handler;
            _watcher.Deleted -= _handler;
            _watcher.Renamed -= _renameHandler;
            _watcher.Error -= _errorHandler;
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
