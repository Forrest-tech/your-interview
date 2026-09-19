using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.Services.Assessment.Domain;

/// <summary>
/// 用户简历正文(2026-09-18:简历匹配分析 + 面试前准备包的输入)。
///
/// 为什么单独建表而不是塞进 AiSetting:
///   AiSetting 是"凭据"表(有 API Key,需要掩码回显、优先级回退等特殊处理),
///   简历是"内容"表(长文本,需要版本追溯)。混一张表会让凭据的查询逻辑
///   跟着简历的体量一起变重,职责也不清。
///
/// 为什么一人一条(主键 UserId):
///   简历是长期资产 —— 内容稳定、按岗位微调不改主版本。
///   真需要按岗位存不同版本时,再另开一张"岗位级简历"表,而不是让本表变多条。
///   本表存的是**主版本**,即简历匹配分析的比对基准。
///
/// ⚠️ 隐私:简历含姓名/电话/邮箱。本表只服务端存储,
///    任何对外调用(如送给 LLM)前都必须先脱敏 —— 见 ResumeRedactor。
/// </summary>
public sealed class UserResume
{
    private UserResume() { }

    public UserResume(Guid userId, string content)
    {
        UserId = userId;
        Content = content;
        Version = 1;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>一人一份主版本简历。</summary>
    public Guid UserId { get; private set; }

    /// <summary>
    /// 简历全文。
    /// 用 text 不设长度上限:简历形态差异大(纯文本/Markdown/从 PDF 提的),
    /// 限长了只会造成"保存失败但不知道为什么"。
    /// </summary>
    public string Content { get; private set; } = string.Empty;

    /// <summary>
    /// 版本号 —— 每次覆盖内容递增。
    /// 用途:匹配分析结果里可以标注"基于简历 v3 计算",
    /// 简历更新后旧的分析结果能被识别为过期,而不是静默失效。
    /// </summary>
    public int Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>覆盖简历内容,版本号自增。</summary>
    public void Update(string content)
    {
        Content = content;
        Version++;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
