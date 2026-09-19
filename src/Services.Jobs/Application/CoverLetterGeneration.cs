using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using YourInterview.Services.Jobs.Infrastructure.Services;

namespace YourInterview.Services.Jobs.Application;

// ============================================================================
//  Cover Letter AI 生成(2026-09-18)
//
//  输入(Forrest 定夺)= 简历 + JD 全文 + 公司情报 + 用户额外要求
//
//  为什么 prompt 在服务端拼而不是前端拼:
//    1. 简历/JD/公司情报可能很长,前端拼要先把它们全拉到浏览器再发回去,
//       多一次大体积往返,而且这些正文会进浏览器历史/缓存。
//    2. 拼装规则(格式、字数、语气约束)属于业务知识,换 prompt 策略
//       不该要求发新版前端。
//    3. 未登录/越权校验在服务端天然成立 —— 前端拼装容易把别人的 JD 带进去。
//
//  ⚠️ 生成是长请求(LLM 几十秒)。这里不设短超时,交给 AiGateway 客户端
//     的 HttpClient 超时,并把超时翻译成人话提示。
// ============================================================================

public sealed record GenerateCoverLetterCommand(
    Guid ApplicationId,
    Guid UserId,
    /// <summary>用户额外要求(如"强调我在 Citigroup 的低延迟交易经验")。可空。</summary>
    string? ExtraInstructions = null,
    /// <summary>true 时即使已有内容也重新生成;false(默认)仅在无内容时生成,避免误覆盖用户改过的信。</summary>
    bool Overwrite = false) : IRequest<Result<CoverLetterDto>>;

