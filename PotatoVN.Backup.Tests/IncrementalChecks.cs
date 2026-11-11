using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;

internal static partial class EngineTests
{
    public static async Task<int> RunIncrementalGcOnlyAsync()
    {
        Directory.CreateDirectory(Root);
        bool completed = false;
        try
        {
            Console.WriteLine("ISOLATION explicit cleanup API + in-memory TransferProvider; all deleted paths are synthetic dictionary entries");
            await Test("增量共享对象跨设备引用保护及回收", IncrementalReferencePruneAsync);
            await Test("增量清理删除中断不损坏保留版本", IncrementalDeleteFailureAsync);
            await Test("增量清理缺失保留引用时停止且不删除", IncrementalMissingReferenceStopsCleanupAsync);
            completed = true;
            Console.WriteLine($"PASS {_passed}/3 isolated incremental cleanup checks; 0 real cloud deletes");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("隔离合成文件保留于 " + Root);
            return 1;
        }
        finally
        {
            if (completed)
            {
                string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PotatoVNBackupTests")) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(Root).StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("隔离清理目录越界。");
                Directory.Delete(Root, true);
            }
        }
    }

    public static async Task<int> RunRemoteCommitOnlyAsync()
    {
        Directory.CreateDirectory(Root);
        bool completed = false;
        try
        {
            Console.WriteLine("ISOLATION RemoteCommit + TransferProvider memory dictionary; temporary synthetic payloads only; 0 real cloud operations");
            await Test("提交协议半写payload保持未提交", CommitHalfPayloadAsync);
            await Test("提交协议半写JSON标记保持未提交", CommitHalfMarkerAsync);
            await Test("提交协议旧已发布对象修复中断撤销旧标记", CommitRepairFailureAsync);
            await Test("提交协议重试完成后下载校验通过", CommitRetryDownloadAsync);
            await Test("提交协议取消下载保留已有本机目标", CommitCancelledDownloadAsync);
            completed = true;
            Console.WriteLine($"PASS {_passed}/5 isolated remote-commit checks; 0 real cloud operations");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("隔离合成文件保留于 " + Root);
            return 1;
        }
        finally
        {
            if (completed)
            {
                string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PotatoVNBackupTests")) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(Root).StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("隔离清理目录越界。");
                Directory.Delete(Root, true);
            }
        }
    }

    private static async Task CommitHalfPayloadAsync()
    {
        var fixture = await CommitFixtureAsync();
        fixture.Remote.BeforeUpload = (path, bytes, _) =>
        {
            if (path != fixture.Path) return;
            fixture.Remote.Files[path] = bytes[..(bytes.Length / 2)];
            throw new IOException("synthetic half payload");
        };
        await Throws<IOException>(() => RemoteCommit.UploadAsync(fixture.Provider, fixture.Path, fixture.Local, fixture.Work));
        Check(fixture.Remote.Files.ContainsKey(fixture.Path) && !fixture.Remote.Files.ContainsKey(fixture.Path + ".complete"), "半写payload存在可用提交标记。");
        await AssertUncommittedAsync(fixture);
    }

    private static async Task CommitHalfMarkerAsync()
    {
        var fixture = await CommitFixtureAsync();
        fixture.Remote.BeforeUpload = (path, bytes, _) =>
        {
            if (path != fixture.Path + ".complete") return;
            fixture.Remote.Files[path] = bytes[..(bytes.Length / 2)];
            throw new IOException("synthetic half JSON marker");
        };
        await Throws<IOException>(() => RemoteCommit.UploadAsync(fixture.Provider, fixture.Path, fixture.Local, fixture.Work));
        Check(fixture.Remote.Files[fixture.Path].SequenceEqual(fixture.Bytes) && fixture.Remote.Files.ContainsKey(fixture.Path + ".complete"),
            "未生成完整payload加半JSON标记的故障场景。");
        await AssertUncommittedAsync(fixture);
    }

    private static async Task CommitRepairFailureAsync()
    {
        var fixture = await CommitFixtureAsync();
        await RemoteCommit.UploadAsync(fixture.Provider, fixture.Path, fixture.Local, fixture.Work);
        Check(await RemoteCommit.IsReadyAsync(fixture.Provider, fixture.Path, fixture.Work), "初次完整上传未提交。");
        fixture.Remote.Files.Remove(fixture.Path);
        fixture.Remote.BeforeUpload = (path, bytes, _) =>
        {
            if (path != fixture.Path) return;
            fixture.Remote.Files[path] = bytes[..(bytes.Length / 2)];
            throw new IOException("repair upload interrupted");
        };
        await Throws<IOException>(() => RemoteCommit.UploadAsync(fixture.Provider, fixture.Path, fixture.Local, fixture.Work));
        Check(fixture.Remote.Deletes.Contains(fixture.Path + ".complete") && !fixture.Remote.Files.ContainsKey(fixture.Path + ".complete"),
            "修复旧对象之前未移除旧complete标记。");
        await AssertUncommittedAsync(fixture);
    }

    private static async Task CommitRetryDownloadAsync()
    {
        var fixture = await CommitFixtureAsync();
        fixture.Remote.Files[fixture.Path] = fixture.Bytes[..(fixture.Bytes.Length / 2)];
        fixture.Remote.Files[fixture.Path + ".complete"] = Encoding.UTF8.GetBytes("{\"FormatVersion\":2,");
        await RemoteCommit.UploadAsync(fixture.Provider, fixture.Path, fixture.Local, fixture.Work);
        RemoteCommitInfo? marker = await RemoteCommit.ReadMarkerAsync(fixture.Provider, fixture.Path, fixture.Work);
        Check(marker != null && marker.Length == fixture.Bytes.LongLength && marker.Sha256.Equals(Sha(fixture.Bytes), StringComparison.OrdinalIgnoreCase),
            "成功提交标记与实际payload字节不一致。");
        string target = Path.Combine(Work(), "verified-payload.bin");
        Check(await RemoteCommit.DownloadIfCommittedAsync(fixture.Provider, fixture.Path, target, fixture.Work) &&
            (await File.ReadAllBytesAsync(target)).SequenceEqual(fixture.Bytes), "重试成功后不能下载并验证完整payload。");
    }

    private static async Task CommitCancelledDownloadAsync()
    {
        var fixture = await CommitFixtureAsync();
        await RemoteCommit.UploadAsync(fixture.Provider, fixture.Path, fixture.Local, fixture.Work);
        byte[] original = Encoding.UTF8.GetBytes("previous local target remains intact");
        string target = Path.Combine(Work(), "existing-local.bin"); await File.WriteAllBytesAsync(target, original);
        using var cancellation = new CancellationTokenSource();
        fixture.Remote.BeforeDownload = (path, local, token) =>
        {
            if (path != fixture.Path) return;
            File.WriteAllBytes(local, fixture.Bytes[..(fixture.Bytes.Length / 2)]);
            cancellation.Cancel(); token.ThrowIfCancellationRequested();
        };
        await Throws<OperationCanceledException>(() => RemoteCommit.DownloadIfCommittedAsync(fixture.Provider, fixture.Path, target, fixture.Work, cancellation.Token));
        Check((await File.ReadAllBytesAsync(target)).SequenceEqual(original), "取消半下载覆盖了已有本机目标。");
    }

    private static async Task AssertUncommittedAsync(CommitFixture fixture)
    {
        fixture.Remote.BeforeUpload = null;
        Check(await RemoteCommit.ReadMarkerAsync(fixture.Provider, fixture.Path, fixture.Work) == null &&
            !await RemoteCommit.IsReadyAsync(fixture.Provider, fixture.Path, fixture.Work), "未完成的对象被视为ready。");
        int downloadStart = fixture.Remote.Downloads.Count;
        string target = Path.Combine(Work(), "uncommitted-target.bin"); byte[] original = Encoding.UTF8.GetBytes("original local bytes");
        await File.WriteAllBytesAsync(target, original);
        Check(!await RemoteCommit.DownloadIfCommittedAsync(fixture.Provider, fixture.Path, target, fixture.Work) &&
            (await File.ReadAllBytesAsync(target)).SequenceEqual(original) && !fixture.Remote.Downloads.Skip(downloadStart).Contains(fixture.Path),
            "未提交对象触发payload下载或改写本机目标。");
    }

