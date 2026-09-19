using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.Services.Jobs.Domain;

// ============================================================================
//  Cover Letter(2026-09-18)
//
//  为什么挂在 JobApplication 而不是独立按公司:
//    求职信天然是"针对某个岗位"写的 —— 同一家公司投两个岗位要写两封。
//    挂在投递记录上,读 JD 全文/公司情报/简历都是现成的,不用再关联一次。
//
//  为什么与简历分开存:
//    简历是一人一份的长期资产(Jobs.UserResume);求职信是每次投递的产物,
//    数量随投递增长。混在一张表里会让"读简历"变成"从 N 封信里挑一封"。
//
//  为什么保存生成时用的简历版本号(ResumeVersion):
//    生成结果可标注"基于简历 v3"。简历更新后,旧信能被识别为"可能已过期",
//    而不是静默失效让人以为还是最新的。
//
//  基类选 AggregateRoot 而不是 Entity:
//    这封信是独立可取的资源(有自己的一级端点 GET/PUT/DELETE),
//    不是只能随投递聚合一起加载的从属部分。用聚合根,
//    EF 会天然把它当成独立表 + 独立查询,不与 JobApplication 的状态机纠缠。
// ============================================================================

/// <summary>求职信生成状态。</summary>
public enum CoverLetterStatus
{
    /// <summary>空白或人工编辑中,尚未生成/未确认。</summary>
    Draft = 0,

    /// <summary>AI 生成完毕,待人工审阅。</summary>
    Generated = 1,

    /// <summary>人工已确认,可以发出去。</summary>
    Final = 2
}

/// <summary>
/// 一条投递对应一封求职信(按 ApplicationId 唯一)。
/// Content 存全文 —— AI 生成与人工编辑写同一字段:
/// 用户改完就是最终版,不需要区分"AI 版/人工版"两套字段,
/// 否则会出现"我改的怎么没生效"。
/// </summary>
public sealed class CoverLetter : AuditableAggregateRoot
{
    private CoverLetter() { }

    public CoverLetter(Guid applicationId, Guid userId, string content,
        string? model = null, int? resumeVersion = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("求职信内容不能为空", nameof(content));

        ApplicationId = applicationId;
        UserId = userId;
        Content = content;
        Status = CoverLetterStatus.Draft;
        GeneratedByModel = model;
        ResumeVersion = resumeVersion;
        if (model is not null) GeneratedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>所针对的投递记录。</summary>
    public Guid ApplicationId { get; private set; }

    /// <summary>撰写者(求职信是用户级资产)。</summary>
    public Guid UserId { get; private set; }

    /// <summary>
    /// 求职信全文(纯文本)。
    /// 用 text 不设长度上限:标准求职信 300-400 词,但用户可能粘贴很长的版本,
    /// 限长只会造成"保存失败但不知道为什么"。
    /// </summary>
    public string Content { get; private set; } = string.Empty;

    public CoverLetterStatus Status { get; private set; }

    /// <summary>生成用的模型(人工撰写时为 null)。便于追溯"这封是哪次生成的"。</summary>
    public string? GeneratedByModel { get; private set; }

    /// <summary>生成时所用简历的版本号。简历更新后可据此判断信件是否过期。</summary>
    public int? ResumeVersion { get; private set; }

    /// <summary>最后一次生成时用户输入的补充要求,便于"再生成一次"时复用。</summary>
    public string? LastPromptHint { get; private set; }

    /// <summary>生成时间(人工撰写时为 null)。</summary>
    public DateTimeOffset? GeneratedAt { get; private set; }

    /// <summary>
    /// 人工编辑内容。编辑后状态退回 Draft ——
    /// 改过的内容不该还标着"已确认",那会让人误以为可以直接发。
    /// </summary>
    public void UpdateContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("求职信内容不能为空", nameof(content));

        Content = content;
        if (Status == CoverLetterStatus.Final)
            Status = CoverLetterStatus.Draft;
        Touch();
    }

    /// <summary>
    /// 记录一次 AI 生成结果。
    /// ⚠️ 生成视为覆盖:再次生成就是"重新来一版",不保留旧版本 ——
    /// 用户要留旧版可以自己复制走。保留多版本会让"哪版是当前版"变模糊,
    /// 而且需要额外的版本选择 UI,收益不抵复杂度。
    /// </summary>
    public void RecordGeneration(string content, string model, int? resumeVersion, string? promptHint)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("生成内容不能为空", nameof(content));

        Content = content;
        GeneratedByModel = model;
        ResumeVersion = resumeVersion;
        LastPromptHint = promptHint;
        GeneratedAt = DateTimeOffset.UtcNow;
        Status = CoverLetterStatus.Generated;
        Touch();
    }

    /// <summary>标记为已确认(可发出)。内容为空时不允许 —— 空信没有"确认"的意义。</summary>
    public void MarkFinal()
    {
        if (string.IsNullOrWhiteSpace(Content))
            throw new InvalidOperationException("求职信内容为空,不能标记为已确认。");
        Status = CoverLetterStatus.Final;
        Touch();
    }
}
