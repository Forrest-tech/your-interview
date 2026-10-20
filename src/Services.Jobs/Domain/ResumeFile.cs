using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.Services.Jobs.Domain;

// ============================================================================
//  简历 PDF 文件(2026-10-19)
//
//  为什么单独一张表,而不是把二进制塞进 UserResume:
//    UserResume 是"简历正文"(text)—— 匹配分析/求职信生成读的是它,
//    读文本不需要把几 MB 的 PDF 也拖进内存。分开存,读文本永远轻量。
//
//  去重逻辑(Forrest 2026-10-19 定夺):
//    ApplicationId 为 null = 基础简历(一人一份,存在 /resume 页);
//    ApplicationId 有值 = 某条投递的 tailored 版本(一条投递一份)。
//    Documents tab 里:有 tailored 就显示 tailored,没有就直接引用基础简历 ——
//    不复制,省空间。
// ============================================================================

/// <summary>
/// 简历 PDF 文件。ApplicationId 为 null 表示基础简历(一人一份),
/// 有值表示某条投递的 tailored 版本(一条投递一份)。
/// </summary>
public sealed class ResumeFile : AuditableAggregateRoot
{
    private ResumeFile() { }

    public ResumeFile(Guid userId, string fileName, string contentType, byte[] data,
        string textContent, Guid? applicationId = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("文件名不能为空", nameof(fileName));
        if (data is null || data.Length == 0)
            throw new ArgumentException("文件内容不能为空", nameof(data));

        UserId = userId;
        ApplicationId = applicationId;
        FileName = fileName;
        ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/pdf" : contentType;
        Data = data;
        TextContent = textContent ?? string.Empty;
    }

    /// <summary>文件归属用户。</summary>
    public Guid UserId { get; private set; }

    /// <summary>
    /// 挂载的投递。null = 基础简历(一人一份);有值 = 该投递的 tailored 版本。
    /// </summary>
    public Guid? ApplicationId { get; private set; }

    /// <summary>原始文件名(如 Forrest_Lin_Resume_Arvato.pdf)。</summary>
    public string FileName { get; private set; } = string.Empty;

    /// <summary>如 application/pdf。</summary>
    public string ContentType { get; private set; } = "application/pdf";

    /// <summary>文件二进制(PostgreSQL bytea)。</summary>
    public byte[] Data { get; private set; } = [];

    /// <summary>
    /// 从 PDF 提取的文本 —— Resume Score 按投递版本算分时读这个,
    /// 不用每次下载 PDF 再解析。
    /// </summary>
    public string TextContent { get; private set; } = string.Empty;

    /// <summary>覆盖文件(重新上传)。</summary>
    public void Update(string fileName, string contentType, byte[] data, string textContent)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("文件名不能为空", nameof(fileName));
        if (data is null || data.Length == 0)
            throw new ArgumentException("文件内容不能为空", nameof(data));

        FileName = fileName;
        ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/pdf" : contentType;
        Data = data;
        TextContent = textContent ?? string.Empty;
        Touch();
    }
}
