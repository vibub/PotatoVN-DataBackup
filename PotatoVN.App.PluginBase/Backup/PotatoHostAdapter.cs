using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.Enums;
using GalgameManager.Models;
using GalgameManager.Models.BgTasks;
using GalgameManager.WinApp.Base.Contracts;
using LiteDB;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;

namespace PotatoVN.App.PluginBase.Backup;

/// <summary>宿主内部接口只在这里解析，不修改宿主或公开库。</summary>
public sealed class PotatoHostAdapter : IBackupHost
{
    private readonly IPotatoVnApi _api;
    private readonly DispatcherQueue _dispatcher;
    private readonly Assembly _hostAssembly;
    private readonly object _settings;
    private readonly string _stateRoot;
    public string DataRoot { get; }
    public string StateRoot => _stateRoot;

    // 可迁移的常规设置：不自动收集所有设置，避免包含账户、路径或同步游标。
    public static IReadOnlyList<string> SettingKeys => SoftwareBackupCodec.PortableSettingKeys;

    public PotatoHostAdapter(IPotatoVnApi api)
    {
        _api = api;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("请在主线程初始化云备份插件。");
        _hostAssembly = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetType("GalgameManager.App") != null);
        _settings = Service("GalgameManager.Contracts.Services.ILocalSettingsService");
        DataRoot = ((DirectoryInfo)Property(_settings, "LocalFolder")).FullName;
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(DataRoot).ToUpperInvariant())))[..16];
        _stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PotatoVN-DataBackup", key);
        Directory.CreateDirectory(_stateRoot);
    }

    private object Service(string typeName)
    {
        Type type = _hostAssembly.GetType(typeName, true)!;
        MethodInfo method = _hostAssembly.GetType("GalgameManager.App", true)!.GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        return method.MakeGenericMethod(type).Invoke(null, null)!;
    }

    private static object Property(object target, string name) => target.GetType().GetProperty(name)?.GetValue(target)
        ?? throw new NotSupportedException($"当前 PotatoVN 缺少兼容接口：{name}。");

    public Task<T> OnMainAsync<T>(Func<T> action)
    {
        if (_dispatcher.HasThreadAccess) return Task.FromResult(action());
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception e) { completion.SetException(e); }
        })) completion.SetException(new InvalidOperationException("主程序正在关闭。"));
        return completion.Task;
    }

    public IReadOnlyList<GameIdentity> GetGames() => OnMainAsync(() =>
    {
        HashSet<Guid> running = _api.GetBgTasks()
            .Where(t => t.GetType().FullName == "GalgameManager.Models.BgTasks.RecordPlayTimeTask" && t.IsRunning)
            .Select(t => t.GetType().GetField("Galgame")?.GetValue(t) as Galgame)
            .Where(g => g != null).Select(g => g!.Uuid).ToHashSet();
        return (IReadOnlyList<GameIdentity>)_api.GetAllGames().Select(g => new GameIdentity
        {
            Id = g.Uuid, Name = g.Name.Value ?? "未命名游戏", LocalDirectory = g.LocalPath,
            IsRunning = running.Contains(g.Uuid),
            SourceIds = Enum.GetValues<RssType>().Distinct().Where(r => (int)r >= 0 && (int)r < g.Ids.Length && !string.IsNullOrWhiteSpace(g.Ids[(int)r]))
                .ToDictionary(r => r.ToString(), r => g.Ids[(int)r]!)
        }).ToList();
    }).GetAwaiter().GetResult();

    public string? GetGameCoverPath(Guid gameId) => OnMainAsync(() =>
    {
        string? path = _api.GetAllGames().FirstOrDefault(game => game.Uuid == gameId)?.ImagePath.Value;
        return path == Galgame.DefaultImagePath ? null : path;
    }).GetAwaiter().GetResult();

    public string? GetSuggestedSavePath(Guid gameId) => GetSuggestedSavePaths(gameId).FirstOrDefault();

    public IReadOnlyList<string> GetSuggestedSavePaths(Guid gameId) => OnMainAsync(() =>
    {
        Galgame? game = _api.GetAllGames().FirstOrDefault(g => g.Uuid == gameId);
        if (game == null) return (IReadOnlyList<string>)Array.Empty<string>();
        // 检测结果失效时仍检查宿主保存的手动/同步位置，不将安装根目录当作存档。
        var candidates = new[] { game.DetectedSavePath?.ToPath(), game.SavePath };
        string? installation = string.IsNullOrWhiteSpace(game.LocalPath) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.LocalPath));
        return (IReadOnlyList<string>)candidates.Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
                (File.Exists(path) || Directory.Exists(path)))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!)))
            .Where(path => !string.Equals(path, installation, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }).GetAwaiter().GetResult();

    public async Task<HostExport> ExportAsync(string workDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(workDirectory);
        BgTaskBase task = await OnMainAsync(() =>
        {
            if (_api.GetBgTasks().Any(t => t.GetType().FullName == "GalgameManager.Models.BgTasks.ExportTask" && t.IsRunning))
                throw new InvalidOperationException("主程序正在导出数据，请稍后再试。");
            Type exportType = _hostAssembly.GetType("GalgameManager.Models.BgTasks.ExportTask", true)!;
            return (BgTaskBase)Activator.CreateInstance(exportType, workDirectory)!;
        });
        Task hostTask = await OnMainAsync(() => _api.AddBgTask(task));
        await hostTask; // 宿主会记录错误但可能吞掉异常，因此还需要等待原任务。
        await task.Task;
        cancellationToken.ThrowIfCancellationRequested();
        string? archive = Directory.GetFiles(workDirectory, "*.pvnExport.zip").SingleOrDefault();
        if (archive == null || task.CurrentProgress.Current < 0)
            throw new IOException("PotatoVN 未能生成完整的导出文件，请查看主程序事件记录。");
        Dictionary<string, JsonNode?> values = [];
        foreach (string key in SettingKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object? value = await ReadSettingAsync(key);
            values[key] = value == null ? null : JsonNode.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(value));
        }
        // 上游 ExportTask 只复制旧主页 JSON；新版主页已迁至 LiteDB，需要补齐。
        LiteDatabase live = (LiteDatabase)Property(_settings, "Database");
        if (live.CollectionExists("homepage_list"))
        {
            BsonArray home = new(live.GetCollection("homepage_list").FindAll().Select(d => (BsonValue)d));
            using ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Update);
            zip.GetEntry("data.homepage_list.json")?.Delete();
            using StreamWriter homeWriter = new(zip.CreateEntry("data.homepage_list.json").Open());
            homeWriter.Write(LiteDB.JsonSerializer.Serialize(home));
        }
        return new HostExport { ArchivePath = archive, Settings = values };
    }

    private async Task<object?> ReadSettingAsync(string key)
    {
        MethodInfo method = _settings.GetType().GetMethods().Single(m => m.Name == "ReadSettingAsync" && m.IsGenericMethodDefinition);
        Task task = (Task)method.MakeGenericMethod(typeof(object)).Invoke(_settings, [key, false, null, false])!;
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private async Task WriteSettingAsync(string key, JsonNode? value)
    {
        if (!SettingKeys.Contains(key, StringComparer.Ordinal)) throw new InvalidDataException($"不能恢复此设置：{key}");
        MethodInfo method = _settings.GetType().GetMethods().Single(m => m.Name == "SaveSettingAsync" && m.IsGenericMethodDefinition);
        object? parsed = value == null ? null : Newtonsoft.Json.JsonConvert.DeserializeObject(value.ToJsonString());
        Task task = (Task)method.MakeGenericMethod(typeof(object)).Invoke(_settings, [key, parsed, false, false, null, false])!;
        await task;
    }

    public async Task CompletePendingImportAsync()
    {
        string stateFile = Path.Combine(_stateRoot, "settings-restore.json");
        if (!File.Exists(stateFile)) return;
        JsonObject state = JsonNode.Parse(await File.ReadAllTextAsync(stateFile))!.AsObject();
        string markerId = state["marker"]!.GetValue<string>();
        bool imported = File.Exists(Path.Combine(DataRoot, $"databackup-{markerId}.complete"));
        JsonObject values = state[imported ? "after" : "before"]!.AsObject();
        foreach (var item in values) await WriteSettingAsync(item.Key, item.Value);
        File.Delete(stateFile);
        if (imported) File.Delete(Path.Combine(DataRoot, $"databackup-{markerId}.complete"));
        _api.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
            imported ? "备份恢复已完成" : "上次导入未完成，已保留原设置", "语言和部分显示设置在下次启动后生效。", 10000);
    }

    public void ValidateImport()
    {
        if (Directory.GetFiles(DataRoot, "*.pvnExport.zip").Length != 0)
            throw new InvalidOperationException("已有待导入的软件数据，请先完成或取消该导入。");
        if (GetGames().Any(g => g.IsRunning)) throw new InvalidOperationException("请先退出运行中的游戏，再恢复软件资料。");
    }

    public async Task StageImportAsync(string mergedArchivePath, Dictionary<string, JsonNode?> settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateImport();
        string marker = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(_stateRoot, "import", marker);
        Directory.CreateDirectory(stage);
        string target = Path.Combine(DataRoot, $"DataBackup_{marker}.pvnExport.zip");
        string prepared = Path.Combine(stage, "prepared.zip");
        File.Copy(mergedArchivePath, prepared);
        string databasePath = Path.Combine(stage, "pvn_data.db");
        LiteDatabase liveDb = (LiteDatabase)Property(_settings, "Database");
        string[] rebuilt = ["galgame", "source", "category", "category_group", "staff", "multi_stream", "multi_stream_page", "multistream", "homepage_list"];
        using (ZipArchive nativeZip = ZipFile.OpenRead(prepared))
        {
            if (nativeZip.GetEntry("data.homepage_list.json") is { } homeEntry)
            {
                using StreamReader reader = new(homeEntry.Open());
                BsonValue home = LiteDB.JsonSerializer.Deserialize(reader.ReadToEnd());
                if (!home.IsArray) throw new InvalidDataException("主页布局格式无效。");
                using LiteDatabase homeDatabase = new(databasePath);
                foreach (BsonValue value in home.AsArray)
                {
                    if (!value.IsDocument) throw new InvalidDataException("主页列表格式无效。");
                    BsonDocument document = value.AsDocument;
                    string type = document["_type"].IsString ? document["_type"].AsString.Split(',')[0].Trim() : "";
                    string[] allowed = ["GalgameManager.MultiStreamPage.Lists.GameListDbDto",
                        "GalgameManager.MultiStreamPage.Lists.CategoryListDbDto", "GalgameManager.MultiStreamPage.Lists.SourceListDbDto"];
                    if (!allowed.Contains(type, StringComparer.Ordinal)) throw new InvalidDataException("备份含不支持的主页列表类型。");
                    homeDatabase.GetCollection("homepage_list").Insert(document);
                }
            }
            else rebuilt = rebuilt.Where(n => n != "homepage_list").ToArray();
        }
        using (LiteDatabase database = new(databasePath))
        {
            foreach (string name in liveDb.GetCollectionNames().Where(n => !rebuilt.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<BsonDocument> records = liveDb.GetCollection(name).FindAll().ToList();
                if (records.Count > 0) database.GetCollection(name).InsertBulk(records);
            }
        }
        using (ZipArchive zip = ZipFile.Open(prepared, ZipArchiveMode.Update))
        {
            if (zip.GetEntry("data.homepage_list.json") != null && zip.GetEntry("data.dataStatus.json") is { } statusEntry)
            {
                JsonObject status;
                using (StreamReader reader = new(statusEntry.Open())) status = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
                status["MultiStreamPageLiteDbUpgrade"] = true;
                statusEntry.Delete();
                using StreamWriter statusWriter = new(zip.CreateEntry("data.dataStatus.json").Open());
                statusWriter.Write(status.ToJsonString());
            }
            zip.GetEntry("pvn_data.db")?.Delete();
            zip.CreateEntryFromFile(databasePath, "pvn_data.db");
            string pluginRoot = Path.Combine(DataRoot, "Plugins");
            if (Directory.Exists(pluginRoot))
                foreach (string file in Directory.EnumerateFiles(pluginRoot, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(file, "Plugins/" + Path.GetRelativePath(pluginRoot, file).Replace('\\', '/'));
                }
            using StreamWriter writer = new(zip.CreateEntry($"databackup-{marker}.complete").Open());
            writer.Write(marker);
        }
        JsonObject before = new(), after = new();
        foreach (var item in settings)
        {
            if (!SettingKeys.Contains(item.Key, StringComparer.Ordinal)) throw new InvalidDataException("备份包含不允许恢复的设置。");
            object? value = await ReadSettingAsync(item.Key);
            before[item.Key] = value == null ? null : JsonNode.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(value));
            after[item.Key] = item.Value?.DeepClone();
        }
        string settingsFile = Path.Combine(_stateRoot, "settings-restore.json");
        await BackupFiles.WriteJsonAsync(settingsFile, new JsonObject { ["marker"] = marker, ["before"] = before, ["after"] = after }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string temporaryTarget = target + ".preparing";
        try
        {
            File.Copy(prepared, temporaryTarget);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryTarget, target); // 同一文件系统内原子发布；半包不能被宿主识别。
        }
        finally { if (File.Exists(temporaryTarget)) File.Delete(temporaryTarget); }
        string temporaryRoot = ((DirectoryInfo)Property(_settings, "TemporaryFolder")).FullName;
        string dataPrefix = Path.GetFullPath(DataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(temporaryRoot).TrimEnd(Path.DirectorySeparatorChar).Equals(
                Path.GetFullPath(DataRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(temporaryRoot).StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            try { await RestartWithExternalTempAsync(target, stage, cancellationToken); }
            catch (Exception error)
            {
                try { WithdrawPublishedImport(target, stage, error.ToString()); }
                catch (Exception withdrawalError)
                {
                    throw new IOException("恢复重启失败，且未能撤回待导入文件；请查看插件导入目录的错误记录。",
                        new AggregateException(error, withdrawalError));
                }
                throw;
            }
            return;
        }
        string reason = await OnMainAsync(() => AppInstance.Restart("/import").ToString());
        if (!string.Equals(reason, "RestartPending", StringComparison.Ordinal))
        {
            _api.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational, "请重启 PotatoVN 完成恢复", "已准备好导入文件。", 10000);
        }
    }
    private async Task RestartWithExternalTempAsync(string importPath, string stage, CancellationToken ct)
    {
        // 已核实的上游问题：ImportWindow 会将 LocalData/Temp 移入自身子目录。
        // 仅这种非打包布局使用独立重启器，为下个宿主进程指定外部临时目录。上游修复临时目录包含关系后可移除此分支。
        string executable = Environment.ProcessPath ?? throw new NotSupportedException("无法确定 PotatoVN 程序位置，已取消本次导入准备。");
        string script = Path.Combine(stage, "restart-import.ps1");
        string config = Path.Combine(stage, "restart-import.json");
        await File.WriteAllTextAsync(script, RestartScript, new UTF8Encoding(true), ct);
        await File.WriteAllTextAsync(config, System.Text.Json.JsonSerializer.Serialize(new
        {
            ProcessId = Environment.ProcessId, Executable = executable,
            DataRoot, TempPath = Path.Combine(_stateRoot, "host-temp"),
            ErrorFile = Path.Combine(stage, "restart-import-error.txt")
        }), ct);
        ProcessStartInfo start = new("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                     "-ConfigPath", config, "-ImportPath", importPath, "-WithdrawnPath", Path.Combine(stage, "withdrawn-import.zip"),
                     "-DataRoot", DataRoot })
            start.ArgumentList.Add(argument);
        using Process helper = Process.Start(start) ?? throw new IOException("无法启动恢复重启器，已取消本次导入准备。");
        await OnMainAsync(() =>
        {
            MethodInfo close = _hostAssembly.GetType("GalgameManager.App", true)!.GetMethod("SetWindowMode", BindingFlags.Static | BindingFlags.Public)!;
            close.Invoke(null, [Enum.Parse(close.GetParameters()[0].ParameterType, "Close")]);
            return true;
        });
    }

    private void WithdrawPublishedImport(string importPath, string stage, string reason)
    {
        string source = Path.GetFullPath(importPath);
        string data = Path.GetFullPath(DataRoot).TrimEnd(Path.DirectorySeparatorChar);
        string recovery = Path.GetFullPath(stage);
        if (!string.Equals(Path.GetDirectoryName(source), data, StringComparison.OrdinalIgnoreCase) ||
            !recovery.StartsWith(Path.GetFullPath(_stateRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("撤回导入文件的路径不在预期位置。");
        // 先在原卷内改名，确保后续移动失败也不会被普通启动识别为待导入包。
        string disabled = source + ".withdrawn";
        if (File.Exists(source)) File.Move(source, disabled, true);
        File.WriteAllText(Path.Combine(recovery, "restart-import-error.txt"), reason, Encoding.UTF8);
        if (File.Exists(disabled)) File.Move(disabled, Path.Combine(recovery, "withdrawn-import.zip"), true);
    }

    private const string RestartScript = """
param(
    [Parameter(Mandatory=$true)][string]$ConfigPath,
    [Parameter(Mandatory=$true)][string]$ImportPath,
    [Parameter(Mandatory=$true)][string]$WithdrawnPath,
    [Parameter(Mandatory=$true)][string]$DataRoot
)
$ErrorActionPreference = 'Stop'
$errorFile = Join-Path ([System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($ConfigPath))) 'restart-import-error.txt'
try {
    $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $old = Get-Process -Id $config.ProcessId -ErrorAction SilentlyContinue
    if ($old -and -not $old.WaitForExit(120000)) { throw '等待 PotatoVN 退出超时，未启动导入。' }
    if (-not (Test-Path -LiteralPath $ImportPath -PathType Leaf)) { return }
    $launch = New-Object System.Diagnostics.ProcessStartInfo
    $launch.FileName = $config.Executable
    $launch.Arguments = '/import'
    $launch.WorkingDirectory = [System.IO.Path]::GetDirectoryName($config.Executable)
    $launch.UseShellExecute = $false
    $launch.EnvironmentVariables['POTATOVN_LOCALDATA_PATH'] = $config.DataRoot
    $launch.EnvironmentVariables['POTATOVN_TEMP_PATH'] = $config.TempPath
    [System.Diagnostics.Process]::Start($launch) | Out-Null
} catch {
    $failure = $_.Exception.Message
    try {
        $source = [System.IO.Path]::GetFullPath($ImportPath)
        $data = [System.IO.Path]::GetFullPath($DataRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
        $destination = [System.IO.Path]::GetFullPath($WithdrawnPath)
        $stage = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($ConfigPath))
        if (-not [System.IO.Path]::GetDirectoryName($source).Equals($data, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not $destination.StartsWith($stage.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase)) { throw '撤回导入文件的路径不在预期位置。' }
        $disabled = $source + '.withdrawn'
        if (Test-Path -LiteralPath $source -PathType Leaf) { Move-Item -LiteralPath $source -Destination $disabled -Force }
        if (Test-Path -LiteralPath $disabled -PathType Leaf) { Move-Item -LiteralPath $disabled -Destination $destination -Force }
        $failure += [Environment]::NewLine + '待导入文件已撤回，普通启动不会触发此次导入。'
    } catch {
        $failure += [Environment]::NewLine + '撤回导入文件时发生错误：' + $_.Exception.Message
    }
    $failure | Set-Content -LiteralPath $errorFile -Encoding UTF8
    exit 1
}
""";
}