public sealed class GenerateCoverLetterCommandHandler(
    JobsDbContext db,
    IAiGatewayClient ai,
    ILogger<GenerateCoverLetterCommandHandler> logger)
    : IRequestHandler<GenerateCoverLetterCommand, Result<CoverLetterDto>>
{
    /// <summary>求职信正文的硬约束 —— 写进 system prompt,不是写在 user prompt 里当建议。</summary>
    private const string SystemPrompt =
        "You are an expert career coach who writes cover letters for senior software engineering roles " +
        "in the North American market.\n\n" +
        "Rules you must follow:\n" +
        "1. Output ONLY the cover letter body text. Do NOT add headings, markdown, bullet markers, " +
        "placeholders like [Your Name], or commentary before/after the letter.\n" +
        "2. Write 300-400 words, 3-4 paragraphs: hook + strongest matching evidence, " +
        "concrete technical proof tied to the job's requirements, and a specific close.\n" +
        "3. Use ONLY facts present in the provided resume. Never invent employers, dates, " +
        "metrics, technologies, or achievements.\n" +
        "4. Mirror the job posting's exact terminology for technologies and responsibilities, " +
        "but do not copy whole sentences from it.\n" +
        "5. Sound like a confident senior engineer writing plainly. Avoid buzzwords " +
        "(\"passionate\", \"synergy\", \"dynamic team player\", \"I am writing to express\"). " +
        "No exclamation marks. No clichés about \"hitting the ground running\".\n" +
        "6. Open with a concrete reason this specific company and role, grounded in the " +
        "company information provided. Do not open with \"I am writing to apply for\".\n" +
        "7. Write in the first person, past/present tense, in English.";

    public async Task<Result<CoverLetterDto>> Handle(GenerateCoverLetterCommand request, CancellationToken ct)
    {
        // ---------- 1. 取齐四个输入 ----------
        var app = await db.Applications.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.ApplicationId, ct);
        if (app is null)
            return Result.Failure<CoverLetterDto>(Error.NotFound("投递记录"));

        var resume = await db.Resumes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == request.UserId, ct);
        if (resume is null || string.IsNullOrWhiteSpace(resume.Content))
            return Result.Failure<CoverLetterDto>(Error.Validation(
                "CoverLetter.ResumeMissing",
                "尚未录入简历 —— 求职信必须基于你的真实经历撰写,请先录入简历。"));

        var company = await db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == app.CompanyId, ct);

        var existing = await db.CoverLetters.FirstOrDefaultAsync(x => x.ApplicationId == request.ApplicationId, ct);

        // 默认不覆盖已有内容 —— 生成一次要花钱花时间,把用户手改的信冲掉是严重体验事故。
        if (existing is not null && !string.IsNullOrWhiteSpace(existing.Content) && !request.Overwrite)
            return Result.Failure<CoverLetterDto>(Error.Validation(
                "CoverLetter.AlreadyExists",
                "该投递已有求职信内容。如需重新生成,请勾选\"覆盖现有内容\"。"));

        // ---------- 2. 拼 prompt ----------
        var prompt = BuildPrompt(app, company, resume.Content, request.ExtraInstructions);

        // ---------- 3. 调 AiGateway(凭据/协议/端点全在网关内部) ----------
        AiCompletionResult resp;
        try
        {
            resp = await ai.CompleteAsync(
                purpose: "cover-letter",
                prompt: prompt,
                systemPrompt: SystemPrompt,
                // 0.6:求职信需要一点措辞变化,但过高会开始编造经历。
                temperature: 0.6,
                maxTokens: 1600,
                ct: ct);
        }
        catch (AiGatewayException ex) when (ex.IsConfigurationError)
        {
            // 没配 key —— 400,要求先去设置页配置
            return Result.Failure<CoverLetterDto>(Error.Validation("CoverLetter.AiNotConfigured", ex.Message));
        }
        catch (AiGatewayException ex)
        {
            // 上游/网络问题 —— 502,不是用户输入的问题
            logger.LogWarning(ex, "生成求职信失败:投递 {ApplicationId}", request.ApplicationId);
            return Result.Failure<CoverLetterDto>(
                new Error("CoverLetter.GenerationFailed", ex.Message, ErrorType.Failure));
        }

        var text = resp.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return Result.Failure<CoverLetterDto>(
                new Error("CoverLetter.EmptyGeneration", "AI 返回了空内容,请重试。", ErrorType.Failure));

        // ---------- 4. 落库 ----------
        if (existing is null)
        {
            existing = new CoverLetter(request.ApplicationId, request.UserId, text,
                resp.Model, resume.Version);
            existing.RecordGeneration(text, resp.Model, resume.Version, request.ExtraInstructions);
            db.CoverLetters.Add(existing);
        }
        else
        {
            existing.RecordGeneration(text, resp.Model, resume.Version, request.ExtraInstructions);
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "已为投递 {ApplicationId} 生成求职信(模型 {Model},{Chars} 字符)",
            request.ApplicationId, resp.Model, text.Length);

        return Result.Success(GetCoverLetterQueryHandler.ToDto(existing, resume.Version));
    }

    /// <summary>
    /// 拼装 prompt。分节 + 明确标签,让模型能区分"岗位要求"与"我的经历"。
    ///
    /// ⚠️ 长度控制:简历/JD/公司情报各自截断到上限再拼。
    /// 无脑全塞会遇到两个问题:(1) 超出上下文窗口被上游拒;
    /// (2) 塞在中间的信息注意力衰减,反而丢失关键匹配点。
    /// 上限取的是"够用且不溢出"的经验值 —— JD 40000 是库里的上限,这里收到 12000
    /// 是因为求职信只需要抓 3-4 个核心要求,不是把整份 JD 逐条对应。
    /// </summary>
    private static string BuildPrompt(JobApplication app, Company? company, string resumeContent,
        string? extraInstructions)
    {
        var sb = new System.Text.StringBuilder(8192);

        sb.AppendLine("Write a cover letter for the following job application.");
        sb.AppendLine();

        sb.AppendLine("=== POSITION ===");
        sb.AppendLine($"Company: {company?.Name ?? "(unknown)"}");
        sb.AppendLine($"Role: {app.Role}");
        if (!string.IsNullOrWhiteSpace(app.Location)) sb.AppendLine($"Location: {app.Location}");
        if (!string.IsNullOrWhiteSpace(app.WorkMode)) sb.AppendLine($"Work mode: {app.WorkMode}");
        if (!string.IsNullOrWhiteSpace(app.Salary)) sb.AppendLine($"Posted salary: {app.Salary}");
        sb.AppendLine();

        if (company is not null && !string.IsNullOrWhiteSpace(company.Profile))
        {
            sb.AppendLine("=== COMPANY INFORMATION ===");
            sb.AppendLine(Truncate(company.Profile, 6000));
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(app.JdText))
        {
            sb.AppendLine("=== JOB DESCRIPTION (verbatim) ===");
            sb.AppendLine(Truncate(app.JdText, 12000));
            sb.AppendLine();
        }
        else if (!string.IsNullOrWhiteSpace(app.JdSummary))
        {
            // 没有全文时退到摘要,总比什么都不给强(但上面 readiness 接口会提示补全文)
            sb.AppendLine("=== JOB DESCRIPTION (summary) ===");
            sb.AppendLine(Truncate(app.JdSummary, 4000));
            sb.AppendLine();
        }

        sb.AppendLine("=== MY RESUME (the ONLY source of facts about me) ===");
        sb.AppendLine(Truncate(resumeContent, 16000));
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(extraInstructions))
        {
            sb.AppendLine("=== ADDITIONAL INSTRUCTIONS FROM THE APPLICANT ===");
            sb.AppendLine(extraInstructions.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("Now write the cover letter body. Remember: 300-400 words, no headings, " +
                      "no placeholders, no invented facts, no signature block.");

        return sb.ToString();
    }

    /// <summary>
    /// 截断到上限。从尾部切掉并显式标注截断 ——
    /// 让模型知道"这里还有内容被省略了",而不是当成自然结尾。
    /// </summary>
    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "\n…(content truncated for length)";
}
