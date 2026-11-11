using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Contracts;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.BackupUi;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;
using PotatoVN.Backup.Core.Providers;

namespace PotatoVN.App.PluginBase.Backup;

public sealed class BackupController : IBackupUiController, IDisposable
{
    private readonly IPotatoVnApi _api;
    private readonly PotatoHostAdapter _host;
    private readonly SettingsStore _store;
    private readonly PendingRestoreStore _pending;
    private readonly ISecretStore _secrets;
    private readonly BackupCoordinator _coordinator;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _current;
    private DateTimeOffset? _exitDue;
    private DateTimeOffset _lastAutoAttempt;
    private readonly Dictionary<string, Guid> _listedProfiles = new(StringComparer.Ordinal);
    public BackupSettings Settings { get; }
    public bool IsBusy { get; private set; }
    public string Status { get; private set; } = "尚未开始备份。";
    public event Action? StateChanged;
    private PendingRestore[] _pendingView = Array.Empty<PendingRestore>();
    private long _pendingBytes;
    public long PendingBytes => Interlocked.Read(ref _pendingBytes);
    public IReadOnlyList<GameIdentity> GetGames() => _host.GetGames();
    public string? GetGameCoverPath(Guid gameId) => _host.GetGameCoverPath(gameId);
    public IReadOnlyList<PendingRestore> GetPending() => Volatile.Read(ref _pendingView);

    public BackupController(IPotatoVnApi api, PotatoHostAdapter host, SettingsStore store,
        BackupSettings settings, PendingRestoreStore pending)
    {
        _api = api; _host = host; _store = store; Settings = settings; _pending = pending;
        _secrets = new DpapiSecretStore(Path.Combine(host.StateRoot, "secrets"));
        _coordinator = new BackupCoordinator(host.StateRoot, host, settings, store, pending, p => BackupProviderFactory.Create(p, _secrets));
        _coordinator.Progress += text => { Status = text; Changed(); };
        Changed();
        _ = WatchAsync();
    }

    private void Changed()
    {
        Volatile.Write(ref _pendingView, _pending.Items.Select(i => new PendingRestore
        {
            Id = i.Id, ProfileId = i.ProfileId, ProfileName = i.ProfileName, Manifest = i.Manifest,
            LocationId = i.LocationId, LocalGameId = i.LocalGameId, ExcludedLocalGameIds = i.ExcludedLocalGameIds.ToList(), Status = i.Status,
            TargetOverride = i.TargetOverride, TargetPath = i.TargetPath, Error = i.Error, IsBusy = i.IsBusy
        }).ToArray());
        Interlocked.Exchange(ref _pendingBytes, _pending.GetStats().Bytes);
        _api.InvokeOnMainThread(() => StateChanged?.Invoke());
    }
    private ProviderProfile Profile(Guid id) => Settings.Profiles.FirstOrDefault(p => p.Id == id)
        ?? throw new InvalidOperationException("所选渠道已被删除。");

    public async Task SaveSettingsAsync()
    {
        if (IsBusy) throw new InvalidOperationException("请等待当前任务结束后再保存配置。");
        if (!Settings.OnGameExitEnabled) _exitDue = null;
        if (Settings.Content == BackupContent.None) throw new InvalidOperationException("请至少勾选一种备份内容。");
        if (!double.IsFinite(Settings.IntervalHours) || Settings.IntervalHours < 0.05 || Settings.IntervalHours > 8760)
            throw new InvalidOperationException("定时间隔应在 0.05 至 8760 小时之间。");
        if (Settings.ExitDelaySeconds < 0 || Settings.ExitDelaySeconds > 3600 || Settings.KeepSnapshots < 1 || Settings.KeepSaves < 1)
            throw new InvalidOperationException("请检查退出延迟和历史保留数量。");
        if ((Settings.TimerEnabled || Settings.OnGameExitEnabled) && !Settings.Profiles.Any(p => p.Id == Settings.ActiveProfileId))
            throw new InvalidOperationException("启用自动备份前，请先选择备份渠道。");
        foreach (GameSaveConfig config in Settings.SaveConfigs)
            foreach (SaveLocation location in config.Locations)
                if (location.BaseKind == SaveBaseKind.Custom && !Settings.CustomSavePaths.ContainsKey(location.Id))
                    throw new InvalidOperationException("自定义存档位置需要填写本机完整路径。");
        await _store.SaveAsync(Settings);
        Status = "设置已保存。"; Changed();
    }