    private static async Task<CommitFixture> CommitFixtureAsync()
    {
        byte[] bytes = RandomBytes(2577, 63); string root = Work(); string local = Path.Combine(root, "synthetic-payload.gz");
        await File.WriteAllBytesAsync(local, bytes);
        string hash = Sha(bytes); string path = $"v2/objects/{Guid.NewGuid():N}/{hash[..2]}/{hash}.gz";
        var remote = new TransferRemote();
        return new CommitFixture(root, local, path, bytes, remote, new TransferProvider(remote));
    }
    private sealed record CommitFixture(string Work, string Local, string Path, byte[] Bytes, TransferRemote Remote, TransferProvider Provider);

    public static async Task<int> RunIncrementalStoreOnlyAsync()
    {
        Directory.CreateDirectory(Root);
        bool completed = false;
        try
        {
            Console.WriteLine("ISOLATION provider=TransferProvider (in-memory Dictionary); files=" + Root);
            Console.WriteLine("ISOLATION no cloud endpoint, HTTP client, credentials, host export or Coordinator invocation");
            await Test("对象存储首次上传及压缩字节计量", StoreInitialOnlyAsync);
            await Test("对象存储跨实例原样复用及ZIP时间差异", StoreUnchangedOnlyAsync);
            await Test("对象存储修改一个小文件只上传一个对象", StoreSingleChangeOnlyAsync);
            await Test("对象存储断网半对象及取消重试", StoreInterruptedOnlyAsync);
            await Test("对象存储ZIP完整重建及损坏时保留原目标", StoreRestoreOnlyAsync);
            completed = true;
            Console.WriteLine($"PASS {_passed}/5 isolated incremental-store checks; 0 real cloud operations");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("隔离合成文件保留于 " + Root);
            return 1;
        }
        finally
        {
            if (completed)
            {
                string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PotatoVNBackupTests")) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(Root).StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("隔离清理目录越界。");
                Directory.Delete(Root, true);
            }
        }
    }

    public static async Task<int> RunIncrementalIntegrationOnlyAsync()
    {
        Directory.CreateDirectory(Root);
        bool completed = false;
        try
        {
            Console.WriteLine("ISOLATION independent v2 Coordinator entries; TransferProvider memory dictionary; temporary synthetic files only");
            await Test("增量首次上传对象并完整恢复", IncrementalInitialAsync);
            await Test("增量无变化跨实例不重复传对象", IncrementalNoChangeAsync);
            await Test("增量仅修改一个小存档只传一个对象", IncrementalSingleSaveChangeAsync);
            await Test("增量删除及重命名不重传且历史完整", IncrementalDeleteRenameAsync);
            await Test("增量资料更新不重传封面或存档", IncrementalStableMediaAsync);
            await Test("增量部分选择不携带未选引用或字段", IncrementalSelectionAsync);
            await Test("增量断网与取消不发布半成品", IncrementalInterruptedAsync);
            await Test("旧v1完整恢复及新v2独立复制", IncrementalLegacyRestoreAsync);
            await Test("增量缺失对象补传后可恢复", IncrementalMissingObjectAsync);
            await Test("增量失败残留同名半对象必须重传", IncrementalPartialObjectRetryAsync);
            await Test("已发布对象缺失且修复半写后重试", IncrementalPublishedRepairRetryAsync);
            await Test("未提交半清单不阻断已发布历史读取", IncrementalPartialManifestAsync);
            await Test("增量存档本机适配与重暂存保留确认状态", IncrementalLocalAdapterAsync);
            completed = true;
            Console.WriteLine($"PASS {_passed}/13 isolated incremental integration checks; cleanup integration intentionally excluded");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("隔离合成文件保留于 " + Root);
            return 1;
        }
        finally
        {
            if (completed)
            {
                string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PotatoVNBackupTests")) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(Root).StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("隔离清理目录越界。");
                Directory.Delete(Root, true);
            }
        }
    }

    private static async Task StoreInitialOnlyAsync()
    {
        var fixture = await StoreFixtureAsync();
        var store = new IncrementalArchiveStore(new TransferProvider(fixture.Remote), Work());
        ArchiveRecipe recipe = await store.UploadAsync(fixture.Archive, fixture.Owner);
        var transfers = fixture.Remote.ObjectTransfers(0);
        Check(store.UploadedFiles == fixture.Files.Count && store.ReusedFiles == 0 && transfers.Count == fixture.Files.Count, "首次上传对象数错误。");
        Check(store.UploadedBytes == transfers.Sum(item => item.Bytes) && store.UploadedBytes > 0, "上传压缩字节统计与实际内存provider传输不一致。");
        AssertStoreFiles(fixture.Files, ValidateRecipeBytes(fixture.Remote, recipe));
        Console.WriteLine($"MEASURE initial objects={store.UploadedFiles}, compressedBytes={store.UploadedBytes}, reused={store.ReusedFiles}");
    }

    private static async Task StoreUnchangedOnlyAsync()
    {
        var fixture = await StoreFixtureAsync();
        var first = new IncrementalArchiveStore(new TransferProvider(fixture.Remote), Work());
        ArchiveRecipe recipe = await first.UploadAsync(fixture.Archive, fixture.Owner);
        string changedZip = await WriteStoreZipAsync(fixture.Files, CompressionLevel.NoCompression, 2022);
        Check(await BackupFiles.HashAsync(fixture.Archive) != await BackupFiles.HashAsync(changedZip), "测试ZIP字节和时间戳未实际变化。");
        int checkpoint = fixture.Remote.Transfers.Count;
        var restarted = new IncrementalArchiveStore(new TransferProvider(fixture.Remote), Work(), RecipeObjectPaths(recipe).ToHashSet(StringComparer.Ordinal));
        ArchiveRecipe second = await restarted.UploadAsync(changedZip, fixture.Owner);
        Check(restarted.UploadedFiles == 0 && restarted.UploadedBytes == 0 && restarted.ReusedFiles == fixture.Files.Count && fixture.Remote.ObjectTransfers(checkpoint).Count == 0,
            "同内容不同ZIP时间戳/压缩方式导致重复上传。");
        AssertStoreFiles(fixture.Files, ValidateRecipeBytes(fixture.Remote, second));
        Console.WriteLine($"MEASURE unchanged objects=0, compressedBytes=0, reused={restarted.ReusedFiles}");
    }

    private static async Task StoreSingleChangeOnlyAsync()
    {
        var fixture = await StoreFixtureAsync();
        var first = new IncrementalArchiveStore(new TransferProvider(fixture.Remote), Work());
        ArchiveRecipe recipe = await first.UploadAsync(fixture.Archive, fixture.Owner);
        var changed = fixture.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        changed["roots/save-root/slot.dat"] = Encoding.UTF8.GetBytes("one small save changed");
        string archive = await WriteStoreZipAsync(changed, CompressionLevel.Optimal, 2024);
        int checkpoint = fixture.Remote.Transfers.Count;
        var next = new IncrementalArchiveStore(new TransferProvider(fixture.Remote), Work(), RecipeObjectPaths(recipe).ToHashSet(StringComparer.Ordinal));
        ArchiveRecipe result = await next.UploadAsync(archive, fixture.Owner);
        var transfers = fixture.Remote.ObjectTransfers(checkpoint);
        Check(next.UploadedFiles == 1 && next.ReusedFiles == fixture.Files.Count - 1 && transfers.Count == 1 &&
            transfers[0].Path.EndsWith(Sha(changed["roots/save-root/slot.dat"]) + ".gz", StringComparison.Ordinal), "修改一个小文件时上传了未变化的大文件/图片。");
        Check(next.UploadedBytes == transfers[0].Bytes, "小文件压缩传输字节统计错误。");
        AssertStoreFiles(changed, ValidateRecipeBytes(fixture.Remote, result));
        Console.WriteLine($"MEASURE single-change objects=1, compressedBytes={next.UploadedBytes}, reused={next.ReusedFiles}");
    }

