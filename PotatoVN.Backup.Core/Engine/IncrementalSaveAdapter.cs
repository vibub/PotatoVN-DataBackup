using System.Text.Json;

namespace PotatoVN.Backup.Core.Engine;

/// <summary>将已校验、重建的增量存档转换为待恢复区继续使用的本机 ZIP 清单。</summary>
public static class IncrementalSaveAdapter
{
    public static async Task<SaveManifest> ToLocalAsync(SaveManifest remote, string archive, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // 远端清单保留对象配方；本机副本继续走现有 ZIP 校验、暂存及恢复流程。
        SaveManifest local = JsonSerializer.Deserialize<SaveManifest>(
            JsonSerializer.SerializeToUtf8Bytes(remote, BackupJson.Options), BackupJson.Options)!;
        local.FormatVersion = 1;
        local.Recipe = null;
        local.ArchivePath = "";
        local.Sha256 = await BackupFiles.HashAsync(archive, ct);
        local.Length = new FileInfo(archive).Length;
        return local;
    }
}
