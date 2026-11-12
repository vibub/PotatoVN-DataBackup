using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace PotatoVN.Backup.Core.Engine;

/// <summary>只覆盖非当前索引槽；写后读回校验，两个槽都损坏时不创建空仓库。</summary>
public sealed class PackedCatalog
{
    private const int MaximumJsonBytes = 64 * 1024 * 1024;
    private readonly IBackupProvider _provider;
    private readonly string _work;
    private readonly Guid _device;
    private readonly Slot[] _slots;
    private int _currentSlot;
    public PackedState State { get; private set; }
    public string? Notice { get; }
    public IEnumerable<PackedState> ProtectedStates => _slots.Where(slot => slot.State != null).Select(slot => slot.State!);
    private sealed record Slot(PackedState? State, string? Hash, bool Missing, string? Error = null);
    private sealed class Envelope
    {
        public string Sha256 { get; set; } = "";
        public JsonElement Payload { get; set; }
    }
    private PackedCatalog(IBackupProvider provider, string work, Guid device, Slot[] slots)
    {
        _provider = provider; _work = work; _device = device; _slots = slots;
        var available = slots.Select((slot, index) => (slot, index)).Where(item => item.slot.State != null).OrderByDescending(item => item.slot.State!.Generation).ToArray();
        if (available.Length == 0)
        {
            if (slots.Any(slot => !slot.Missing)) throw new InvalidDataException("两份v3索引均不可用，已停止备份与清理；请保留云端文件并修复索引。");
            State = new PackedState { DeviceId = device };
            _currentSlot = 1;
        }
        else
        {
            if (available.Length == 2 && available[0].slot.State!.Generation == available[1].slot.State!.Generation && available[0].slot.Hash != available[1].slot.Hash)
                throw new InvalidDataException("v3索引同代内容冲突，已停止写入。");
            _currentSlot = available[0].index;
            State = PackedFormat.Clone(available[0].slot.State!);
            if (slots.Any(slot => !slot.Missing && slot.State == null)) Notice = "一份云端索引损坏，已使用另一份有效索引。";
        }
    }
    public static async Task<PackedCatalog> LoadAsync(IBackupProvider provider, Guid device, string work, CancellationToken ct = default)
    {
        Directory.CreateDirectory(work);
        Slot a = await ReadSlotAsync(provider, device, 0, work, ct);
        Slot b = await ReadSlotAsync(provider, device, 1, work, ct);
        return new PackedCatalog(provider, work, device, [a, b]);
    }
    public async Task PublishAsync(CancellationToken ct = default)
    {
        int target = 1 - _currentSlot;
        State.Generation = checked(_slots.Where(slot => slot.State != null).Select(slot => slot.State!.Generation).DefaultIfEmpty().Max() + 1);
        PackedFormat.Validate(State, _device);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(State, PackedFormat.Json);
        if (payload.Length > MaximumJsonBytes / 2) throw new InvalidDataException("v3索引过大，请减少历史保留数量。");
        JsonElement element = JsonSerializer.Deserialize<JsonElement>(payload);
        // JsonElement重写日期等字符串时可能规范化转义；两端使用相同序列化规则计算摘要。
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(element, PackedFormat.Json);
        string hash = Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
        byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(new Envelope { Sha256 = hash, Payload = element }, PackedFormat.Json);
        string temporary = Path.Combine(_work, "publish-index-" + Guid.NewGuid().ToString("N") + ".gz");
        try
        {
            await using (var output = File.Create(temporary))
            await using (var gzip = new GZipStream(output, CompressionLevel.Optimal)) await gzip.WriteAsync(envelope, ct);
            await _provider.UploadAsync(PackedFormat.IndexPath(_device, target), temporary, ct);
            Slot verified = await ReadSlotAsync(_provider, _device, target, _work, ct);
            if (verified.State?.Generation != State.Generation || verified.Hash != hash) throw new IOException("v3索引上传后校验失败，旧索引仍保留；请重试。" + verified.Error);
            _slots[target] = verified;
            _currentSlot = target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<Slot> ReadSlotAsync(IBackupProvider provider, Guid device, int slot, string work, CancellationToken ct)
    {
        string local = Path.Combine(work, "read-index-" + Guid.NewGuid().ToString("N") + ".gz");
        try
        {
            try { await provider.DownloadAsync(PackedFormat.IndexPath(device, slot), local, ct); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { return new Slot(null, null, true); }
            catch (FileNotFoundException) { return new Slot(null, null, true); }
            if (new FileInfo(local).Length > MaximumJsonBytes) return new Slot(null, null, false);
            try
            {
                await using var file = File.OpenRead(local);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var decoded = new MemoryStream();
                byte[] buffer = new byte[81920];
                int count;
                while ((count = await gzip.ReadAsync(buffer, ct)) > 0)
                {
                    if (decoded.Length + count > MaximumJsonBytes) throw new InvalidDataException("v3索引解压后过大。");
                    await decoded.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                Envelope frame = JsonSerializer.Deserialize<Envelope>(decoded.ToArray(), PackedFormat.Json) ?? throw new InvalidDataException("空索引。");
                if (!PackedFormat.IsHash(frame.Sha256) || frame.Payload.ValueKind != JsonValueKind.Object) throw new InvalidDataException("索引封装无效。");
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(frame.Payload, PackedFormat.Json);
                if (Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant() != frame.Sha256) throw new InvalidDataException("索引校验失败。");
                PackedState state = frame.Payload.Deserialize<PackedState>(PackedFormat.Json) ?? throw new InvalidDataException("空索引内容。");
                if (state.FormatVersion > 3) throw new NotSupportedException("云端索引来自更新版本，请升级插件后再操作。");
                PackedFormat.Validate(state, device);
                if (state.Generation < 1) throw new InvalidDataException("索引代数无效。");
                return new Slot(state, frame.Sha256, false);
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or OverflowException) { return new Slot(null, null, false, error.Message); }
        }
        finally { if (File.Exists(local)) File.Delete(local); }
    }
}
