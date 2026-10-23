using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace YourInterview.Services.Assessment.Infrastructure.Dictionary;

/// <summary>
/// 发音文件解析（SPEC §2.3）：不透明 key → 本地文件，按层定位
/// 核心包 manifest → Tier 0 牛津目录 → 懒缓存已有文件 → 按 audio-index 懒拉取落盘。
/// 懒缓存：上限 200 MB、LRU 淘汰、写入时记录来源 URL 与许可（cache-index.json）。
/// </summary>
public sealed class AudioResolver
{
    public const long CacheCapBytes = 200L * 1024 * 1024;

    private readonly OpenLayerStore _open;
    private readonly OxfordPackStore _oxford;
    private readonly DictionaryOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    private Dictionary<(string Word, string Accent), (string Key, string FileName)>? _coreByWord;
    private Dictionary<string, (string Word, string Accent, string FileName)>? _coreByKey;
    private Dictionary<string, AudioFileRec>? _indexByMp3Name;
    private Dictionary<string, CacheEntry>? _cacheIndex;

    private sealed class CacheEntry
    {
        public long Bytes { get; set; }
        public DateTimeOffset LastAccessUtc { get; set; }
        public string SourceUrl { get; set; } = "";
        public string License { get; set; } = "";
        public string Kind { get; set; } = "commons";
    }

    private readonly long _cacheCapBytes;

    public AudioResolver(
        Lazy<OpenLayerStore> open,
        OxfordPackStore oxford,
        DictionaryOptions options,
        IHttpClientFactory httpFactory,
        TimeProvider clock,
        long cacheCapBytes = CacheCapBytes,
        ILogger<AudioResolver>? logger = null)
    {
        _open = open.Value;
        _oxford = oxford;
        _options = options;
        _httpFactory = httpFactory;
        _clock = clock;
        _cacheCapBytes = cacheCapBytes;
        _logger = logger ?? NullLogger<AudioResolver>.Instance;
    }

    // ---------- 核心包（构建期 manifest 驱动） ----------

    private void EnsureCoreLoaded()
    {
        if (_coreByWord is not null) return;
        _coreByWord = [];
        _coreByKey = [];
        try
        {
            var manifestPath = Path.Combine(_options.ResolveAudioCoreDir(), "audio-core-manifest.json");
            if (!File.Exists(manifestPath)) return;
            var doc = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
            foreach (var f in doc.GetProperty("files").EnumerateArray())
            {
                var key = f.GetProperty("key").GetString()!;
                var word = f.GetProperty("word").GetString()!;
                var accent = f.GetProperty("accent").GetString()!;
                var stored = f.TryGetProperty("storedAs", out var s) ? s.GetString()! : $"{key}.mp3";
                _coreByWord[(word, accent)] = (key, stored);
                _coreByKey[key] = (word, accent, stored);
            }
        }
        catch (Exception)
        {
            // manifest 损坏 → 核心包按缺席处理（懒缓存层仍在）
            _coreByWord = [];
            _coreByKey = [];
        }
    }

    public (string Key, string FileName)? ResolveCore(string word, string accent)
    {
        EnsureCoreLoaded();
        return _coreByWord!.TryGetValue((word, accent), out var hit) ? hit : null;
    }

    // ---------- audio-index 反查（懒拉取用） ----------

    public static string Mp3FileName(AudioFileRec rec)
    {
        if (!string.IsNullOrEmpty(rec.Mp3Url))
        {
            var name = Path.GetFileName(new Uri(rec.Mp3Url).LocalPath);
            if (!string.IsNullOrEmpty(name)) return name;
        }
        // 无转码 URL 时以源文件名 + .mp3 命名（端点拉取会失败并回落下一层，不假装）
        return rec.File + ".mp3";
    }

    private AudioFileRec? FindIndexRecByMp3Name(string mp3Name)
    {
        if (_indexByMp3Name is null)
        {
            _indexByMp3Name = [];
            foreach (var (word, recs) in _open.AudioIndex)
            {
                foreach (var rec in recs)
                {
                    var name = Mp3FileName(rec);
                    _indexByMp3Name.TryAdd(name, rec);
                }
            }
        }
        return _indexByMp3Name.TryGetValue(mp3Name, out var hit) ? hit : null;
    }

    // ---------- 音频端点主入口 ----------

