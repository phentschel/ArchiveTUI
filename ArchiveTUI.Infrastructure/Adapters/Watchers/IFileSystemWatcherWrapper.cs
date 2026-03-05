using System.IO;

namespace ArchiveTUI.Infrastructure.Adapters.Watchers;

internal interface IFileSystemWatcherWrapper : IDisposable
{
    event FileSystemEventHandler? Created;
    event FileSystemEventHandler? Changed;
    event FileSystemEventHandler? Deleted;
    event RenamedEventHandler? Renamed;
    event ErrorEventHandler? Error;

    bool IncludeSubdirectories { get; set; }
    bool EnableRaisingEvents { get; set; }
    string Filter { get; set; }
    NotifyFilters NotifyFilter { get; set; }
    int InternalBufferSize { get; set; }
    string Path { get; }
}