    public Task SetSecretAsync(Guid profileId, string suffix, string value)
    {
        _ = Profile(profileId);
        if (suffix is not ("password" or "client-secret")) throw new ArgumentException("不支持的凭据类型。");
        return _secrets.WriteAsync(profileId + ":" + suffix, value);
    }

    public Task AuthorizeAsync(Guid id, CancellationToken ct) => RunAsync("正在授权…", async token =>
    {
        using IBackupProvider provider = BackupProviderFactory.Create(Profile(id), _secrets);
        await provider.AuthorizeAsync(token); await _store.SaveAsync(Settings); Status = "授权成功。";
    }, ct);

    public Task ProbeAsync(Guid id, CancellationToken ct) => RunAsync("正在检查连接…", async token =>
    {
        using IBackupProvider provider = BackupProviderFactory.Create(Profile(id), _secrets);
        await provider.ProbeAsync(token); Status = "连接成功。";
    }, ct);

    public Task BackupAsync(CancellationToken ct) => RunAsync("正在备份…", async token =>
    {
        await _coordinator.BackupAsync(token);
    }, ct);

    public async Task<IReadOnlyList<SnapshotManifest>> ListSnapshotsAsync(CancellationToken ct)
    {
        IReadOnlyList<SnapshotManifest> result = Array.Empty<SnapshotManifest>();
        await RunAsync("正在读取云端历史…", async token =>
        {
            Guid profile = _coordinator.ActiveProfile.Id;
            result = await _coordinator.ListSnapshotsAsync(token);
            _listedProfiles.Clear();
            foreach (SnapshotManifest item in result) _listedProfiles[item.ManifestPath] = profile;
            Status = $"找到 {result.Count} 个完整备份版本。";
        }, ct);
        return result;
    }

    public Task RestoreSnapshotAsync(SnapshotManifest snapshot, BackupContent content, CancellationToken ct, RestoreMatchingDecisions? matching = null) => RunAsync("正在准备恢复…", async token =>
    {
        if (!_listedProfiles.TryGetValue(snapshot.ManifestPath, out Guid profile) || profile != _coordinator.ActiveProfile.Id)
            throw new InvalidOperationException("渠道已改变，请重新刷新并选择该渠道的备份版本。");
        await _coordinator.StageRestoreAsync(snapshot, content, token, matching);
        Status = "所选恢复内容已处理；存档请在待恢复区手动确认恢复。";
    }, ct);

    public Task ConfigureGameMappingAsync(Guid backupGameId, Guid localGameId, CancellationToken ct) => RunAsync("正在保存游戏对应关系…", async token =>
    {
        if (!GetGames().Any(g => g.Id == localGameId)) throw new InvalidOperationException("请先选择当前游戏库中的游戏。");
        Settings.GameMappings.RemoveAll(m => m.BackupGameId == backupGameId);
        Settings.GameMappings.Add(new GameMatchMapping { BackupGameId = backupGameId, LocalGameId = localGameId });
        _pending.Refresh(GetGames(), Settings.GameMappings);
        await _store.SaveAsync(Settings, token); await _pending.SaveAsync(token);
        Status = "游戏对应关系已保存。";
    }, ct);

    public Task RefreshPendingAsync(CancellationToken ct) => RunAsync("正在刷新待恢复区…", async token =>
    {
        _pending.Refresh(GetGames(), Settings.GameMappings);
        await _pending.SaveAsync(token);
        Status = "待恢复区已刷新。";
    }, ct);

