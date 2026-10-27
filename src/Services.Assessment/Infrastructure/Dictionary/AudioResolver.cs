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
        /// <summary>票254 §1：合成条目的 TTS 缓存键（text|voice|speed|language），与音频缓存登记并列记录。</summary>
        public string TtsKey { get; set; } = "";
    }

    private readonly long _cacheCapBytes;
    private readonly YourInterview.Services.Assessment.Application.SpeechSynthesizer? _synthesizer;

    public AudioResolver(
        Lazy<OpenLayerStore> open,
        OxfordPackStore oxford,
        DictionaryOptions options,
        IHttpClientFactory httpFactory,
        TimeProvider clock,
        long cacheCapBytes = CacheCapBytes,
        ILogger<AudioResolver>? logger = null,
        YourInterview.Services.Assessment.Application.SpeechSynthesizer? synthesizer = null)
    {
        _open = open.Value;
        _oxford = oxford;
        _options = options;
        _httpFactory = httpFactory;
        _clock = clock;
        _cacheCapBytes = cacheCapBytes;
        _logger = logger ?? NullLogger<AudioResolver>.Instance;
        _synthesizer = synthesizer;
    }

    // ---------- 核心包（构建期 manifest 驱动） ----------

    private readonly object _coreLock = new();

    /// <summary>测试可见：核心包 manifest 实际构建次数（票130 TB-2 并发单次构建断言用）。</summary>
    internal int CoreBuildCountForTests;

    /// <summary>核心包表是否已一次性发布（票130 预热 W3 完成标志）。</summary>
    internal bool CoreTablePublished => Volatile.Read(ref _coreByWord) is not null;

    /// <summary>票130 预热入口：强制核心包表构建完成（与 ResolveCore 同一路径，不旁路）。</summary>
    internal void WarmCore() => EnsureCoreLoaded();

    private void EnsureCoreLoaded()
    {
        if (Volatile.Read(ref _coreByWord) is not null) return;
        lock (_coreLock)
        {
            if (_coreByWord is not null) return;
            // 票130 §4.3 一次性发布：本地建完才对外可见——构建进行中其他线程阻塞在锁上，
            // 读不到「表非 null 但条目未填齐」的半成品（基线先发布空表再逐条填充的竞态在此收口）。
            var byWord = new Dictionary<(string Word, string Accent), (string Key, string FileName)>();
            var byKey = new Dictionary<string, (string Word, string Accent, string FileName)>();
            CoreBuildCountForTests++;
            try
            {
                var manifestPath = Path.Combine(_options.ResolveAudioCoreDir(), "audio-core-manifest.json");
                if (File.Exists(manifestPath))
                {
                    var doc = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
                    foreach (var f in doc.GetProperty("files").EnumerateArray())
                    {
                        var key = f.GetProperty("key").GetString()!;
                        var word = f.GetProperty("word").GetString()!;
                        var accent = f.GetProperty("accent").GetString()!;
                        var stored = f.TryGetProperty("storedAs", out var s) ? s.GetString()! : $"{key}.mp3";
                        byWord[(word, accent)] = (key, stored);
                        byKey[key] = (word, accent, stored);
                    }
                }
            }
            catch (Exception)
            {
                // manifest 损坏 → 核心包按缺席处理（懒缓存层仍在）
                byWord = [];
                byKey = [];
            }
            Volatile.Write(ref _coreByKey, byKey);
            Volatile.Write(ref _coreByWord, byWord);
        }
    }

    public (string Key, string FileName)? ResolveCore(string word, string accent)
    {
        EnsureCoreLoaded();
        var table = Volatile.Read(ref _coreByWord)!;
        return table.TryGetValue((word, accent), out var hit) ? hit : null;
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

    // ---------- 票254 合成键（确定性、无碰撞、可校验） ----------

    /// <summary>
    /// 票254 §1：合成键 = neural-{word-slug}-{accent}.mp3（.mp3 后缀沿既有 neural- 形态，
    /// 使缓存查找 key == 文件名）。word-slug 为单射：小写后 [a-z0-9] 直通，其余字符一律
    /// 转义为 -u{hex}-（hex 为码点小写十六进制），字面连字符自身也被转义，故不同词永不
    /// 碰撞（“a'b”→ a-u27-b；字面含 -u27- 的词转义后含 -u2d-，绝不与上者相同）。
    /// </summary>
    public static string SyntheticKeyFor(string word, string accent)
    {
        var sb = new StringBuilder();
        foreach (var ch in (word ?? "").ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
            else sb.Append("-u").Append(((int)ch).ToString("x")).Append('-');
        }
        if (sb.Length == 0) sb.Append("empty");
        return $"neural-{sb}-{accent}.mp3";
    }

    public static string SlugWord(string word)
    {
        var key = SyntheticKeyFor(word, "us");
        return key["neural-".Length..^".us.mp3".Length];
    }

    public static bool IsSyntheticKey(string key) =>
        key.StartsWith("neural-", StringComparison.Ordinal)
        && key.EndsWith(".mp3", StringComparison.Ordinal)
        && OxfordPackStore.IsBareFileName(key);

    /// <summary>票254 D6：合成后端是否为显式配置的 NeuralTts（其它一切值 = Azure）。</summary>
    public bool IsNeuralTtsProvider =>
        string.Equals(_options.SynthesisProvider?.Trim(), "neuraltts", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 票254 §1：Main 面是否可提供带标注的合成能力（仅能力广告，不调合成）。
    /// NeuralTts：须显式 provider + Enabled + Endpoint；Azure：须注入 synthesizer。
    /// 密钥缺失不在此预判——lookup 保持廉价同步，缺密钥在播放时走可见失败。
    /// </summary>
    public bool IsSynthesisAvailable =>
        _options.SynthesisEnabled
        && (IsNeuralTtsProvider
            ? _options.NeuralTts.Enabled && !string.IsNullOrWhiteSpace(_options.NeuralTts.Endpoint)
            : _synthesizer is not null);

    /// <summary>
    /// 票254 §1：合成字节唯一落盘口（serve-time 物化）。调用方（控制器）须先校验
    /// key == SyntheticKeyFor(word, accent)，不一致不写（防缓存投毒：错词音频顶替正词键）。
    /// 缓存已存在 → 直接返回（不重调合成）；合成失败 → null（端点回 404，前端走 §4.2 可见失败）。
    /// </summary>
    public async Task<string?> MaterializeSyntheticAsync(string key, string word, string accent,
        Guid userId, CancellationToken ct)
    {
        if (!IsSyntheticKey(key)) return null;
        var cacheDir = _options.ResolveAudioCacheDir();
        var path = Path.Combine(cacheDir, key);
        if (File.Exists(path))
        {
            TouchCacheEntry(key);
            return path;
        }
        if (!_options.SynthesisEnabled) return null;

        byte[]? bytes;
        string voice;
        string language;
        if (IsNeuralTtsProvider)
        {
            bytes = await SynthesizeNeuralBytesAsync(word, ct);
            voice = _options.NeuralTts.Voice;
            language = "";
            if (bytes is null) return null;
            return WriteSyntheticFile(key, bytes, "neural-tts", $"{word}|{voice}|1.0|{language}");
        }

        if (_synthesizer is null) return null;
        // Q2 注：UK 音色名 en-GB-SoniaNeural 为实现期取值，Mac 验收时确认（§6 step 4b：
        // 若该音色不可用，改走 Azure 默认 en-GB 音色并记注，不阻塞交付）。
        // 必须显式传音色：DefaultVoiceFor 对 en-GB 无分支会落回 en-US 音色，
        // 与 xml:lang=en-GB 错配会被 Azure 拒绝（法语案同因）。
        (voice, language) = accent == "uk" ? ("en-GB-SoniaNeural", "en-GB") : ("en-US-AriaNeural", "en-US");
        try
        {
            bytes = await _synthesizer.SynthesizeAsync(word, voice, 1.0, userId, ct, language);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dictionary Azure synthesis failed for {Word} ({Accent})", word, accent);
            return null;
        }
        if (bytes is null || bytes.Length == 0) return null;
        return WriteSyntheticFile(key, bytes, "azure-tts", $"{word}|{voice}|1.0|{language}");
    }

    private string? WriteSyntheticFile(string key, byte[] bytes, string source, string ttsKey)
    {
        try
        {
            var cacheDir = _options.ResolveAudioCacheDir();
            var path = Path.Combine(cacheDir, key);
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(path, bytes);
            RegisterCacheEntry(key, bytes.Length, source, "", "synthetic", ttsKey);
            EvictIfNeeded(protectedKey: key);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dictionary synthetic cache write failed for {Key}", key);
            return null;
        }
    }

    // ---------- 音频端点主入口 ----------

    /// <summary>key 命中任一层 → 返回文件全路径；全落空 → null（端点回 404，前端回落下一档）。</summary>
    public Task<string?> GetAudioFileAsync(string key, CancellationToken ct) =>
        GetAudioFileAsync(key, ct, Guid.Empty, null, null);

    /// <summary>
    /// 带合成物化参数的重载：word/accent 仅在合成键缓存未命中时用于 serve-time 物化，
    /// 且须与 key 一致（不一致 → null；控制器对显式不一致回 400）。
    /// </summary>
    public async Task<string?> GetAudioFileAsync(string key, CancellationToken ct,
        Guid userId, string? word, string? accent)
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

        // 3) 懒缓存已有文件（含已物化的合成文件：无参数即可 serve）
        var cachePath = Path.Combine(cacheDir, key);
        if (File.Exists(cachePath))
        {
            TouchCacheEntry(key);
            return cachePath;
        }

        // 3b) 票254 §1：合成键 serve-time 物化（在索引拉取之前拦截，避免与索引文件名混淆）
        if (IsSyntheticKey(key))
        {
            if (word is null || accent is null) return null;
            if (!string.Equals(key, SyntheticKeyFor(word, accent), StringComparison.Ordinal)) return null;
            return await MaterializeSyntheticAsync(key, word, accent, userId, ct);
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

    /// <summary>合成并发入懒缓存（kind=neural 标记）；未启用/失败 → null。票254 前的旧形态保留（呈现层不用）。</summary>
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
        var bytes = await SynthesizeNeuralBytesAsync(word, ct);
        if (bytes is null) return null;
        Directory.CreateDirectory(cacheDir);
        await File.WriteAllBytesAsync(path, bytes, ct);
        RegisterCacheEntry(key, bytes.Length, tts.Endpoint, "neural-tts", "neural");
        EvictIfNeeded(protectedKey: key);
        return key;
    }

    /// <summary>票254：NeuralTts 纯字节合成（供确定性合成键物化复用；落盘与登记由调用方按 §1 规则做）。</summary>
    private async Task<byte[]?> SynthesizeNeuralBytesAsync(string word, CancellationToken ct)
    {
        var tts = _options.NeuralTts;
        if (!tts.Enabled || string.IsNullOrWhiteSpace(tts.Endpoint)) return null;
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
            return bytes.Length == 0 ? null : bytes;
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

    private void RegisterCacheEntry(string key, long bytes, string sourceUrl, string license, string kind,
        string ttsKey = "")
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
                TtsKey = ttsKey,
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
