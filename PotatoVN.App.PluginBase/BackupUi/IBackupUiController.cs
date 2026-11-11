using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PotatoVN.Backup.Core;

namespace PotatoVN.App.PluginBase.BackupUi;

public interface IBackupUiController
{
    BackupSettings Settings { get; }
    IReadOnlyList<GameIdentity> GetGames();
    string? GetGameCoverPath(Guid gameId) => null;
    IReadOnlyList<SavePathSuggestion> GetSavePathSuggestions(Guid gameId) => Array.Empty<SavePathSuggestion>();
    IReadOnlyList<PendingRestore> GetPending();
    long PendingBytes { get; }
    bool IsBusy { get; }
    int? BackupProgressPercent => null;
    Task OpenDataFolderAsync() => throw new NotSupportedException("无法打开本机数据目录。");
    string Status { get; }
    event Action? StateChanged;
    void CancelCurrentOperation();
    Task SaveSettingsAsync();
    Task SetSecretAsync(Guid profileId, string suffix, string value);
    Task AuthorizeAsync(Guid profileId, CancellationToken cancellationToken);
    Task ProbeAsync(Guid profileId, CancellationToken cancellationToken);
    Task BackupAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SnapshotManifest>> ListSnapshotsAsync(CancellationToken cancellationToken);
    Task RestoreSnapshotAsync(SnapshotManifest snapshot, BackupContent content, CancellationToken cancellationToken, RestoreMatchingDecisions? matching = null);
    Task ConfigureGameMappingAsync(Guid backupGameId, Guid localGameId, CancellationToken cancellationToken);
    Task RefreshPendingAsync(CancellationToken cancellationToken);
    Task ConfigurePendingAsync(Guid pendingId, Guid? localGameId, string? targetPath, CancellationToken cancellationToken);
    Task RestorePendingAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken);
    Task ClearPendingAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken);
    Task ApplyLibraryMappingAsync(string libraryId, string targetRoot, CancellationToken cancellationToken);
}

public sealed record SavePathSuggestion(string Path, bool IsFile, string Source);