    public Task ConfigurePendingAsync(Guid pendingId, Guid? localGameId, string? targetPath, CancellationToken ct) => RunAsync("正在保存恢复位置…", async token =>
    {
        PendingRestore item = _pending.Items.Single(i => i.Id == pendingId);
        if (item.IsBusy) throw new InvalidOperationException("该存档正在恢复，请稍后再编辑。");
        if (localGameId != null && !GetGames().Any(g => g.Id == localGameId)) throw new InvalidOperationException("所选游戏已不存在。");
        Settings.GameMappings.RemoveAll(m => m.BackupGameId == item.Manifest.Game.Id);
        if (localGameId != null)
            Settings.GameMappings.Add(new GameMatchMapping { BackupGameId = item.Manifest.Game.Id, LocalGameId = localGameId.Value });
        IReadOnlyList<GameIdentity> games = GetGames();
        _pending.Refresh(games, Settings.GameMappings);
        item.TargetOverride = string.IsNullOrWhiteSpace(targetPath) ? null : Path.GetFullPath(targetPath);
        item.Error = null;
        if (item.Status == RestoreStatus.Failed) item.Status = RestoreStatus.PendingPath;
        _pending.Refresh(games, Settings.GameMappings);
        await _store.SaveAsync(Settings, token); await _pending.SaveAsync(token);
        Status = "恢复位置已保存。";
    }, ct);
    public Task RestorePendingAsync(IReadOnlyList<Guid> ids, CancellationToken ct) => RunAsync("正在恢复存档…", async token =>
    {
        _pending.Refresh(GetGames(), Settings.GameMappings);
        List<PendingRestore> selected = _pending.Items.Where(i => ids.Contains(i.Id)).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("请先选择待恢复项目。");
        int written = 0, unchanged = 0;
        foreach (PendingRestore item in selected)
        {
            token.ThrowIfCancellationRequested();
            GameIdentity? game = GetGames().FirstOrDefault(g => g.Id == item.LocalGameId);
            if (game?.LocalDirectory == null || !Directory.Exists(game.LocalDirectory))
                throw new InvalidOperationException($"{item.Manifest.Game.Name}：请先关联有效的本地游戏目录。");
            if (item.TargetPath == null) throw new InvalidOperationException($"{game.Name}：请先设置存档目标路径。");
            item.IsBusy = true; Changed();
            try
            {
                bool changed = await SaveArchiveService.RestoreAsync(item, _pending.ArchivePath(item.Manifest), game,
                    Path.Combine(_host.StateRoot, "rollback"), token);
                if (changed) written++; else unchanged++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                item.Status = RestoreStatus.Failed; item.Error = e.Message; throw;
            }
            finally { item.IsBusy = false; await _pending.SaveAsync(); Changed(); }
        }
        Status = $"存档恢复完成：写入 {written} 个位置，{unchanged} 个位置内容相同。";
    }, ct);

    public Task ClearPendingAsync(IReadOnlyList<Guid> ids, CancellationToken ct) => RunAsync("正在清理本机待恢复区…", async token =>
    {
        await _pending.ClearAsync(ids, token);
        Status = "已清理本机暂存副本；云端和实际游戏存档保持原样。";
    }, ct);

    public Task ApplyLibraryMappingAsync(string libraryId, string targetRoot, CancellationToken ct) => RunAsync("正在准备库目录映射…", async token =>
    {
        string full = Path.GetFullPath(targetRoot);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("选择的本机库目录不存在。");
        PendingLibraryMapping mapping = Settings.LibraryMappings.Single(m => m.Id == libraryId);
        mapping.TargetPath = full;
        await _coordinator.ApplyLibraryMappingsAsync(token);
    }, ct);

    public void CancelCurrentOperation() => _current?.Cancel();