    /// <summary>key 命中任一层 → 返回文件全路径；全落空 → null（端点回 404，前端回落下一档）。</summary>
    public async Task<string?> GetAudioFileAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) return null;

        // 1) 核心包（key = manifest 不透明 ID）
        EnsureCoreLoaded();
        if (_coreByKey!.TryGetValue(key, out var core))
        {
            var corePath = Path.Combine(_options.ResolveAudioCoreDir(), core.FileName);
            if (File.Exists(corePath)) return corePath;
        }

        // 2) Tier 0 牛津（key = 包内文件 key，须裸文件名 + 文件存在）
        var oxfordPath = _oxford.AudioFilePath(key);
        if (oxfordPath is not null) return oxfordPath;

        if (!OxfordPackStore.IsBareFileName(key)) return null;
        var cacheDir = _options.ResolveAudioCacheDir();

        // 3) 懒缓存已有文件
        var cachePath = Path.Combine(cacheDir, key);
        if (File.Exists(cachePath))
        {
            TouchCacheEntry(key);
            return cachePath;
        }

        // 4) 按 audio-index 懒拉取（3s 超时，SPEC §2.1 第 3 级）
        var rec = FindIndexRecByMp3Name(key);
        if (rec is null || string.IsNullOrEmpty(rec.Mp3Url)) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var client = _httpFactory.CreateClient("dictionary-audio");
            var bytes = await client.GetByteArrayAsync(rec.Mp3Url, cts.Token);
            if (bytes.Length == 0) return null;
            Directory.CreateDirectory(cacheDir);
            await File.WriteAllBytesAsync(cachePath, bytes, ct);
            RegisterCacheEntry(key, bytes.Length, rec.Mp3Url, rec.License, "commons");
            EvictIfNeeded(protectedKey: key);
            return cachePath;
        }
        catch (Exception ex)
        {
            // 拉取失败 → 下一层（端点 404 → 前端走 pronounce/device）；留日志以便区分网络故障与索引缺口
            _logger.LogWarning(ex, "Dictionary lazy audio fetch failed for {Key} from {Url}", key, rec.Mp3Url);
            return null;
        }
    }

    // ---------- Tier 2 神经 TTS（配置启用时，SPEC §2.1 第 4 级） ----------

    /// <summary>合成并发入懒缓存（kind=neural 标记）；未启用/失败 → null。</summary>
    public async Task<string?> SynthesizeNeuralAsync(string word, CancellationToken ct)
    {
        var tts = _options.NeuralTts;
        if (!tts.Enabled || string.IsNullOrWhiteSpace(tts.Endpoint)) return null;
        var cacheDir = _options.ResolveAudioCacheDir();
        var stamp = Convert.ToHexString(SHA1.HashData(
            Encoding.UTF8.GetBytes($"{word}|{tts.Model}|{tts.Voice}")))[..12].ToLowerInvariant();
        var key = $"neural-{stamp}.mp3";
        var path = Path.Combine(cacheDir, key);
        if (File.Exists(path))
        {
            TouchCacheEntry(key);
            return key;
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var client = _httpFactory.CreateClient("dictionary-audio");
            using var req = new HttpRequestMessage(HttpMethod.Post, tts.Endpoint);
            if (!string.IsNullOrWhiteSpace(tts.ApiKey))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tts.ApiKey);
            req.Content = JsonContent.Create(new { model = tts.Model, voice = tts.Voice, input = word });
            using var resp = await client.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            if (bytes.Length == 0) return null;
            Directory.CreateDirectory(cacheDir);
            await File.WriteAllBytesAsync(path, bytes, ct);
            RegisterCacheEntry(key, bytes.Length, tts.Endpoint, "neural-tts", "neural");
            EvictIfNeeded(protectedKey: key);
            return key;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------- 懒缓存账本（cache-index.json） + LRU ----------

    private string CacheIndexPath => Path.Combine(_options.ResolveAudioCacheDir(), "cache-index.json");

    private Dictionary<string, CacheEntry> CacheIndex
    {
        get
        {
            if (_cacheIndex is not null) return _cacheIndex;
            _cacheIndex = [];
            try
            {
                if (File.Exists(CacheIndexPath))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CacheIndexPath));
                    if (loaded is not null) _cacheIndex = loaded;
                }
            }
            catch (Exception) { _cacheIndex = []; }
            return _cacheIndex;
        }
    }

    private void SaveCacheIndex()
    {
        try
        {
            Directory.CreateDirectory(_options.ResolveAudioCacheDir());
            File.WriteAllText(CacheIndexPath, JsonSerializer.Serialize(CacheIndex));
        }
        catch (Exception) { /* 账本写失败不阻塞播音 */ }
    }

    private void RegisterCacheEntry(string key, long bytes, string sourceUrl, string license, string kind)
    {
        lock (_lock)
        {
            CacheIndex[key] = new CacheEntry
            {
                Bytes = bytes,
                LastAccessUtc = _clock.GetUtcNow(),
                SourceUrl = sourceUrl,
                License = license,
                Kind = kind,
            };
            SaveCacheIndex();
        }
    }

    private void TouchCacheEntry(string key)
    {
        lock (_lock)
        {
            if (CacheIndex.TryGetValue(key, out var entry))
            {
                entry.LastAccessUtc = _clock.GetUtcNow();
                SaveCacheIndex();
            }
        }
    }

    private void EvictIfNeeded(string protectedKey)
    {
        lock (_lock)
        {
            var cacheDir = _options.ResolveAudioCacheDir();
            long total = CacheIndex.Values.Sum(e => e.Bytes);
            if (total <= _cacheCapBytes) return;
            foreach (var (key, _) in CacheIndex.OrderBy(kv => kv.Value.LastAccessUtc).ToList())
            {
                if (total <= _cacheCapBytes) break;
                if (key == protectedKey) continue;
                try { File.Delete(Path.Combine(cacheDir, key)); } catch (Exception) { }
                total -= CacheIndex[key].Bytes;
                CacheIndex.Remove(key);
            }
            SaveCacheIndex();
        }
    }
}
