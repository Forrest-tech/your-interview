namespace YourInterview.Services.Assessment.Domain;

/// <summary>
/// 练习素材节点 —— /practice 左侧那棵树。
///
/// 为什么要有这张表(2026-09-15 Forrest 需求第 2 条):
///   此前树只存在浏览器 localStorage(`practice.materials.v1`)里,
///   换浏览器/清缓存/换机器就全没了;而且四个种子节点是**硬编码在前端**的。
///   素材是用户自己攒的练习内容(自我介绍、工签回答、项目讲稿...),
///   属于"丢了要重写"的资产,必须落库。
///
/// 设计取舍:
///   · 邻接表(ParentId 自引用)+ SortOrder:树的深度不限,同层有序。
///     比 ltree/物化路径简单,且前端 MaterialNode 就是同一形状,
///     序列化时不需要任何转换层。
///   · Folder / File 两种 Kind:文件夹承载结构,文件承载正文(Content)。
///     用枚举而非两张表 —— 它们的字段集合几乎相同,拆表只会带来 join。
/// </summary>
public enum MaterialKind
{
    /// <summary>文件夹:有子节点,通常无正文。</summary>
    Folder = 0,
    /// <summary>文件:承载要朗读/回答的正文。</summary>
    File = 1
}

public sealed class PracticeMaterial
{
    private PracticeMaterial() { }

    public PracticeMaterial(Guid userId, Guid? parentId, MaterialKind kind, string name,
        string? content = null, int sortOrder = 0)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("名称不能为空", nameof(name));

