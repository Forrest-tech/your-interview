using System.IO.Compression;
using System.Text.Json;

namespace YourInterview.Services.Assessment.Infrastructure.Dictionary;

/// <summary>
/// Tier 0 牛津本地包（SPEC §3.3）：只读 PackDir 指向的仓库外目录。
/// entries 以「每词条一个 gzip member」的拼接文件存放，idx 给出 (offset, length)，
/// 按需单条解压 + 小 LRU 缓存，常驻内存不随包体量增长。
/// 目录/文件缺失 = Tier 0 缺席（静默降级，接口不 5xx）。
/// </summary>
public sealed class OxfordPackStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string? _packDir;
    private readonly Lazy<PackState?> _state;
    private readonly Dictionary<string, OxfordEntry?> _entryCache = [];
    private readonly object _cacheLock = new();
    private const int EntryCacheCap = 512;

    /// <summary>包代际：装包/重载即递增，DictionaryService 的缓存键含此值（SPEC §4.4 装包即换代）。</summary>
    public int Generation { get; private set; }

    public OxfordPackStore(string? packDir)
    {
        _packDir = string.IsNullOrWhiteSpace(packDir) ? null : Path.GetFullPath(packDir);
        _state = new Lazy<PackState?>(LoadState);
    }

    public bool IsPresent => _state.Value is not null;
    public bool AudioArchivePresent => _state.Value?.AudioArchivePresent ?? false;

    /// <summary>牛津状态是否已被求值（票130 预热 W2 完成标志：求值过一次即定格，缺席也是完成）。</summary>
    internal bool StateEvaluated => _state.IsValueCreated;

    private sealed class PackState
    {
        public required Dictionary<string, long[]> Index { get; init; }
        public required Dictionary<string, string> Redirects { get; init; }
        public required bool AudioArchivePresent { get; init; }
        public required string EntriesPath { get; init; }
        public required string AudioDir { get; init; }
    }

    private PackState? LoadState()
    {
        if (_packDir is null) return null;
        try
        {
            var entriesPath = Path.Combine(_packDir, "oxford-entries.jsonl.gz");
            var idxPath = Path.Combine(_packDir, "oxford-entries.idx.json");
            var redirectsPath = Path.Combine(_packDir, "oxford-redirects.json");
            var manifestPath = Path.Combine(_packDir, "oxford-pack-manifest.json");
            if (!File.Exists(entriesPath) || !File.Exists(idxPath) || !File.Exists(redirectsPath))
                return null;

            var index = JsonSerializer.Deserialize<Dictionary<string, long[]>>(
                File.ReadAllText(idxPath), JsonOptions) ?? [];
            var redirects = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(redirectsPath), JsonOptions) ?? [];
            var audioArchive = false;
            if (File.Exists(manifestPath))
            {
                var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
                audioArchive = manifest.TryGetProperty("audioArchive", out var aa)
                    && aa.GetString() == "present";
            }
            Generation++;
            return new PackState
            {
                Index = index,
                Redirects = redirects,
                AudioArchivePresent = audioArchive,
                EntriesPath = entriesPath,
                AudioDir = Path.Combine(_packDir, "oxford-audio"),
            };
        }
        catch (Exception)
        {
            // 本地包损坏/不可读 → 按缺席处理（降级铁律：不 5xx）
            return null;
        }
    }

    /// <summary>桩归元：屈折形键 → 词元（牛津自带归元，SPEC §4.1 第 3 步）。</summary>
    public string? RedirectTarget(string lowerWord)
    {
        var state = _state.Value;
        if (state is null) return null;
        return state.Redirects.TryGetValue(lowerWord, out var target) ? target.Trim().ToLowerInvariant() : null;
    }

    public OxfordEntry? GetEntry(string lowerHeadword)
    {
        var state = _state.Value;
        if (state is null) return null;
        lock (_cacheLock)
        {
            if (_entryCache.TryGetValue(lowerHeadword, out var cached)) return cached;
        }
        OxfordEntry? entry = null;
        if (state.Index.TryGetValue(lowerHeadword, out var span) && span.Length == 2)
        {
            try
            {
                using var fs = new FileStream(state.EntriesPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(span[0], SeekOrigin.Begin);
                var member = new byte[span[1]];
                fs.ReadExactly(member);
                using var ms = new MemoryStream(member);
                using var gz = new GZipStream(ms, CompressionMode.Decompress);
                using var reader = new StreamReader(gz);
                var line = reader.ReadToEnd().Trim();
                if (line.Length > 0)
                    entry = JsonSerializer.Deserialize<OxfordEntry>(line, JsonOptions);
            }
            catch (Exception)
            {
                entry = null; // 单条损坏按未命中处理
            }
        }
        lock (_cacheLock)
        {
            if (_entryCache.Count >= EntryCacheCap) _entryCache.Clear();
            _entryCache[lowerHeadword] = entry;
        }
        return entry;
    }

    /// <summary>Tier 0 音频文件是否存在（归档缺席时恒 false，整级跳过 — SPEC §2.2）。</summary>
    public bool AudioFileExists(string fileKey) =>
        _state.Value is { AudioArchivePresent: true } state
        && IsBareFileName(fileKey)
        && File.Exists(Path.Combine(state.AudioDir, fileKey));

    public string? AudioFilePath(string fileKey) =>
        AudioFileExists(fileKey) ? Path.Combine(_state.Value!.AudioDir, fileKey) : null;

    public static bool IsBareFileName(string key) =>
        !string.IsNullOrEmpty(key)
        && key.IndexOfAny(['/', '\\']) < 0
        && !key.Contains("..", StringComparison.Ordinal);
}
