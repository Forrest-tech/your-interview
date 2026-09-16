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
        if (Kind == MaterialKind.Folder)
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

    /// <summary>缓存是否对本段参考文本仍然有效(文本没改就能复用,不用重调 Azure)。</summary>
    public bool IsValidFor(string referenceText) =>
        string.Equals(ReferenceText?.Trim(), referenceText?.Trim(), StringComparison.Ordinal);
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
