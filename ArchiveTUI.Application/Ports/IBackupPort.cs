namespace ArchiveTUI.Application.Ports;

public interface IBackupPort
{
    Task<BackupResult> RunAsync(BackupRequest request, CancellationToken cancellationToken = default);
}
