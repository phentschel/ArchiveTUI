using ArchiveTUI.Domain.Models;

namespace ArchiveTUI.Application.Ports;

public interface ISettingsPort
{
    Task<AppSettingsRecord?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(string sourceRoot, IReadOnlyCollection<string> destinationRoots, int verifySamplePercent, bool ageVerificationEnabled, CancellationToken cancellationToken = default);
}
