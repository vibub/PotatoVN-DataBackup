using System.Text.Json;
using PotatoVN.Backup.Core.Engine;

string root = Path.Combine(Path.GetTempPath(), "PotatoVNAtomicChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string destination = Path.Combine(root, "settings.json");
    for (int index = 0; index < 1500; index++)
    {
        await BackupFiles.WriteJsonAsync(destination, new Dictionary<string, int> { ["index"] = index });
        Dictionary<string, int> saved = await BackupFiles.ReadJsonAsync<Dictionary<string, int>>(destination);
        if (saved["index"] != index) throw new InvalidDataException("配置内容未完整提交。");
    }
    using (FileStream heldReader = new(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
    {
        await BackupFiles.WriteJsonAsync(destination, new Dictionary<string, int> { ["index"] = 1500 });
        var previous = JsonSerializer.Deserialize<Dictionary<string, int>>(heldReader)!;
        var current = await BackupFiles.ReadJsonAsync<Dictionary<string, int>>(destination);
        if (previous["index"] != 1499 || current["index"] != 1500)
            throw new InvalidDataException("旧读取句柄和新文件没有分别保留完整版本。");
    }
    using (CancellationTokenSource cancellation = new())
    {
        cancellation.Cancel();
        try { await BackupFiles.WriteJsonAsync(destination, new { index = -1 }, cancellation.Token); throw new Exception("取消操作仍提交了配置。"); }
        catch (OperationCanceledException) { }
        var current = await BackupFiles.ReadJsonAsync<Dictionary<string, int>>(destination);
        if (current["index"] != 1500) throw new InvalidDataException("取消写入改变了已提交配置。");
    }
    if (Directory.EnumerateFiles(root, "*.tmp").Any()) throw new InvalidDataException("配置写入残留临时文件。");
    Console.WriteLine("PASS 1500 atomic writes and reads, open-reader version isolation, and cancellation; no errors or temporary files.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine($"HRESULT={error.HResult:X8}");
    LockOwners.Dump(Path.Combine(root, "settings.json"));
    Console.Error.WriteLine("检查目录：" + root);
    return 1;
}
finally
{
    // 本检查仅创建一个目录和其内的配置文件。
    foreach (string file in Directory.EnumerateFiles(root)) File.Delete(file);
    Directory.Delete(root);
}
