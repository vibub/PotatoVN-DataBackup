using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PotatoVN.Backup.Core;

[Flags]
public enum BackupContent
{
    None = 0, GameInfo = 1, Covers = 2, Reviews = 4, PlayTime = 8,
    Characters = 16, Headers = 32, LibrariesAndCategories = 64, Staff = 128,
    Home = 256, Settings = 512, Saves = 1024, All = 2047
}

public enum ProviderKind { WebDav, OneDrive, GoogleDrive }
public enum SaveBaseKind { GameRoot, AppData, LocalAppData, LocalLow, Documents, Custom }
public enum RestoreStatus { PendingLink, PendingPath, Ready, Restored, Failed }

public sealed class ProviderProfile
{
    public ProviderProfile() { }

    // 旧配置没有此字段，继续使用原仓库；新渠道采用更易识别的固定目录。
    [JsonConstructor]
    public ProviderProfile(string? repositoryFolder) => RepositoryFolder = repositoryFolder ?? "PotatoVN-DataBackup";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新渠道";
    public ProviderKind Kind { get; set; }
    public string Endpoint { get; set; } = "";
    public string Username { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public string RepositoryFolder { get; set; } = "PotatoVN_Data";
    public string ClientId { get; set; } = "";
    public string Tenant { get; set; } = "common";
}

public sealed class BackupSettings
{
    public int FormatVersion { get; set; } = 1;
    public Guid DeviceId { get; set; } = Guid.NewGuid();
    public string DeviceName { get; set; } = Environment.MachineName;
    public List<ProviderProfile> Profiles { get; set; } = [];
    public Guid? ActiveProfileId { get; set; }
    public BackupContent Content { get; set; } = BackupContent.All;
    public List<Guid> SelectedGameIds { get; set; } = [];
    public bool AllGames { get; set; } = true;
    public bool TimerEnabled { get; set; }
    public double IntervalHours { get; set; } = 6;
    public bool OnGameExitEnabled { get; set; }
    public int ExitDelaySeconds { get; set; } = 10;
    public int KeepSnapshots { get; set; } = 10;
    public int KeepSaves { get; set; } = 10;
    public DateTimeOffset? LastSuccessfulBackupUtc { get; set; }
    public List<GameSaveConfig> SaveConfigs { get; set; } = [];
    public Dictionary<string, string> CustomSavePaths { get; set; } = [];
    public List<GameMatchMapping> GameMappings { get; set; } = [];
    public List<PendingLibraryMapping> LibraryMappings { get; set; } = [];
    [JsonIgnore] public RestoreMatchingDecisions? ActiveRestoreMatching { get; set; }
}

public sealed class GameIdentity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Dictionary<string, string> SourceIds { get; set; } = [];
    [JsonIgnore] public string? LocalDirectory { get; set; }
    [JsonIgnore] public bool IsRunning { get; set; }
}

public sealed class GameMatchMapping
{
    public Guid BackupGameId { get; set; }
    public Guid LocalGameId { get; set; }
}

public sealed class PendingLibraryMapping
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string? TargetPath { get; set; }
    public string SourceJson { get; set; } = "";
}

public sealed class GameSaveConfig
{
    public Guid GameId { get; set; }
    public List<SaveLocation> Locations { get; set; } = [];
}

public sealed class SaveLocation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public SaveBaseKind BaseKind { get; set; }
    public string RelativePath { get; set; } = "";
    public bool IsFile { get; set; }
    [JsonIgnore] public string? CustomPath { get; set; }
}

public sealed class FileRecord
{
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class SaveManifest
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public Guid DeviceId { get; set; }
    public GameIdentity Game { get; set; } = new();
    public DateTimeOffset CapturedUtc { get; set; }
    public string ManifestPath { get; set; } = "";
    public string ArchivePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public long Length { get; set; }
    public List<SaveLocation> Locations { get; set; } = [];
    public List<FileRecord> Files { get; set; } = [];
}

public sealed class SnapshotManifest
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public Guid DeviceId { get; set; }
    public string DeviceName { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public BackupContent Content { get; set; }
    public string ManifestPath { get; set; } = "";
    public FileRecord? Data { get; set; }
    public FileRecord? Media { get; set; }
    public List<GameIdentity> Games { get; set; } = [];
    public List<string> SaveManifestPaths { get; set; } = [];
    public List<string> Notices { get; set; } = [];
}

public sealed class PendingRestore
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public string ProfileName { get; set; } = "";
    public SaveManifest Manifest { get; set; } = new();
    public string LocationId { get; set; } = "";
    public Guid? LocalGameId { get; set; }
    public List<Guid> ExcludedLocalGameIds { get; set; } = [];
    public RestoreStatus Status { get; set; }
    public string? TargetOverride { get; set; }
    public string? TargetPath { get; set; }
    public string? Error { get; set; }
    [JsonIgnore] public bool IsBusy { get; set; }
}

public sealed class HostExport
{
    public string ArchivePath { get; set; } = "";
    public Dictionary<string, JsonNode?> Settings { get; set; } = [];
}

public interface IBackupHost
{
    IReadOnlyList<GameIdentity> GetGames();
    void ValidateImport()
    {
        if (GetGames().Any(g => g.IsRunning)) throw new InvalidOperationException("请先退出运行中的游戏，再恢复软件资料。");
    }
    Task<HostExport> ExportAsync(string workDirectory, CancellationToken cancellationToken);
    Task StageImportAsync(string mergedArchivePath, Dictionary<string, JsonNode?> settings,
        CancellationToken cancellationToken);
}

public sealed record RemoteItem(string Path, bool IsDirectory, long Length = 0,
    DateTimeOffset? ModifiedUtc = null);

public interface IBackupProvider : IDisposable
{
    Task AuthorizeAsync(CancellationToken cancellationToken = default);
    Task ProbeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);
    Task UploadAsync(string path, string localFile, CancellationToken cancellationToken = default);
    Task DownloadAsync(string path, string localFile, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default);
    Task WriteAsync(string key, string value, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public static class BackupJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