    private static async Task StoreInterruptedOnlyAsync()
    {
        var fixture = await StoreFixtureAsync();
        string? partialPath = null;
        fixture.Remote.BeforeUpload = (path, bytes, _) =>
        {
            partialPath = path;
            int length = Math.Max(1, bytes.Length / 2);
            fixture.Remote.Files[path] = bytes[..length];
            fixture.Remote.Transfers.Add(new UploadTransfer(path, length, false));
            throw new IOException("simulated interrupted object upload");
        };
        var failed = new IncrementalArchiveStore(new TransferProvider(fixture.Remote), Work());
        await Throws<IOException>(() => failed.UploadAsync(fixture.Archive, fixture.Owner));
        Check(partialPath != null && fixture.Remote.Files.ContainsKey(partialPath) && failed.UploadedFiles == 0, "失败统计错误或没有实际留下同名半对象。");
        fixture.Remote.BeforeUpload = null;
        int checkpoint = fixture.Remote.Transfers.Count;
        var retry = new IncrementalArchiveStore(new TransferProvider(fixture.Remote), Work(), new HashSet<string>(StringComparer.Ordinal));
        ArchiveRecipe result = await retry.UploadAsync(fixture.Archive, fixture.Owner);
        Check(fixture.Remote.ObjectTransfers(checkpoint).Count(item => item.Path == partialPath) == 1 && retry.UploadedFiles == fixture.Files.Count,
            "没有已提交引用的同名半对象被错误复用。");
        AssertStoreFiles(fixture.Files, ValidateRecipeBytes(fixture.Remote, result));

        var cancelled = await StoreFixtureAsync();
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        cancelled.Remote.BeforeUpload = (path, _, token) => { if (IsObject(path) && ++calls == 2) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); } };
        var interrupted = new IncrementalArchiveStore(new TransferProvider(cancelled.Remote), Work());
        await Throws<OperationCanceledException>(() => interrupted.UploadAsync(cancelled.Archive, cancelled.Owner, cancellation.Token));
        Check(interrupted.UploadedFiles == 1 && interrupted.ReusedFiles == 0, "取消未发生在一个对象上传成功之后。");
        cancelled.Remote.BeforeUpload = null;
        var afterCancel = new IncrementalArchiveStore(new TransferProvider(cancelled.Remote), Work());
        ArchiveRecipe complete = await afterCancel.UploadAsync(cancelled.Archive, cancelled.Owner);
        Check(afterCancel.UploadedFiles == cancelled.Files.Count, "取消留下但未提交的对象被跨实例盲目复用。");
        AssertStoreFiles(cancelled.Files, ValidateRecipeBytes(cancelled.Remote, complete));
    }

    private static async Task StoreRestoreOnlyAsync()
    {
        var fixture = await StoreFixtureAsync();
        ArchiveRecipe recipe = await new IncrementalArchiveStore(new TransferProvider(fixture.Remote), Work()).UploadAsync(fixture.Archive, fixture.Owner);
        string restored = Path.Combine(Work(), "restored.zip");
        await new IncrementalArchiveStore(new TransferProvider(fixture.Remote), Work()).RestoreAsync(recipe, restored);
        using (ZipArchive archive = ZipFile.OpenRead(restored))
        {
            Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                using Stream source = entry.Open(); using MemoryStream bytes = new(); await source.CopyToAsync(bytes);
                files.Add(entry.FullName, bytes.ToArray());
            }
            AssertStoreFiles(fixture.Files, files);
        }
        string again = Path.Combine(Work(), "restored-again.zip");
        await new IncrementalArchiveStore(new TransferProvider(fixture.Remote), Work()).RestoreAsync(recipe, again);
        Check(await BackupFiles.HashAsync(restored) == await BackupFiles.HashAsync(again), "相同配方重建ZIP字节不确定。");
        string damaged = RecipeObjectPaths(recipe).First();
        byte[] bad = fixture.Remote.Files[damaged].ToArray(); bad[^8] ^= 0x40; fixture.Remote.Files[damaged] = bad;
        byte[] original = Encoding.UTF8.GetBytes("existing destination must survive corruption");
        string protectedZip = Path.Combine(Work(), "existing.zip"); await File.WriteAllBytesAsync(protectedZip, original);
        await Throws<InvalidDataException>(() => new IncrementalArchiveStore(new TransferProvider(fixture.Remote), Work()).RestoreAsync(recipe, protectedZip));
        Check((await File.ReadAllBytesAsync(protectedZip)).SequenceEqual(original), "损坏对象覆盖了已有恢复目标。");
    }

    private static async Task<(Guid Owner, string Archive, Dictionary<string, byte[]> Files, TransferRemote Remote)> StoreFixtureAsync()
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            ["data/games.json"] = Encoding.UTF8.GetBytes("{\"review\":\"synthetic\"}"),
            ["media/cover.png"] = RandomBytes(4096, 43),
            ["roots/save-root/slot.dat"] = Encoding.UTF8.GetBytes("initial synthetic slot"),
            ["roots/save-root/large.bin"] = RandomBytes(256 * 1024, 44)
        };
        return (Guid.NewGuid(), await WriteStoreZipAsync(files, CompressionLevel.Optimal, 2020), files, new TransferRemote());
    }

    private static async Task<string> WriteStoreZipAsync(IReadOnlyDictionary<string, byte[]> files, CompressionLevel compression, int year)
    {
        string path = Path.Combine(Work(), "synthetic.zip");
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ZipArchiveEntry entry = archive.CreateEntry(file.Key, compression);
            entry.LastWriteTime = new DateTimeOffset(year, 1, 2, 3, 4, 6, TimeSpan.Zero);
            using Stream output = entry.Open(); await output.WriteAsync(file.Value);
        }
        return path;
    }

    private static void AssertStoreFiles(IReadOnlyDictionary<string, byte[]> expected, IReadOnlyDictionary<string, byte[]> actual)
    {
        Check(expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out byte[]? bytes) && pair.Value.SequenceEqual(bytes)),
            "对象配方/重建ZIP与合成原始文件内容不一致。");
    }

    private const BackupContent IncrementalContent = BackupContent.GameInfo | BackupContent.Covers |
        BackupContent.Reviews | BackupContent.PlayTime | BackupContent.Settings | BackupContent.Saves;

    private static async Task IncrementalInitialAsync()
    {
        var fixture = NewIncremental();
        var snapshot = await fixture.Coordinator.BackupIncrementalAsync();
        Check(snapshot.FormatVersion == 2 && snapshot.ManifestPath.StartsWith("v2/snapshots/"), "新备份未使用v2清单。");
        Check(snapshot.Data == null && snapshot.Media == null && snapshot.DataRecipe != null && snapshot.MediaRecipe != null,
            "增量软件备份仍引用完整ZIP。");
        SaveManifest save = fixture.Remote.Read<SaveManifest>(snapshot.SaveManifestPaths.Single());
        Check(save.FormatVersion == 2 && save.Recipe != null && save.ArchivePath.Length == 0 && save.Sha256.Length == 0,
            "增量存档仍引用云端完整ZIP。");
        Check(save.Length == save.Files.Sum(file => file.Length), "增量存档长度不是原始文件总长。");
        Check(JsonSerializer.Serialize(save.Recipe!.Files, BackupJson.Options) == JsonSerializer.Serialize(save.Files, BackupJson.Options),
            "存档配方与文件校验清单不一致。");
        var referenced = RecipeObjectPaths(snapshot.DataRecipe!).Concat(RecipeObjectPaths(snapshot.MediaRecipe!))
            .Concat(RecipeObjectPaths(save.Recipe)).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var transfers = fixture.Remote.ObjectTransfers(0);
        Check(referenced.SetEquals(transfers.Select(transfer => transfer.Path)) && transfers.Count == referenced.Count,
            "首次对象上传与快照实际引用不一致，或同内容对象被重复上传。");
        Check(transfers.Sum(transfer => transfer.Bytes) == referenced.Sum(path => fixture.Remote.Files[path].LongLength),
            "计量上传字节与实际对象字节不一致。");
        Check(fixture.Remote.Successful.Last().Path == snapshot.ManifestPath + ".complete", "最终软件清单未最后发布。");
        Check(!fixture.Remote.Files.Keys.Any(path => path.EndsWith(".zip", StringComparison.Ordinal)), "新v2备份上传了完整ZIP。");
        ValidateRecipeBytes(fixture.Remote, snapshot.DataRecipe!);
        ValidateRecipeBytes(fixture.Remote, snapshot.MediaRecipe!);
        ValidateRecipeBytes(fixture.Remote, save.Recipe);
        await AssertIncrementalSaveRestoreAsync(fixture, snapshot, fixture.LargeBytes, fixture.SlotBytes);
        Console.WriteLine($"TRANSFER initial: {transfers.Count} objects, {transfers.Sum(transfer => transfer.Bytes)} compressed bytes");
    }

    private static async Task IncrementalNoChangeAsync()
    {
        var fixture = NewIncremental();
        var first = await fixture.Coordinator.BackupIncrementalAsync();
        int checkpoint = fixture.Remote.Transfers.Count;
        // 新建Coordinator并重建导出ZIP（含不同时间戳），排除仅依赖进程内缓存的假增量。
        BackupSettings reloaded = JsonSerializer.Deserialize<BackupSettings>(JsonSerializer.Serialize(fixture.Settings, BackupJson.Options), BackupJson.Options)!;
        var restarted = IncrementalCoordinator(Work(), fixture.Host, reloaded, fixture.Remote);
        var second = await restarted.BackupIncrementalAsync();
        var transfers = fixture.Remote.Successful.Skip(checkpoint).ToList();
        Check(fixture.Remote.ObjectTransfers(checkpoint).Count == 0, "无变化跨实例备份重复传输了对象。");
        Check(transfers.Count > 0 && transfers.All(transfer => transfer.Path.EndsWith("/manifest.json", StringComparison.Ordinal) || transfer.Path.EndsWith("/manifest.json.complete", StringComparison.Ordinal)),
            "无变化备份除清单外仍上传了数据。");
        Check(second.SaveManifestPaths.Single() == first.SaveManifestPaths.Single(), "无变化存档未复用旧清单。");
        Check(fixture.Host.ExportCalls >= 2 && first.ManifestPath != second.ManifestPath, "没有真正执行第二次导出与备份。");
        Console.WriteLine("TRANSFER unchanged: 0 objects, 0 compressed bytes; manifest only");
    }

    private static async Task IncrementalSingleSaveChangeAsync()
    {
        var fixture = NewIncremental();
        var first = await fixture.Coordinator.BackupIncrementalAsync();
        SaveManifest oldSave = fixture.Remote.Read<SaveManifest>(first.SaveManifestPaths.Single());
        byte[] changed = Encoding.UTF8.GetBytes("one small slot changed, other files unchanged");
        await File.WriteAllBytesAsync(fixture.SlotPath, changed);
        int checkpoint = fixture.Remote.Transfers.Count;
        var second = await fixture.Coordinator.BackupIncrementalAsync();
        var transfers = fixture.Remote.ObjectTransfers(checkpoint);
        Check(transfers.Count == 1 && transfers[0].Path.EndsWith(Sha(changed) + ".gz", StringComparison.Ordinal),
            "只修改一个小存档却上传了其他对象。");
        SaveManifest newSave = fixture.Remote.Read<SaveManifest>(second.SaveManifestPaths.Single());
        FileRecord large = newSave.Recipe!.Files.Single(file => file.Path.EndsWith("large.bin", StringComparison.Ordinal));
        Check(IncrementalObjectPath(oldSave.Recipe!, oldSave.Recipe!.Files.Single(file => file.Path.EndsWith("large.bin", StringComparison.Ordinal))) ==
            IncrementalObjectPath(newSave.Recipe, large), "未变化大文件的对象地址未复用。");
        await AssertIncrementalSaveRestoreAsync(fixture, second, fixture.LargeBytes, changed);
        Console.WriteLine($"TRANSFER one-slot-change: 1 object, {transfers[0].Bytes} compressed bytes");
    }

    private static async Task IncrementalDeleteRenameAsync()
    {
        var fixture = NewIncremental(BackupContent.Saves);
        SnapshotManifest first = await fixture.Coordinator.BackupIncrementalAsync();
        string allowedRoot = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        Check(Path.GetFullPath(fixture.LargePath).StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFullPath(fixture.SlotPath).StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase), "删除/重命名测试必须局限于随机合成根目录。");
        File.Delete(fixture.SlotPath);
        string renamed = Path.Combine(Path.GetDirectoryName(fixture.LargePath)!, "renamed.bin");
        File.Move(fixture.LargePath, renamed);
        int checkpoint = fixture.Remote.Transfers.Count;
        SnapshotManifest next = await fixture.Coordinator.BackupIncrementalAsync();
        SaveManifest save = fixture.Remote.Read<SaveManifest>(next.SaveManifestPaths.Single());
        Check(save.Files.Count == 1 && save.Files[0].Path.EndsWith("renamed.bin", StringComparison.Ordinal) &&
            save.Recipe!.Files.Count == 1 && save.Recipe.Files[0].Path == save.Files[0].Path,
            "新完整配方没有反映存档删除或重命名。");
        Check(fixture.Remote.ObjectTransfers(checkpoint).Count == 0, "删除文件或仅重命名未变内容仍重传了GZip payload。");
        var metadata = fixture.Remote.Transfers.Skip(checkpoint).Where(transfer => transfer.Completed && !IsObject(transfer.Path)).ToList();
        Check(metadata.Count > 0, "删除/重命名没有发布新的完整清单。");
        var target = Game(fixture.Game.Id, Work());
        string targetSave = Path.Combine(target.LocalDirectory!, "savedata"); Directory.CreateDirectory(targetSave);
        await File.WriteAllBytesAsync(Path.Combine(targetSave, "slot.dat"), fixture.SlotBytes);
        await File.WriteAllBytesAsync(Path.Combine(targetSave, "large.bin"), fixture.LargeBytes);
        var restored = IncrementalCoordinator(Work(), new IncrementalHost([target]), RestoreSettings(fixture), fixture.Remote);
        await restored.StageIncrementalRestoreAsync(next, BackupContent.Saves);
        restored.Pending.Refresh([target], restored.Settings.GameMappings);
        PendingRestore pending = restored.Pending.Items.Single();
        await SaveArchiveService.RestoreAsync(pending, restored.Pending.ArchivePath(pending.Manifest), target, Work());
        Check(!File.Exists(Path.Combine(targetSave, "slot.dat")) && !File.Exists(Path.Combine(targetSave, "large.bin")) &&
            (await File.ReadAllBytesAsync(Path.Combine(targetSave, "renamed.bin"))).SequenceEqual(fixture.LargeBytes),
            "恢复最新版本复活了已删文件/旧文件名，或重命名内容错误。");
        await AssertIncrementalSaveRestoreAsync(fixture, first, fixture.LargeBytes, fixture.SlotBytes);
        Console.WriteLine($"TRANSFER rename-delete: 0 GZip objects, {metadata.Count} metadata files, {metadata.Sum(transfer => transfer.Bytes)} metadata bytes");
    }

    private static async Task IncrementalStableMediaAsync()
    {
        var fixture = NewIncremental();
        var first = await fixture.Coordinator.BackupIncrementalAsync();
        var stableObjects = RecipeObjectPaths(first.MediaRecipe!).Concat(
            RecipeObjectPaths(fixture.Remote.Read<SaveManifest>(first.SaveManifestPaths.Single()).Recipe!)).ToHashSet(StringComparer.Ordinal);
        fixture.Host.Comment = "updated review only";
        fixture.Host.PlayCount = 12;
        int checkpoint = fixture.Remote.Transfers.Count;
        var second = await fixture.Coordinator.BackupIncrementalAsync();
        var transfers = fixture.Remote.ObjectTransfers(checkpoint);
        Check(transfers.Count == 1 && transfers.All(transfer => !stableObjects.Contains(transfer.Path)),
            "资料变化时重复上传了封面或存档，或不只传了变化的资料文件。");
        Check(RecipeObjectPaths(first.MediaRecipe!).SequenceEqual(RecipeObjectPaths(second.MediaRecipe!)), "未变化封面引用发生改变。");
        Check(first.SaveManifestPaths.Single() == second.SaveManifestPaths.Single(), "资料变化导致无变化存档生成新版本。");
    }

    private static async Task IncrementalSelectionAsync()
    {
        var fixture = NewIncremental();
        await fixture.Coordinator.BackupIncrementalAsync();
        fixture.Settings.Content = BackupContent.Reviews;
        var selected = await fixture.Coordinator.BackupIncrementalAsync();
        Check(selected.SaveManifestPaths.Count == 0 && selected.MediaRecipe == null && selected.Media == null,
            "部分选择继续携带未选存档或图片引用。");
        JsonObject fields = RecipeJson(fixture.Remote, selected.DataRecipe!, "games.json")[0]!["Fields"]!.AsObject();
        Check(fields.ContainsKey("Comment") && !fields.ContainsKey("Description") && !fields.ContainsKey("ImagePath") && !fields.ContainsKey("PlayCount"),
            "增量配方引用的数据泄露未选字段。");
        var local = Game(fixture.Game.Id, Work());
        var host = new IncrementalHost([local]) { Description = "keep local description", PlayCount = 99, Comment = "local", CoverBytes = RandomBytes(4096, 57) };
        byte[] localCover = host.CoverBytes;
        var restored = IncrementalCoordinator(Work(), host, RestoreSettings(fixture), fixture.Remote);
        await restored.StageIncrementalRestoreAsync(selected, BackupContent.Reviews);
        JsonNode imported = host.ImportedGames!.Single()!;
        Check(imported["Comment"]!.GetValue<string>() == fixture.Host.Comment && imported["PlayCount"]!.GetValue<int>() == 99 &&
            imported["Description"]!["Value"]!.GetValue<string>() == "keep local description", "部分恢复覆盖了未选本机字段。");
        Check(host.ImportedCoverBytes!.SequenceEqual(localCover), "未选择封面时恢复改写了本机封面。");
    }

    private static async Task IncrementalInterruptedAsync()
    {
        foreach (bool cancel in new[] { false, true })
        {
            var fixture = NewIncremental(BackupContent.Saves);
            var first = await fixture.Coordinator.BackupIncrementalAsync();
            await File.WriteAllTextAsync(fixture.SlotPath, "changed slot for failure");
            await File.WriteAllBytesAsync(fixture.LargePath, RandomBytes(256 * 1024, 22));
            DateTimeOffset? successfulBefore = fixture.Settings.LastSuccessfulBackupUtc;
            int objectCalls = 0;
            using var cancellation = new CancellationTokenSource();
            fixture.Remote.BeforeUpload = (path, _, token) =>
            {
                if (!IsObject(path) || ++objectCalls != 2) return;
                if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                throw new IOException("injected network break after one committed object");
            };
            if (cancel) await Throws<OperationCanceledException>(() => fixture.Coordinator.BackupIncrementalAsync(cancellation.Token));
            else await Throws<IOException>(() => fixture.Coordinator.BackupIncrementalAsync());
            fixture.Remote.BeforeUpload = null;
            Check(objectCalls == 2, "故障没有发生在已有一个对象上传成功之后。");
            var visible = await fixture.Coordinator.ListIncrementalSnapshotsAsync();
            Check(visible.Count == 1 && visible[0].ManifestPath == first.ManifestPath && fixture.Settings.LastSuccessfulBackupUtc == successfulBefore,
                "失败或取消发布了半成品快照，或错误更新最近成功时间。");
            await AssertIncrementalSaveRestoreAsync(fixture, first, fixture.LargeBytes, fixture.SlotBytes);
        }
    }

    private static async Task IncrementalLegacyRestoreAsync()
    {
        var fixture = NewIncremental();
        string work = Work();
        HostExport export = await fixture.Host.ExportAsync(Path.Combine(work, "export"), CancellationToken.None);
        SoftwarePackage package = await SoftwareBackupCodec.CreateAsync(export, fixture.Settings, [fixture.Game], Path.Combine(work, "projected"));
        string archive = Path.Combine(work, "save.zip");
        SaveManifest save = (await SaveArchiveService.CreateAsync(fixture.Game, fixture.Settings.SaveConfigs[0].Locations, archive, fixture.Settings.DeviceId))!;
        save.ManifestPath = $"v1/saves/{save.Game.Id:N}/{save.DeviceId:N}/{save.Id}/manifest.json";
        save.ArchivePath = save.ManifestPath.Replace("manifest.json", "save.zip", StringComparison.Ordinal);
        fixture.Remote.Files[save.ArchivePath] = await File.ReadAllBytesAsync(archive);
        fixture.Remote.Put(save.ManifestPath, save);
        var legacy = new SnapshotManifest
        {
            FormatVersion = 1, Id = "legacy_" + Guid.NewGuid().ToString("N"), DeviceId = fixture.Settings.DeviceId,
            DeviceName = "legacy device", CreatedUtc = DateTimeOffset.UtcNow, Content = fixture.Settings.Content,
            Games = [fixture.Game], SaveManifestPaths = [save.ManifestPath]
        };
        string prefix = $"v1/snapshots/{legacy.DeviceId:N}/{legacy.Id}";
        legacy.ManifestPath = prefix + "/manifest.json";
        legacy.Data = await SeedLegacyFileAsync(fixture.Remote, prefix + "/data.zip", package.DataPath!);
        legacy.Media = await SeedLegacyFileAsync(fixture.Remote, prefix + "/media.zip", package.MediaPath!);
        fixture.Remote.Put(legacy.ManifestPath, legacy);
        Check((await fixture.Coordinator.ListIncrementalSnapshotsAsync()).Single().FormatVersion == 1, "旧v1快照不可列出。");
        var target = Game(fixture.Game.Id, Work());
        var host = new IncrementalHost([target]) { Comment = "local before legacy restore", CoverBytes = RandomBytes(4096, 58) };
        var restored = IncrementalCoordinator(Work(), host, RestoreSettings(fixture), fixture.Remote);
        await restored.StageIncrementalRestoreAsync(legacy, legacy.Content);
        Check(host.ImportedGames!.Single()!["Comment"]!.GetValue<string>() == fixture.Host.Comment && host.ImportedCoverBytes!.SequenceEqual(fixture.Host.CoverBytes),
            "v1资料或封面未正确恢复。");
        await RestorePendingAndCheckAsync(restored, target, fixture.LargeBytes, fixture.SlotBytes);
        var originals = fixture.Remote.Files.Where(pair => pair.Key.StartsWith("v1/", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        BackupSettings freshSettings = RestoreSettings(fixture);
        freshSettings.Content = BackupContent.Saves;
        var fresh = IncrementalCoordinator(Work(), new IncrementalHost([Game(fixture.Game.Id)]), freshSettings, fixture.Remote);
        SnapshotManifest copiedSnapshot = await fresh.BackupIncrementalAsync();
        SaveManifest copied = fixture.Remote.Read<SaveManifest>(copiedSnapshot.SaveManifestPaths.Single());
        Check(copied.FormatVersion == 2 && copied.ManifestPath.StartsWith($"v2/saves/{fixture.Game.Id:N}/{freshSettings.DeviceId:N}/", StringComparison.Ordinal) &&
            copied.DeviceId == freshSettings.DeviceId && copied.SourceDeviceId == save.DeviceId && copied.CapturedUtc == save.CapturedUtc &&
            copied.Recipe?.DeviceId == freshSettings.DeviceId,
            "无本机存档时未建立当前设备独立v2副本，或丢失旧原设备/备份时间。");
        Check(originals.All(pair => fixture.Remote.Files.TryGetValue(pair.Key, out byte[]? bytes) && bytes.SequenceEqual(pair.Value)),
            "复制v1为v2时修改了旧客户端原件。");
        var next = await fixture.Coordinator.BackupIncrementalAsync();
        SaveManifest localCopy = fixture.Remote.Read<SaveManifest>(next.SaveManifestPaths.Single());
        Check(next.FormatVersion == 2 && localCopy.FormatVersion == 2 && localCopy.DeviceId == fixture.Settings.DeviceId &&
            localCopy.ManifestPath.StartsWith("v2/saves/", StringComparison.Ordinal) && localCopy.ManifestPath != save.ManifestPath,
            "有完整本机ZIP时，新v2快照仍直接引用旧v1存档。");
        foreach (string path in originals.Keys) fixture.Remote.Files.Remove(path); // 模拟旧客户端只清理自己的v1历史。
        await AssertIncrementalSaveRestoreAsync(fixture, copiedSnapshot, fixture.LargeBytes, fixture.SlotBytes);
        await AssertIncrementalSaveRestoreAsync(fixture, next, fixture.LargeBytes, fixture.SlotBytes);
    }

    private static async Task IncrementalReferencePruneAsync()
    {
        var fixture = NewIncremental();
        fixture.Settings.KeepSnapshots = 1;
        fixture.Settings.KeepSaves = 1;
        var first = await fixture.Coordinator.BackupIncrementalAsync();
        SaveManifest firstSave = fixture.Remote.Read<SaveManifest>(first.SaveManifestPaths.Single());
        string oldSlot = IncrementalObjectPath(firstSave.Recipe!, firstSave.Recipe!.Files.Single(file => file.Path.EndsWith("slot.dat", StringComparison.Ordinal)));
        var foreign = new SnapshotManifest
        {
            FormatVersion = 2, Id = "foreign_" + Guid.NewGuid().ToString("N"), DeviceId = Guid.NewGuid(), CreatedUtc = DateTimeOffset.UtcNow,
            Content = first.Content, Games = first.Games, DataRecipe = first.DataRecipe, MediaRecipe = first.MediaRecipe,
            SaveManifestPaths = first.SaveManifestPaths.ToList()
        };
        foreign.ManifestPath = $"v2/snapshots/{foreign.DeviceId:N}/{foreign.Id}/manifest.json";
        fixture.Remote.PutCommitted(foreign.ManifestPath, foreign);
        fixture.Host.Comment = "next retained review";
        await File.WriteAllTextAsync(fixture.SlotPath, "next retained slot");
        var second = await fixture.Coordinator.BackupIncrementalAsync();
        await fixture.Coordinator.CleanupIncrementalHistoryAsync();
        Check(!fixture.Remote.Files.ContainsKey(first.ManifestPath) && fixture.Remote.Files.ContainsKey(foreign.ManifestPath), "保留数量未按设备清理快照。");
        Check(fixture.Remote.Files.ContainsKey(firstSave.ManifestPath) && fixture.Remote.Files.ContainsKey(oldSlot), "跨设备保留快照引用的存档或共享对象被清理。");
        await AssertIncrementalSaveRestoreAsync(fixture, foreign, fixture.LargeBytes, fixture.SlotBytes);
        fixture.Remote.Files.Remove(foreign.ManifestPath);
        var third = await fixture.Coordinator.BackupIncrementalAsync();
        await fixture.Coordinator.CleanupIncrementalHistoryAsync();
        Check(!fixture.Remote.Files.ContainsKey(firstSave.ManifestPath) && !fixture.Remote.Files.ContainsKey(oldSlot),
            "最后引用释放后仍保留过期存档对象。");
        Check(fixture.Remote.Deletes.Contains(oldSlot), "对象没有通过云端删除接口回收。");
        ValidateRecipeBytes(fixture.Remote, third.DataRecipe!);
        ValidateRecipeBytes(fixture.Remote, third.MediaRecipe!);
        await AssertIncrementalSaveRestoreAsync(fixture, third, fixture.LargeBytes, Encoding.UTF8.GetBytes("next retained slot"));
    }

    private static async Task IncrementalDeleteFailureAsync()
    {
        var fixture = NewIncremental();
        fixture.Settings.KeepSnapshots = 10; fixture.Settings.KeepSaves = 10;
        await fixture.Coordinator.BackupIncrementalAsync();
        fixture.Host.Comment = "retained review after deletion failure";
        byte[] changed = Encoding.UTF8.GetBytes("retained slot after deletion failure");
        await File.WriteAllBytesAsync(fixture.SlotPath, changed);
        SnapshotManifest retained = await fixture.Coordinator.BackupIncrementalAsync();
        fixture.Settings.KeepSnapshots = 1; fixture.Settings.KeepSaves = 1;
        int deletes = 0;
        fixture.Remote.AfterDelete = (_, _) => { if (++deletes == 1) throw new IOException("synthetic failure after first actual delete"); };
        await Throws<IOException>(() => fixture.Coordinator.CleanupIncrementalHistoryAsync());
        Check(deletes == 1 && (await fixture.Coordinator.ListIncrementalSnapshotsAsync()).Any(snapshot => snapshot.ManifestPath == retained.ManifestPath),
            "清理删除中断误删了保留快照，或没有真正执行一次删除。");
        ValidateRecipeBytes(fixture.Remote, retained.DataRecipe!);
        ValidateRecipeBytes(fixture.Remote, retained.MediaRecipe!);
        await AssertIncrementalSaveRestoreAsync(fixture, retained, fixture.LargeBytes, changed);
        fixture.Remote.AfterDelete = null;
        SnapshotManifest next = await fixture.Coordinator.BackupIncrementalAsync();
        await fixture.Coordinator.CleanupIncrementalHistoryAsync();
        await AssertIncrementalSaveRestoreAsync(fixture, next, fixture.LargeBytes, changed);
    }

    private static async Task IncrementalMissingReferenceStopsCleanupAsync()
    {
        var fixture = NewIncremental(BackupContent.Saves);
        fixture.Settings.KeepSnapshots = 10; fixture.Settings.KeepSaves = 10;
        SnapshotManifest older = await fixture.Coordinator.BackupIncrementalAsync();
        byte[] changed = Encoding.UTF8.GetBytes("retained save with deliberately missing manifest reference");
        await File.WriteAllBytesAsync(fixture.SlotPath, changed);
        SnapshotManifest retained = await fixture.Coordinator.BackupIncrementalAsync();
        fixture.Settings.KeepSnapshots = 1; fixture.Settings.KeepSaves = 1;
        string missing = retained.SaveManifestPaths.Single();
        byte[] originalManifest = fixture.Remote.Files[missing];
        byte[] originalMarker = fixture.Remote.Files[missing + ".complete"];
        fixture.Remote.Files.Remove(missing); fixture.Remote.Files.Remove(missing + ".complete");
        int before = fixture.Remote.Deletes.Count;
        await Throws<InvalidDataException>(() => fixture.Coordinator.CleanupIncrementalHistoryAsync());
        Check(fixture.Remote.Deletes.Count == before && fixture.Remote.Files.ContainsKey(older.ManifestPath) && fixture.Remote.Files.ContainsKey(older.ManifestPath + ".complete"),
            "保留引用不完整时仍执行了删除计划。");
        fixture.Remote.Files[missing] = originalManifest; fixture.Remote.Files[missing + ".complete"] = originalMarker;
        await AssertIncrementalSaveRestoreAsync(fixture, retained, fixture.LargeBytes, changed);
    }

    private static async Task IncrementalMissingObjectAsync()
    {
        var fixture = NewIncremental(BackupContent.Saves);
        var first = await fixture.Coordinator.BackupIncrementalAsync();
        SaveManifest save = fixture.Remote.Read<SaveManifest>(first.SaveManifestPaths.Single());
        FileRecord missing = save.Recipe!.Files.Single(file => file.Path.EndsWith("large.bin", StringComparison.Ordinal));
        string missingPath = IncrementalObjectPath(save.Recipe, missing);
        fixture.Remote.Files.Remove(missingPath);
        int checkpoint = fixture.Remote.Transfers.Count;
        var repaired = await fixture.Coordinator.BackupIncrementalAsync();
        Check(fixture.Remote.ObjectTransfers(checkpoint).Count(transfer => transfer.Path == missingPath) == 1,
            "缺失对象未补传，或重复补传。");
        await AssertIncrementalSaveRestoreAsync(fixture, repaired, fixture.LargeBytes, fixture.SlotBytes);
    }

    private static async Task IncrementalPartialObjectRetryAsync()
    {
        var fixture = NewIncremental(BackupContent.Saves);
        string? partialPath = null;
        fixture.Remote.BeforeUpload = (path, bytes, _) =>
        {
            if (!IsObject(path)) return;
            partialPath = path;
            int partialLength = Math.Max(1, bytes.Length / 2);
            fixture.Remote.Files[path] = bytes[..partialLength];
            fixture.Remote.Transfers.Add(new UploadTransfer(path, partialLength, false));
            throw new IOException("remote kept a partial object under its final name");
        };
        await Throws<IOException>(() => fixture.Coordinator.BackupIncrementalAsync());
        Check(partialPath != null && !(await fixture.Coordinator.ListIncrementalSnapshotsAsync()).Any(), "模拟失败没有留下半对象或错误发布了快照。");
        fixture.Remote.BeforeUpload = null;
        int checkpoint = fixture.Remote.Transfers.Count;
        var completed = await fixture.Coordinator.BackupIncrementalAsync();
        Check(fixture.Remote.ObjectTransfers(checkpoint).Count(transfer => transfer.Path == partialPath) == 1,
            "失败残留同名半对象被Exists检查直接复用，没有重新上传。");
        await AssertIncrementalSaveRestoreAsync(fixture, completed, fixture.LargeBytes, fixture.SlotBytes);
    }

    private static async Task IncrementalPublishedRepairRetryAsync()
    {
        var fixture = NewIncremental(BackupContent.Saves);
        SnapshotManifest first = await fixture.Coordinator.BackupIncrementalAsync();
        SaveManifest original = fixture.Remote.Read<SaveManifest>(first.SaveManifestPaths.Single());
        string missing = IncrementalObjectPath(original.Recipe!, original.Recipe!.Files.Single(file => file.Path.EndsWith("large.bin", StringComparison.Ordinal)));
        Check(fixture.Remote.Files.ContainsKey(missing + ".complete"), "已发布对象缺少complete标记，测试未覆盖旧已提交修复。");
        fixture.Remote.Files.Remove(missing);
        fixture.Remote.BeforeUpload = (path, bytes, _) =>
        {
            if (path != missing) return;
            int half = Math.Max(1, bytes.Length / 2);
            fixture.Remote.Files[path] = bytes[..half];
            fixture.Remote.Transfers.Add(new UploadTransfer(path, half, false));
            throw new IOException("repair of a published object left a partial replacement");
        };
        await Throws<IOException>(() => fixture.Coordinator.BackupIncrementalAsync());
        Check(fixture.Remote.Files.ContainsKey(missing) && !fixture.Remote.Files.ContainsKey(missing + ".complete"),
            "旧已发布对象修复失败后仍带有旧complete标记。");
        fixture.Remote.BeforeUpload = null;
        Check((await fixture.Coordinator.ListIncrementalSnapshotsAsync()).Single().ManifestPath == first.ManifestPath,
            "修复半写失败时发布了新快照。");
        int checkpoint = fixture.Remote.Transfers.Count;
        var restarted = IncrementalCoordinator(Work(), fixture.Host, fixture.Settings, fixture.Remote);
        SnapshotManifest retried = await restarted.BackupIncrementalAsync();
        Check(fixture.Remote.ObjectTransfers(checkpoint).Count(transfer => transfer.Path == missing) == 1 && fixture.Remote.Files.ContainsKey(missing + ".complete"),
            "重试把已发布对象修复后的同名半写内容误认作完整对象。");
        await AssertIncrementalSaveRestoreAsync(fixture, retried, fixture.LargeBytes, fixture.SlotBytes);
    }

    private static async Task IncrementalPartialManifestAsync()
    {
        var fixture = NewIncremental(BackupContent.Saves);
        SnapshotManifest first = await fixture.Coordinator.BackupIncrementalAsync();
        string uncommitted = $"v2/snapshots/{fixture.Settings.DeviceId:N}/half-written/manifest.json";
        fixture.Remote.Files[uncommitted] = Encoding.UTF8.GetBytes("{\"FormatVersion\":2,\"Id\":");
        string invalidMarker = $"v2/snapshots/{fixture.Settings.DeviceId:N}/half-marker/manifest.json";
        fixture.Remote.Files[invalidMarker] = fixture.Remote.Files[first.ManifestPath].ToArray();
        fixture.Remote.Files[invalidMarker + ".complete"] = Encoding.UTF8.GetBytes("{\"FormatVersion\":2,");
        IReadOnlyList<SnapshotManifest> listed = await fixture.Coordinator.ListIncrementalSnapshotsAsync();
        Check(listed.Count == 1 && listed[0].ManifestPath == first.ManifestPath,
            "未提交半manifest或半JSON marker阻断旧历史读取，或被列为可恢复版本。");
        await AssertIncrementalSaveRestoreAsync(fixture, listed[0], fixture.LargeBytes, fixture.SlotBytes);
    }

    private static async Task IncrementalLocalAdapterAsync()
    {
        var fixture = await SaveFixtureAsync();
        var remote = JsonSerializer.Deserialize<SaveManifest>(JsonSerializer.Serialize(fixture.Manifest, BackupJson.Options), BackupJson.Options)!;
        remote.FormatVersion = 2;
        remote.ManifestPath = remote.ManifestPath.Replace("v1/", "v2/", StringComparison.Ordinal);
        remote.ArchivePath = "";
        remote.Sha256 = "";
        remote.Length = remote.Files.Sum(file => file.Length);
        remote.Recipe = new ArchiveRecipe { DeviceId = remote.DeviceId, Files = remote.Files };
        string original = JsonSerializer.Serialize(remote, BackupJson.Options);
        SaveManifest first = await IncrementalSaveAdapter.ToLocalAsync(remote, fixture.Archive);
        Check(first.FormatVersion == 1 && first.Recipe == null && first.ArchivePath.Length == 0 && first.Length == new FileInfo(fixture.Archive).Length &&
            first.Sha256.Equals(await BackupFiles.HashAsync(fixture.Archive), StringComparison.OrdinalIgnoreCase), "本机存档适配未按重建ZIP更新长度和校验。");
        Check(JsonSerializer.Serialize(remote, BackupJson.Options) == original, "本机适配修改了远端清单。");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Throws<OperationCanceledException>(() => IncrementalSaveAdapter.ToLocalAsync(remote, fixture.Archive, cancellation.Token));
        string alternate = Path.Combine(Work(), "same-files-different-zip.zip");
        using (ZipArchive source = ZipFile.OpenRead(fixture.Archive))
        using (ZipArchive destination = ZipFile.Open(alternate, ZipArchiveMode.Create))
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                ZipArchiveEntry replacement = destination.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                replacement.LastWriteTime = new DateTimeOffset(2020, 1, 2, 3, 4, 6, TimeSpan.Zero);
                using Stream input = entry.Open(); using Stream output = replacement.Open(); await input.CopyToAsync(output);
            }
        SaveManifest second = await IncrementalSaveAdapter.ToLocalAsync(remote, alternate);
        Check(first.Sha256 != second.Sha256, "测试未生成内容相同但字节不同的ZIP。");
        string root = Work(); var store = new PendingRestoreStore(root); var profile = new ProviderProfile();
        await store.StageAsync(profile, first, fixture.Archive);
        store.Refresh([fixture.Game], []);
        PendingRestore pending = store.Items.Single();
        string confirmed = Path.Combine(Work(), "confirmed-target");
        pending.TargetOverride = confirmed; pending.TargetPath = confirmed; pending.Status = RestoreStatus.Restored;
        await store.SaveAsync();
        await store.StageAsync(profile, second, alternate);
        Check(pending.Manifest.Sha256 == second.Sha256 && pending.TargetOverride == confirmed && pending.TargetPath == confirmed && pending.Status == RestoreStatus.Restored,
            "相同云端版本重新暂存未更新本机ZIP清单，或丢失已确认路径/状态。");
        var reloaded = new PendingRestoreStore(root); await reloaded.LoadAsync();
        PendingRestore loaded = reloaded.Items.Single();
        Check(loaded.Manifest.Sha256 == second.Sha256 && loaded.TargetOverride == confirmed && loaded.Status == RestoreStatus.Restored,
            "重启后暂存包清单或已确认状态未保留。");
        await SaveArchiveService.RestoreAsync(loaded, reloaded.ArchivePath(loaded.Manifest), fixture.Game, Work());
        Check(await File.ReadAllTextAsync(Path.Combine(confirmed, "slot.dat")) == "original-progress", "重新暂存并重启后不能恢复相同存档。");
    }

    private static IncrementalFixture NewIncremental(BackupContent content = IncrementalContent)
    {
        string root = Work(); string gameRoot = Path.Combine(root, "game"); string saves = Path.Combine(gameRoot, "savedata");
        Directory.CreateDirectory(saves);
        byte[] large = RandomBytes(256 * 1024, 17); byte[] slot = Encoding.UTF8.GetBytes("initial small slot");
        File.WriteAllBytes(Path.Combine(saves, "large.bin"), large); File.WriteAllBytes(Path.Combine(saves, "slot.dat"), slot);
        GameIdentity game = Game(directory: gameRoot); var host = new IncrementalHost([game]); var remote = new TransferRemote();
        var profile = new ProviderProfile();
        var settings = new BackupSettings { Content = content, Profiles = [profile], ActiveProfileId = profile.Id,
            SaveConfigs = [new GameSaveConfig { GameId = game.Id, Locations = [new SaveLocation { Id = "save-root", BaseKind = SaveBaseKind.GameRoot, RelativePath = "savedata" }] }] };
        return new IncrementalFixture(IncrementalCoordinator(root, host, settings, remote), settings, remote, host, game,
            Path.Combine(saves, "large.bin"), Path.Combine(saves, "slot.dat"), large, slot);
    }

    private static BackupCoordinator IncrementalCoordinator(string root, IBackupHost host, BackupSettings settings, TransferRemote remote) =>
        new(root, host, settings, new SettingsStore(root), new PendingRestoreStore(root), _ => new TransferProvider(remote));
    private static BackupSettings RestoreSettings(IncrementalFixture fixture) => new()
    { Profiles = fixture.Settings.Profiles, ActiveProfileId = fixture.Settings.ActiveProfileId };
    private static byte[] RandomBytes(int length, int seed) { byte[] value = new byte[length]; new Random(seed).NextBytes(value); return value; }
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsObject(string path) => path.StartsWith("v2/objects/", StringComparison.Ordinal) && path.EndsWith(".gz", StringComparison.Ordinal);
    private static string IncrementalObjectPath(ArchiveRecipe recipe, FileRecord file)
    { string hash = file.Sha256.ToLowerInvariant(); return $"v2/objects/{recipe.DeviceId:N}/{hash[..2]}/{hash}.gz"; }
    private static IEnumerable<string> RecipeObjectPaths(ArchiveRecipe recipe) => recipe.Files.Select(file => IncrementalObjectPath(recipe, file));
    private static Dictionary<string, byte[]> ValidateRecipeBytes(TransferRemote remote, ArchiveRecipe recipe)
    {
        Dictionary<string, byte[]> result = new(StringComparer.Ordinal);
        foreach (FileRecord file in recipe.Files)
        {
            string path = IncrementalObjectPath(recipe, file);
            Check(remote.Files.ContainsKey(path), "清单引用不存在的对象：" + path);
            using MemoryStream source = new(remote.Files[path]); using GZipStream unzip = new(source, CompressionMode.Decompress); using MemoryStream bytes = new();
            unzip.CopyTo(bytes); byte[] value = bytes.ToArray();
            Check(value.LongLength == file.Length && Sha(value).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase), "对象原始内容与文件配方校验不一致。");
            result.Add(file.Path, value);
        }
        return result;
    }
    private static JsonNode RecipeJson(TransferRemote remote, ArchiveRecipe recipe, string path) =>
        JsonNode.Parse(ValidateRecipeBytes(remote, recipe)[path])!;
    private static async Task<FileRecord> SeedLegacyFileAsync(TransferRemote remote, string path, string local)
    {
        byte[] bytes = await File.ReadAllBytesAsync(local); remote.Files[path] = bytes;
        return new FileRecord { Path = path, Length = bytes.LongLength, Sha256 = Sha(bytes) };
    }
    private static async Task AssertIncrementalSaveRestoreAsync(IncrementalFixture fixture, SnapshotManifest snapshot, byte[] large, byte[] slot)
    {
        var game = Game(fixture.Game.Id, Work());
        var restored = IncrementalCoordinator(Work(), new IncrementalHost([game]), RestoreSettings(fixture), fixture.Remote);
        await restored.StageIncrementalRestoreAsync(snapshot, BackupContent.Saves);
        await RestorePendingAndCheckAsync(restored, game, large, slot);
    }
    private static async Task RestorePendingAndCheckAsync(BackupCoordinator restored, GameIdentity game, byte[] large, byte[] slot)
    {
        restored.Pending.Refresh([game], restored.Settings.GameMappings);
        PendingRestore pending = restored.Pending.Items.Single();
        await SaveArchiveService.RestoreAsync(pending, restored.Pending.ArchivePath(pending.Manifest), game, Work());
        Check((await File.ReadAllBytesAsync(Path.Combine(game.LocalDirectory!, "savedata", "large.bin"))).SequenceEqual(large) &&
            (await File.ReadAllBytesAsync(Path.Combine(game.LocalDirectory!, "savedata", "slot.dat"))).SequenceEqual(slot),
            "增量重建后的存档字节不正确。");
    }
    private sealed record IncrementalFixture(BackupCoordinator Coordinator, BackupSettings Settings, TransferRemote Remote,
        IncrementalHost Host, GameIdentity Game, string LargePath, string SlotPath, byte[] LargeBytes, byte[] SlotBytes);
    private sealed record UploadTransfer(string Path, long Bytes, bool Completed);
    private sealed class TransferRemote
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<UploadTransfer> Transfers { get; } = [];
        public List<string> Attempts { get; } = [];
        public List<string> Deletes { get; } = [];
        public List<string> Downloads { get; } = [];
        public Action<string, string, CancellationToken>? BeforeDownload { get; set; }
        public Action<string, CancellationToken>? AfterDelete { get; set; }
        public Action<string, byte[], CancellationToken>? BeforeUpload { get; set; }
        public IEnumerable<UploadTransfer> Successful => Transfers.Where(transfer => transfer.Completed);
        public List<UploadTransfer> ObjectTransfers(int checkpoint) => Transfers.Skip(checkpoint).Where(transfer => transfer.Completed && IsObject(transfer.Path)).ToList();
        public void Put<T>(string path, T value) => Files[path] = JsonSerializer.SerializeToUtf8Bytes(value, BackupJson.Options);
        public void PutCommitted<T>(string path, T value)
        {
            Put(path, value);
            byte[] bytes = Files[path];
            Put(path + ".complete", new { FormatVersion = 2, Path = path, Length = bytes.LongLength, Sha256 = Sha(bytes) });
        }
        public T Read<T>(string path) => JsonSerializer.Deserialize<T>(Files[path], BackupJson.Options)!;
    }
    private sealed class TransferProvider(TransferRemote remote) : IBackupProvider
    {
        public void Dispose() { }
        public Task AuthorizeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ProbeAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(remote.Files.ContainsKey(path)); }
        public Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string prefix = directory.TrimEnd('/') + "/";
            IReadOnlyList<RemoteItem> result = remote.Files.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
                .Select(path => path[prefix.Length..].Split('/')[0]).Distinct(StringComparer.Ordinal).Select(name =>
                { string path = prefix + name; return new RemoteItem(path, !remote.Files.ContainsKey(path), remote.Files.TryGetValue(path, out var bytes) ? bytes.LongLength : 0); }).ToList();
            return Task.FromResult(result);
        }
        public async Task UploadAsync(string path, string localFile, CancellationToken cancellationToken = default)
        {
            byte[] bytes = await File.ReadAllBytesAsync(localFile, cancellationToken);
            remote.Attempts.Add(path);
            remote.BeforeUpload?.Invoke(path, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            remote.Files[path] = bytes;
            remote.Transfers.Add(new UploadTransfer(path, bytes.LongLength, true));
        }
        public async Task DownloadAsync(string path, string localFile, CancellationToken cancellationToken = default)
        {
            remote.Downloads.Add(path);
            if (!remote.Files.TryGetValue(path, out byte[]? bytes)) throw new FileNotFoundException("Missing simulated object", path);
            Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
            remote.BeforeDownload?.Invoke(path, localFile, cancellationToken);
            await File.WriteAllBytesAsync(localFile, bytes, cancellationToken);
        }
        public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); remote.Deletes.Add(path); remote.Files.Remove(path); remote.AfterDelete?.Invoke(path, cancellationToken); return Task.CompletedTask; }
    }
    private sealed class IncrementalHost(List<GameIdentity> games) : IBackupHost
    {
        public string Comment { get; set; } = "cloud review";
        public string Description { get; set; } = "cloud description";
        public int PlayCount { get; set; } = 4;
        public byte[] CoverBytes { get; set; } = RandomBytes(4096, 31);
        public int ExportCalls { get; private set; }
        public JsonArray? ImportedGames { get; private set; }
        public byte[]? ImportedCoverBytes { get; private set; }
        public IReadOnlyList<GameIdentity> GetGames() => games;
        public async Task<HostExport> ExportAsync(string workDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExportCalls++;
            HostExport export = await NativeAsync(games.Select(game =>
            {
                JsonObject raw = NativeGame(game, Comment);
                raw["Description"]!["Value"] = Description; raw["PlayCount"] = PlayCount;
                return raw;
            }));
            using ZipArchive zip = ZipFile.Open(export.ArchivePath, ZipArchiveMode.Update);
            zip.GetEntry("Images/cover.png")!.Delete();
            ZipArchiveEntry cover = zip.CreateEntry("Images/cover.png");
            using (Stream stream = cover.Open()) await stream.WriteAsync(CoverBytes, cancellationToken);
            foreach (ZipArchiveEntry entry in zip.Entries) entry.LastWriteTime = new DateTimeOffset(2020, 1, Math.Min(ExportCalls, 28), 0, 0, 0, TimeSpan.Zero);
            return export;
        }
        public Task StageImportAsync(string mergedArchivePath, Dictionary<string, JsonNode?> settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImportedGames = ReadEntry(mergedArchivePath, "data.galgames.json").AsArray();
            if (ImportedGames.Count > 0 && ImportedGames[0]?["ImagePath"]?["Value"]?.GetValue<string>() is { } image)
            {
                using ZipArchive zip = ZipFile.OpenRead(mergedArchivePath);
                string path = image.Replace('\\', '/'); if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
                using Stream stream = zip.GetEntry(path)!.Open(); using MemoryStream bytes = new(); stream.CopyTo(bytes); ImportedCoverBytes = bytes.ToArray();
            }
            return Task.CompletedTask;
        }
    }
}