    private async Task RunAsync(string initial, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("已有云备份任务正在运行。");
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        _current = linked; IsBusy = true; Status = initial; Changed();
        try { await action(linked.Token); }
        catch (OperationCanceledException) { Status = "操作已取消；已经完成的版本和游戏存档仍会保留。"; throw; }
        catch (Exception e) { Status = e.Message; _api.Log(InfoBarSeverity.Error, "云备份：" + e.Message); throw; }
        finally { _current = null; IsBusy = false; _gate.Release(); Changed(); }
    }

    public IReadOnlyList<SavePathSuggestion> GetSavePathSuggestions(Guid gameId)
    {
        GameIdentity? game = GetGames().FirstOrDefault(item => item.Id == gameId);
        if (game == null) return Array.Empty<SavePathSuggestion>();
        var candidates = new List<SavePathSuggestion>();
        void AddCandidate(string path, string source)
        {
            if (!Path.IsPathFullyQualified(path) || (!File.Exists(path) && !Directory.Exists(path))) return;
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string[] protectedRoots = [Path.GetPathRoot(full) ?? "", game.LocalDirectory ?? "",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!, "LocalLow"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)];
            if (protectedRoots.Where(root => !string.IsNullOrWhiteSpace(root)).Any(root =>
                    string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), full, StringComparison.OrdinalIgnoreCase))) return;
            if (candidates.Any(candidate => string.Equals(candidate.Path, full, StringComparison.OrdinalIgnoreCase))) return;
            candidates.Add(new SavePathSuggestion(full, File.Exists(full), source));
        }
        foreach (string path in _host.GetSuggestedSavePaths(gameId)) AddCandidate(path, "PotatoVN 提供的存档位置");
        // 仅核对直接名为 save 的子文件夹，不扫描游戏文件，不自动加入备份。
        if (!string.IsNullOrWhiteSpace(game.LocalDirectory) && Directory.Exists(game.LocalDirectory))
            AddCandidate(Path.Combine(game.LocalDirectory, "save"), "游戏安装目录内的 save 文件夹（请确认）");
        return candidates;
    }

    public void ScheduleGameExit()
    {
        if (Settings.OnGameExitEnabled) _exitDue = DateTimeOffset.UtcNow.AddSeconds(Settings.ExitDelaySeconds);
    }

    private async Task WatchAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                if (IsBusy) continue;
                try
                {
                    if (await _gate.WaitAsync(0, _shutdown.Token))
                    {
                        try
                        {
                            List<Guid> before = _pending.Items.Where(i => i.Status == RestoreStatus.PendingLink).Select(i => i.Id).ToList();
                            _pending.Refresh(GetGames(), Settings.GameMappings);
                            if (_pending.Items.Any(i => before.Contains(i.Id) && i.Status != RestoreStatus.PendingLink))
                            {
                                await _pending.SaveAsync(_shutdown.Token);
                                _api.Info(InfoBarSeverity.Informational, "有待恢复存档", "游戏已关联，请打开云备份的待恢复区选择版本并手动恢复。", 10000);
                                Changed();
                            }
                        }
                        finally { _gate.Release(); }
                    }
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    bool exit = Settings.OnGameExitEnabled && _exitDue != null && now >= _exitDue;
                    bool periodic = Settings.TimerEnabled && now - (Settings.LastSuccessfulBackupUtc ?? _lastAutoAttempt) >= TimeSpan.FromHours(Settings.IntervalHours);
                    if ((!exit && !periodic) || now - _lastAutoAttempt < TimeSpan.FromSeconds(60)) continue;
                    _exitDue = null; _lastAutoAttempt = now;
                    await BackupAsync(_shutdown.Token);
                }
                catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested) { }
                catch (Exception e) when (!_shutdown.IsCancellationRequested)
                { _api.Info(InfoBarSeverity.Warning, "自动备份未完成", e.Message, 10000); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        _shutdown.Cancel(); _current?.Cancel();
    }
}