        UserId = userId;
        ParentId = parentId;
        Kind = kind;
        Name = name.Trim();
        Content = content;
        SortOrder = sortOrder;
        IsExpanded = kind == MaterialKind.Folder;   // 文件夹默认展开,与前端行为一致
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// ★ 第三十七轮新增:带**客户端指定 id** 的构造。
    ///
    /// 为什么要这个:整树覆盖保存时,若服务端无视前端传来的 id
    /// 而每次都新发一个 GUID,会造成一个致命的丢数据循环 ——
    ///   前端发 id=A → 服务端存成 B(丢了 A)→ 回读拿到 B
    ///   → 若前端在竞态中又发了一次带 A 的树,
    ///     服务端认不得 A → 当成**新节点** → 原行被当作"未提交"删除。
    /// 旧实现就是这样:每次保存都把用户的节点删掉重建。
    ///
    /// 修法:接受前端在“新建”时给的 GUID。该 id 是前端生成的
    /// (不依赖服务端回传),于是**每次保存 id 都稳定**,
    /// 整树覆盖于是变成真正的幂等 upsert,不再删了重建。
    ///
    /// 安全:此 ctor 仅在“库里找不到该 id”时调用,且查询已按 UserId 隔离,
    ///   所以客户端无法借 id 跨用户改写别人的节点(跨用户 id 会走到这里
    ///   当作新节点建,而不是覆盖他人数据)。
    /// </summary>
    public PracticeMaterial(Guid id, Guid userId, Guid? parentId, MaterialKind kind,
        string name, string? content, int sortOrder)
        : this(userId, parentId, kind, name, content, sortOrder)
    {
        if (id == Guid.Empty) throw new ArgumentException("id 不能为空", nameof(id));
        Id = id;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    /// <summary>归属用户 —— 练习数据是纯私有数据,一律按 UserId 隔离。</summary>
    public Guid UserId { get; private set; }
    /// <summary>父节点(null = 根)。</summary>
    public Guid? ParentId { get; private set; }
    public MaterialKind Kind { get; private set; }
    public string Name { get; private set; } = string.Empty;
    /// <summary>正文(仅 File 有意义)。</summary>
    public string? Content { get; private set; }
    /// <summary>同层排序,升序。前端拖拽排序改的就是它。</summary>
    public int SortOrder { get; private set; }
    /// <summary>文件夹展开状态 —— 也持久化,免得每次打开都要重新展开。</summary>
    public bool IsExpanded { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // ---------- 行为(全部经聚合方法改,不允许外部直接改属性) ----------

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("名称不能为空", nameof(name));
        Name = name.Trim();
        Touch();
    }

    public void SetContent(string? content)
    {
        // ★ 第三十七轮修复(严重 Bug:保存带文件夹的树一律 500)。
        //   原实现在 Kind==Folder 时**无条件抛异常**,
        //   但保存处理器对文件夹正是调 `SetContent(null)`(清空正文)。
        //   于是:只要整树里含任何已有文件夹,保存就 500 →
        //   用户看到"保存失败"、刷新后新东西全没了。
        //   正确语义:文件夹**不允许持有正文**,但"把正文置空"本身完全合法。
        //   只拦"给文件夹写入非空正文"这种真错误。
        if (Kind == MaterialKind.Folder && !string.IsNullOrEmpty(content))
            throw new InvalidOperationException("文件夹不能设置正文");
        Content = content;
        Touch();
    }

    /// <summary>移动到新父节点(拖拽"移入"时用)。</summary>
    public void MoveTo(Guid? parentId, int sortOrder)
    {
        if (parentId == Id) throw new InvalidOperationException("不能把节点移动到它自己下面");
        ParentId = parentId;
        SortOrder = sortOrder;
        Touch();
    }

    public void SetSortOrder(int sortOrder)
    {
        SortOrder = sortOrder;
        Touch();
    }

    public void SetExpanded(bool expanded)
    {
        if (Kind != MaterialKind.Folder) return;
        IsExpanded = expanded;
        Touch();
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}

/// <summary>
/// 一条录音。挂在某个素材文件下 —— 素材是"要说什么",录音是"我这次怎么说的"。
///
/// ⚠️ 音频字节**不在这张表里**(2026-09-15 方案决策):
///   音频是二进制大对象,塞进 Postgres bytea 会把库撑大、拖慢备份、
///   让 pg_dump 体积失控。所以音频落**文件系统**(Storage:RootDirectory),
///   这张表只存**元数据 + 存储路径**。
///   以后要换对象存储(S3 / Azure Blob),只需换 IAudioStore 的实现,
///   本表结构与所有查询一行都不用改。
///
/// 评分结果放在 1:1 的 practice_recording_scores 表(见 PracticeRecordingScore),
/// 这样"评分已完成"是一个可查询的事实,不需要去 JOIN 一堆可空列判断。
/// </summary>
public sealed class PracticeRecording
{
    private PracticeRecording() { }

    /// <summary>
    /// 建一条录音。
    /// </summary>
    /// <param name="id">
    /// 显式传入 Id —— 因为**磁盘文件名用的就是它**,两边必须一致
    /// (见 SaveRecordingCommandHandler:先定 id → 用它命名文件 → 再用同一个 id 建实体)。
    /// 若不显式传,实体自己 NewGuid() 就会和文件名对不上,
    /// "录音 id → 找文件"这条链路就断了。
    /// </param>
    public PracticeRecording(Guid id, Guid userId, Guid materialId, string storagePath,
        string contentType, long sizeBytes, double durationSeconds, string sourceLanguage = "en-US")
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            throw new ArgumentException("存储路径不能为空", nameof(storagePath));

        Id = id;
        UserId = userId;
        MaterialId = materialId;
        StoragePath = storagePath;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        DurationSeconds = durationSeconds;
        SourceLanguage = sourceLanguage;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid UserId { get; private set; }
    /// <summary>所属素材文件 —— 换素材就换录音列表。</summary>
    public Guid MaterialId { get; private set; }
    /// <summary>音频文件相对路径(相对 Storage:RootDirectory),不存绝对路径 —— 换机器也能用。</summary>
    public string StoragePath { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = "audio/webm";
    public long SizeBytes { get; private set; }
    public double DurationSeconds { get; private set; }
    /// <summary>语言(默认 en-US,练北美面试)。</summary>
    public string SourceLanguage { get; private set; } = "en-US";
    public DateTimeOffset CreatedAt { get; private set; }
    public bool IsDeleted { get; private set; }

    /// <summary>评分结果(1:1,可为 null = 还没评过)。</summary>
    public PracticeRecordingScore? Score { get; private set; }

    public void AttachScore(PracticeRecordingScore score) => Score = score;

    /// <summary>重新评分时替换旧结果(可重跑是硬要求)。</summary>
    public void ReplaceScore(PracticeRecordingScore score)
    {
        Score = score;   // 旧行由 DbContext 按 1:1 覆盖写
    }

    public void MarkDeleted() => IsDeleted = true;
}

/// <summary>
/// 一次发音评估的结果 —— 与 PracticeRecording 是 1:1。
///
/// 关键价值(Forrest 需求第 4 条):**评分落库后不再重复调 Azure**。
///   Azure 每次调用都消耗额度,而且同一段音频 + 同一段参考文本的结果是稳定的,
///   重复评一次纯属浪费。所以"评过就存,再打开直接读"。
///
/// ⚠️ 诚实约束(与 PronunciationAssessor 一致,不许违反):
///   Free F0 层只返回 AccuracyScore;Fluency / Completeness / Prosody
///   一律 null —— **绝不用 0 冒充**,那会污染进步曲线。
///   Azure 发音评估**不返回音标**,所以这里也没有音标字段。
/// </summary>
public sealed class PracticeRecordingScore
{
    private PracticeRecordingScore() { }

    public PracticeRecordingScore(Guid recordingId, double? pronScore, double? accuracyScore,
        double? fluencyScore, double? completenessScore, double? prosodyScore,
        string recognizedText, string? wordsJson, string azureRegion, string referenceText)
    {
        RecordingId = recordingId;
        PronScore = pronScore;
        AccuracyScore = accuracyScore;
        FluencyScore = fluencyScore;
        CompletenessScore = completenessScore;
        ProsodyScore = prosodyScore;
        RecognizedText = recognizedText;
        WordsJson = wordsJson;
        AzureRegion = azureRegion;
        ReferenceText = referenceText;
        AssessedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>既是主键也是外键(1:1)。</summary>
    public Guid RecordingId { get; private set; }
    /// <summary>综合分。拿不到时 null(不填 0)。</summary>
    public double? PronScore { get; private set; }
    /// <summary>准确度。Free F0 唯一稳定返回的一项。</summary>
    public double? AccuracyScore { get; private set; }
    /// <summary>流利度。⚠️ Free F0 不返回 → null。</summary>
    public double? FluencyScore { get; private set; }
    /// <summary>完整度。⚠️ Free F0 不返回 → null。</summary>
    public double? CompletenessScore { get; private set; }
    /// <summary>韵律。⚠️ Free F0 不返回 → null。</summary>
    public double? ProsodyScore { get; private set; }
    /// <summary>Azure 识别出的文本。</summary>
    public string RecognizedText { get; private set; } = string.Empty;
    /// <summary>逐词明细(word / accuracy / errorType),JSON 数组。</summary>
    public string? WordsJson { get; private set; }
    /// <summary>调用时的 Azure 区域 —— 换区域后分数可能有差异,记下来便于解释。</summary>
    public string AzureRegion { get; private set; } = string.Empty;
    /// <summary>本次评分用的参考文本 —— 改了文本才能知道该不该重评。</summary>
    public string ReferenceText { get; private set; } = string.Empty;
    public DateTimeOffset AssessedAt { get; private set; }

    /// <summary>
    /// ★ 第三十四轮(Forrest:"给我准确的消耗了多少"):本次评分送评的音频秒数。
    ///
    /// ⚠️ Azure 发音评估按**音频时长**计费,不是 token。这个值由服务端
    ///   从 WAV 头精确算出(字节率 × data 长度),可 100% 复现。
    ///   落库是为了:再次打开评分报告(读库路径)也能显示同样的文案 ——
    ///   否则"本地已存评分"那条会显示不出花了多少,信息就残缺了。
    ///   拿不到为 null(不编造)。
    /// </summary>
    public double? BilledSeconds { get; private set; }

    /// <summary>送评的 WAV 字节数(便于核对,非计费单位)。</summary>
    public int? BilledBytes { get; private set; }

    /// <summary>补充计费口径信息(重评分时由 SaveRecordingScoreCommand 传入)。</summary>
    public void SetBilling(double? seconds, int? bytes)
    {
        BilledSeconds = seconds;
        BilledBytes = bytes;
    }

    /// <summary>缓存是否对本段参考文本仍然有效(文本没改就能复用,不用重调 Azure)。</summary>
    public bool IsValidFor(string referenceText) =>
        string.Equals(ReferenceText?.Trim(), referenceText?.Trim(), StringComparison.Ordinal);
}

/// <summary>
/// 一次示范朗读的音频缓存(OFFLINE — 2026-09-16 第三十一轮)。
///
/// 解决什么(Forrest 需求第 3 条):
///   先前每次点「示范朗读」都要**现调 Azure TTS 合成**,同一段文本读第二遍
///   也照样再花一次额度。而示范朗读的文本极少变 —— 缓存命中率极高。
///   所以:文本 + 音色 + 倍速一致时,直接回放上次合成的 MP3,零 Azure 调用。
///
/// ⚠️ 与 PracticeRecording 的区别 —— 两者绝不能混:
///   · PracticeRecording        = **用户自己的录音**(麦克风采的语音);
///   · PracticeTtsCache         = **机器合成的示范音**(Azure 神经语音吐的 MP3)。
///   它们都落磁盘,但归属、生命周期、业务含义完全不同,所以分成两张表两个目录。
///
/// 缓存键 = SHA-256(文本 + 音色 + 倍速 + 语言)。
///   为什么把音色与倍速也放进键里:换了音色/语速,出来的是**另一段音频**,
///   拿旧缓存回放就是骗人。宁可多合成一次,也不能播错版本。
/// </summary>
public sealed class PracticeTtsCache
{
    private PracticeTtsCache() { }

    public PracticeTtsCache(Guid userId, string cacheKey, string storagePath,
        string contentType, long sizeBytes, string textHash, string voice, double speed,
        string language)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
            throw new ArgumentException("缓存键不能为空", nameof(cacheKey));
        if (string.IsNullOrWhiteSpace(storagePath))
            throw new ArgumentException("存储路径不能为空", nameof(storagePath));

        UserId = userId;
        CacheKey = cacheKey;
        StoragePath = storagePath;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        TextHash = textHash;
        Voice = voice;
        Speed = speed;
        Language = language;
        CreatedAt = DateTimeOffset.UtcNow;
        LastUsedAt = DateTimeOffset.UtcNow;
        HitCount = 0;
    }

    /// <summary>合成这份音频的用户 —— 缓存也按用户隔离(不同用户用的 key 可能不同)。</summary>
    public Guid UserId { get; private set; }

    /// <summary>缓存键(= SHA-256 十六进制,文本+音色+倍速+语言的指纹)。也是主键。</summary>
    public string CacheKey { get; private set; } = string.Empty;

    /// <summary>MP3 相对路径(相对 Storage:RootDirectory),与 IAudioStore 同一套约定。</summary>
    public string StoragePath { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = "audio/mpeg";
    public long SizeBytes { get; private set; }

    /// <summary>文本本身的 SHA-256(不含音色/倍速)—— 便于统计"同一段文本被合成过几次"。</summary>
    public string TextHash { get; private set; } = string.Empty;
    public string Voice { get; private set; } = string.Empty;
    public double Speed { get; private set; } = 1.0;
    public string Language { get; private set; } = "en-US";

    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>最后一次被回放的时间 —— 用于将来的 LRU 清理策略(先不做,只记账)。</summary>
    public DateTimeOffset LastUsedAt { get; private set; }
    /// <summary>被回放次数(纯统计,不改行为)。</summary>
    public int HitCount { get; private set; }

    /// <summary>记录一次命中(回放)。</summary>
    public void MarkUsed()
    {
        LastUsedAt = DateTimeOffset.UtcNow;
        HitCount++;
    }

    /// <summary>
    /// 缓存键算法 —— 一处定义,读写两边共用,避免两边算法漂移导致永远不命中。
    /// 归一化:文本去首尾空白与内部连续空白(换行/多空格都压成单空格),
    /// 这样"文本末端多打一个回车"不会白白作废一份缓存。
    /// </summary>
    public static string ComputeKey(string text, string? voice, double speed, string language)
    {
        var normalized = System.Text.RegularExpressions.Regex
            .Replace((text ?? string.Empty).Trim(), @"\s+", " ");
        var v = string.IsNullOrWhiteSpace(voice) ? "(default)" : voice.Trim();
        var sp = Math.Round(Math.Clamp(speed <= 0 ? 1.0 : speed, 0.5, 2.0), 4)
            .ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        var raw = normalized + "\u0000" + v + "\u0000" + sp + "\u0000" + (language ?? "en-US");
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>文本本身的指纹(不含音色/倍速),供统计使用。</summary>
    public static string ComputeTextHash(string text)
    {
        var normalized = System.Text.RegularExpressions.Regex
            .Replace((text ?? string.Empty).Trim(), @"\s+", " ");
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>
/// Azure Speech 运行时设置(服务端托管)。
///
/// ⚠️ 安全铁律:Key **永远不下发浏览器**。本表存的是加密/明文 key,
///    由服务端在读配置时使用;对外接口一律只返回"是否已配置 + 掩码 + 区域"。
///
/// 为什么要有这张表而不是只用 appsettings:
///   Forrest 需求第 3 条要求能在界面上配置 key。若只写 appsettings 文件,
///   改配置要重启服务、而且容器里文件是只读的。落库后改完立即生效。
///   appsettings 作为**兜底来源**保留(没配过库就用文件里的),两者优先级:
///   数据库 > 环境变量 > appsettings。
/// </summary>
public sealed class SpeechSetting
{
    private SpeechSetting() { }

    public SpeechSetting(Guid userId, string key, string region, string? endpoint = null)
    {
        UserId = userId;
        Key = key;
        Region = region;
        Endpoint = endpoint;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>一条记录对应用户(当前单人使用,但结构上按用户隔离)。</summary>
    public Guid UserId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Region { get; private set; } = "canadacentral";
    public string? Endpoint { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Update(string key, string region, string? endpoint)
    {
        Key = key;
        Region = region;
        Endpoint = endpoint;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
