namespace YourInterview.Services.Assessment.Infrastructure.Dictionary;

/// <summary>
/// 票119 词典模块配置（SPEC §1）。全部可选：未配置 = Tier 0 缺席、Tier 2 跳过，
/// 开放层（嵌入资源）恒在，任何配置状态接口不 5xx。
/// </summary>
public sealed class DictionaryOptions
{
    public const string SectionName = "Dictionary";

    public OxfordOptions Oxford { get; set; } = new();

    /// <summary>真人录音懒缓存目录。空 = PackDir 同级 dict-data/audio-cache/ 口径（见 ResolveDataDir）。</summary>
    public string AudioCacheDir { get; set; } = "";

    /// <summary>真人录音核心包目录（构建期采收产物，仓库外）。空 = 同上口径的 audio-core/。</summary>
    public string AudioCoreDir { get; set; } = "";

    public NeuralTtsOptions NeuralTts { get; set; } = new();

    /// <summary>
    /// 票254 D7：合成层总开关（Dictionary:SynthesisEnabled，默认 true）。
    /// false = 跳过合成层（step 7），无真人录音时诚实无钮，不做任何合成调用。
    /// </summary>
    public bool SynthesisEnabled { get; set; } = true;

    /// <summary>
    /// 票254 D6：合成后端显式选择（Dictionary:SynthesisProvider）。
    /// 空或 "azure" = Azure 神经语音（默认）；"neuraltts" = Dictionary:NeuralTts 端点。
    /// NeuralTts 永远不是自动兜底：只有显式配成 "neuraltts" 才走该端点；
    /// Azure 未配置且未显式配 NeuralTts 时，合成不可用（无钮，不静默切换）。
    /// </summary>
    public string SynthesisProvider { get; set; } = "";

    /// <summary>
    /// 数据根目录解析：PackDir 已配置时取其父目录（dict-data/ 布局：oxford/、audio-core/、
    /// audio-cache/ 同级）；否则用当前工作目录下的 dict-data/。
    /// </summary>
    public string ResolveDataDir()
    {
        var packDir = Oxford.PackDir;
        if (!string.IsNullOrWhiteSpace(packDir))
        {
            var parent = Directory.GetParent(Path.GetFullPath(packDir))?.FullName;
            if (!string.IsNullOrEmpty(parent)) return parent;
        }
        return Path.GetFullPath("dict-data");
    }

    public string ResolveAudioCacheDir() =>
        !string.IsNullOrWhiteSpace(AudioCacheDir)
            ? Path.GetFullPath(AudioCacheDir)
            : Path.Combine(ResolveDataDir(), "audio-cache");

    public string ResolveAudioCoreDir() =>
        !string.IsNullOrWhiteSpace(AudioCoreDir)
            ? Path.GetFullPath(AudioCoreDir)
            : Path.Combine(ResolveDataDir(), "audio-core");
}

public sealed class OxfordOptions
{
    /// <summary>牛津本地包目录（仓库外，用户个人数据）。未配置或目录不存在 = Tier 0 缺席。</summary>
    public string PackDir { get; set; } = "";
}

public sealed class NeuralTtsOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string Voice { get; set; } = "";
}
