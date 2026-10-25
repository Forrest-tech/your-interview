using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using YourInterview.Services.Jobs.Infrastructure.Services;
using YourInterview.SharedContracts.Ai;

namespace YourInterview.Services.Jobs.Application;

// ============================================================================
//  Tracker 智能粘贴的编排层(需求 6.2.2 / 6.2.3)
//
//  纯规则解析放在 SmartParseRules.cs(零依赖、可独立验证),这里负责编排:
//    1. 识别粘贴类型(链接 / 文本 / 图片);
//    2. 调规则解析出一份草稿;
//    3. 可选地让 AI 补规则解不出的字段;
//    4. 判定状态(Ok / Partial / Failed)并给出人话提示。
//
//  业务背景:用户每天从 LinkedIn / Indeed / 公司官网 / Greenhouse / Workday
//  看到岗位,再手动把公司、职位、地点、薪资、JD 一个个敲进表格 ——
//  这一步的摩擦大到足以让人放弃记录。所以入口必须是"粘贴一下就完事"。
//
//  ⚠️ 兜底是硬要求:任何一层失败都不能让整条链路挂掉。
//     解析不出来就返回 Failed 让前端回退手动填写 —— 这是验收标准明确写的。
// ============================================================================

/// <summary>
/// FUN-07(票35):parse 成功响应的用户提示不再直出中文句 —— 后端只回结构化码列表,
/// 前端 smart-add-dialog 按码查 i18n 字典本地化拼接(parse.* 键,zh 值与改前原句逐字对齐)。
/// 码序即呈现序:主状态码在前,附加说明(fetch/AI 降级)在后。
/// </summary>
public static class ParseMessageCodes
{
    public const string JdOkAi = "Parse.JdOkAi";
    public const string JdOk = "Parse.JdOk";
    public const string JdPartial = "Parse.JdPartial";
    public const string JdFailed = "Parse.JdFailed";
    public const string ImageUnsupported = "Parse.ImageUnsupported";
    public const string FetchBlocked = "Parse.FetchBlocked";
    public const string InviteOk = "Parse.InviteOk";
    public const string InvitePartial = "Parse.InvitePartial";
    public const string InviteFailed = "Parse.InviteFailed";
    public const string AiNoCredentials = "Parse.AiNoCredentials";
    public const string AiFailed = "Parse.AiFailed";
    public const string AiTimeout = "Parse.AiTimeout";

    /// <summary>AI 降级说明的种类(内部枚举,出门即转码)。</summary>
    public enum AiNoteKind { None, NoCredentials, Failed, Timeout }

    public static string CodeFor(AiNoteKind kind) => kind switch
    {
        AiNoteKind.NoCredentials => AiNoCredentials,
        AiNoteKind.Timeout => AiTimeout,
        _ => AiFailed
    };

    /// <summary>JD 主状态码:Ok 时区分是否经 AI 补齐(与改前文案分支一致)。</summary>
    public static IReadOnlyList<string> ComposeJd(string status, bool usedAi,
        bool fetchBlocked, AiNoteKind aiNote)
    {
        var codes = new List<string>
        {
            status switch
            {
                ParseOutcome.Ok => usedAi ? JdOkAi : JdOk,
                ParseOutcome.Partial => JdPartial,
                _ => JdFailed
            }
        };
        if (fetchBlocked) codes.Add(FetchBlocked);
        if (aiNote != AiNoteKind.None) codes.Add(CodeFor(aiNote));
        return codes;
    }

    /// <summary>Invite 主状态码:改前 Ok 句不带 AI 说明,仅 Partial/Failed 追加(口径不变)。</summary>
    public static IReadOnlyList<string> ComposeInvite(string status, AiNoteKind aiNote)
    {
        var codes = new List<string>
        {
            status switch
            {
                ParseOutcome.Ok => InviteOk,
                ParseOutcome.Partial => InvitePartial,
                _ => InviteFailed
            }
        };
        if (status != ParseOutcome.Ok && aiNote != AiNoteKind.None) codes.Add(CodeFor(aiNote));
        return codes;
    }
}

/// <summary>解析一段粘贴内容。UseAi=false 时只做确定性解析(未配 AI 凭据也能用)。</summary>
public sealed record ParseJdCommand(string Raw, bool UseAi = true) : IRequest<Result<ParsedJdDto>>;

public sealed class ParseJdCommandHandler(
    IAiGatewayClient ai,
    YourInterview.Services.Jobs.Infrastructure.Services.IJobPageFetcher pageFetcher)
    : IRequestHandler<ParseJdCommand, Result<ParsedJdDto>>
{
    public async Task<Result<ParsedJdDto>> Handle(ParseJdCommand request, CancellationToken ct)
    {
        var raw = request.Raw ?? string.Empty;
        if (raw.Trim().Length == 0)
            return Result.Failure<ParsedJdDto>(Error.Validation("Parse.Empty", "粘贴内容为空"));

        if (raw.Length > 200_000)
            return Result.Failure<ParsedJdDto>(Error.Validation("Parse.TooLong", "内容过长,请先截取 JD 正文"));

        var kind = JdPasteParser.DetectKind(raw);

        // 图片:当前链路没有 OCR / 视觉模型,硬解只会给出假字段。
        // 明确告诉前端"识别为图片但读不出内容",让界面直接回退手动填写 —— 比编造字段诚实。
        if (kind == PasteKind.Image)
        {
            return Result.Success(new ParsedJdDto(
                kind, ParseOutcome.Failed, new[] { ParseMessageCodes.ImageUnsupported },
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
                new Dictionary<string, int>(), false, true));
        }

        // 链接:先尝试把页面抓下来再解析 —— 光靠 URL 模式只能猜出公司名。
        // LinkedIn / Indeed 等反爬站点抓不到,返回 null 后降级为 URL 模式解析,
        // 并给用户一句人话提示(改粘贴 JD 文本)。
        string? fetchNote = null;
        string parseInput = raw;
        if (kind == PasteKind.Url)
        {
            var urlMatch = System.Text.RegularExpressions.Regex.Match(
                raw.Trim(), @"https?://[^\s)】」,\u3002]+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (urlMatch.Success)
            {
                var fetched = await pageFetcher.FetchPageTextAsync(urlMatch.Value, ct);
                if (fetched is not null)
                {
                    // 抓到的正文 + 原链接一起解析:正文给字段,链接保留为职位链接。
                    parseInput = fetched + "\n" + urlMatch.Value;
                    kind = PasteKind.Text;
                }
                else
                {
                    fetchNote = ParseMessageCodes.FetchBlocked;
                }
            }
        }

        var draft = JdPasteParser.Parse(parseInput, kind);
        // 抓取成功时把原始链接写回 Link 字段,方便用户回看原文。
        if (fetchNote is null && parseInput != raw && draft.Link is null)
        {
            var urlMatch = System.Text.RegularExpressions.Regex.Match(
                raw.Trim(), @"https?://[^\s)】」,\u3002]+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (urlMatch.Success)
                draft = draft with { Link = urlMatch.Value };
        }
        var usedAi = false;
        var aiNoteKind = ParseMessageCodes.AiNoteKind.None;

        if (request.UseAi)
        {
            // AI 只是"补缺的帮手",不是必经之路:失败一律降级为确定性结果,绝不把错误抛给用户。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var enriched = await JdAiEnricher.EnrichAsync(ai, parseInput, draft, cts.Token);
                if (enriched is not null) { draft = enriched; usedAi = true; }
            }
            catch (AiGatewayException ex)
            {
                aiNoteKind = ex.IsConfigurationError
                    ? ParseMessageCodes.AiNoteKind.NoCredentials
                    : ParseMessageCodes.AiNoteKind.Failed;
            }
            catch (OperationCanceledException)
            {
                aiNoteKind = ParseMessageCodes.AiNoteKind.Timeout;
            }
            catch (Exception)
            {
                aiNoteKind = ParseMessageCodes.AiNoteKind.Failed;
            }
        }

        var filled = JdPasteParser.CountFilled(draft);
        var status = filled >= 4 ? ParseOutcome.Ok
                   : filled >= 1 ? ParseOutcome.Partial
                   : ParseOutcome.Failed;

        return Result.Success(draft with
        {
            Status = status,
            MessageCodes = ParseMessageCodes.ComposeJd(status, usedAi,
                fetchBlocked: fetchNote is not null, aiNoteKind),
            UsedAi = usedAi,
            NeedsManualReview = status != ParseOutcome.Ok
        });
    }
}

// ============================ 一键建档(解析结果 → 投递记录) ============================

public sealed record SmartAddApplicationCommand(
    Guid UserId, string? Company, string? Role, string? Location, string? Salary, string? WorkMode, string? JobType,
    string? JdSummary, string? MatchKeywords, string? Priority, string? Link,
    string? JdText, string? Source, string? Status, DateOnly? Deadline = null,
    string? CompanyDescription = null, string? WorkAuthorization = null,
    string? Requirements = null, string? Benefits = null,
    // 票 150:解析到的真实 logo(li-logo)经此透传落库;此前 Body/Command 无此字段,
    // 真实 logo 在 Smart-add 边界静默丢失,新公司只剩 favicon 派生。
    string? LogoUrl = null)
    : IRequest<Result<SmartAddResultDto>>;

public sealed record SmartAddResultDto(
    Guid ApplicationId, Guid CompanyId, string CompanyName, bool CompanyCreated, string Status);

public sealed class SmartAddApplicationCommandValidator
    : AbstractValidator<SmartAddApplicationCommand>
{
    public SmartAddApplicationCommandValidator()
    {
        // FUN-04:此前 SmartAdd 无校验,长 JD 直接撞列上限整单 500 (Postgres 22001)。
        // 与全仓统一口径对齐 (JdTextLimits.MaxLength),超限在落库前给 400 校验提示。
        RuleFor(x => x.JdText).MaximumLength(JdTextLimits.MaxLength);
        // 票62:解析出的四段长文本同型隐患 —— 单段超旧列上限 20000 同样整单 500。
        // 列已扩 text,四段与 JdText 共用同一闸门,超限在落库前给 400 字段提示。
        RuleFor(x => x.JdSummary).MaximumLength(JdTextLimits.MaxLength);
        RuleFor(x => x.Requirements).MaximumLength(JdTextLimits.MaxLength);
        RuleFor(x => x.Benefits).MaximumLength(JdTextLimits.MaxLength);
        RuleFor(x => x.CompanyDescription).MaximumLength(JdTextLimits.MaxLength);
        // 票 150:LogoUrl 列上限 1024(JobsDbContext),超限在落库前给 400 提示。
        RuleFor(x => x.LogoUrl).MaximumLength(1024);
    }
}

public sealed class SmartAddApplicationCommandHandler(JobsDbContext db)
    : IRequestHandler<SmartAddApplicationCommand, Result<SmartAddResultDto>>
{
    /// <summary>
    /// 解析出来的草稿经用户改过之后才落库 —— 所以这里只做"公司按名解析 + 建档",
    /// 不做任何猜测式补全(公司名对不上就新建,绝不静默合并到别的公司)。
    /// </summary>
    public async Task<Result<SmartAddResultDto>> Handle(SmartAddApplicationCommand request, CancellationToken ct)
    {
        var companyName = (request.Company ?? string.Empty).Trim();
        var role = (request.Role ?? string.Empty).Trim();

        if (companyName.Length == 0)
            return Result.Failure<SmartAddResultDto>(Error.Validation("SmartAdd.NoCompany", "公司名不能为空"));
        if (role.Length == 0)
            return Result.Failure<SmartAddResultDto>(Error.Validation("SmartAdd.NoRole", "职位不能为空"));

        // 公司按名大小写不敏感匹配 —— "Shopify" 与 "shopify" 是同一家,
        // 不这样做会在看板上出现两条同名的公司列。
        // COD-01:find-or-create 按 (Name, UserId) 双条件 —— 同名公司在他人名下不算已存在
        var existing = await db.Companies
            .FirstOrDefaultAsync(c => c.Name.ToLower() == companyName.ToLower() && c.UserId == request.UserId, ct);

        var created = false;
        if (existing is null)
        {
            // 只在链接指向公司自有站点时才把它当官网 —— LinkedIn/Indeed 这类聚合站的域名
            // 不是公司官网,存进去会让"公司官网"字段变成噪音(Logo 也会取成招聘站的图标)。
            var website = JdPasteParser.IsAggregator(request.Link) ? null : JdPasteParser.HostOf(request.Link);
            // 票 150:新建优先用解析到的显式 logo(li-logo),其次 Website 派生。
            existing = new Company(companyName,
                website: website,
                logoUrl: CompanyLogoRules.ResolveNewCompanyLogo(request.LogoUrl, website),
                userId: request.UserId);
            db.Companies.Add(existing);
            created = true;
        }
        // 票 150:已存在公司且存量 logo 为空时,用这次解析到的显式 logo 补上;
        // 已有值绝不覆盖(ShouldFillExistingLogo 把关)。
        else if (CompanyLogoRules.ShouldFillExistingLogo(existing.LogoUrl, request.LogoUrl))
        {
            existing.SetLogoUrl(request.LogoUrl!.Trim());
        }

        var app = new JobApplication(existing.Id, role,
            location: NullIfBlank(request.Location),
            link: NullIfBlank(request.Link),
            salary: NullIfBlank(request.Salary),
            workMode: NullIfBlank(request.WorkMode),
            source: NullIfBlank(request.Source),
            jdSummary: NullIfBlank(request.JdSummary),
            userId: request.UserId);

        if (!string.IsNullOrWhiteSpace(request.Priority)
            && Enum.TryParse<Priority>(request.Priority, true, out var p))
            app.SetPriority(p);

        // JD 全文与出处:解析出来的正文原样存下来,日后做简历匹配/面经预测要用。
        app.SetJdContent(request.JdText, request.Link);

        // 公司介绍:Smart paste 从 "About the company" 段提取,建档时一并存下。
        if (!string.IsNullOrWhiteSpace(request.CompanyDescription))
            app.SetCompanyDescription(request.CompanyDescription.Trim());

        // 工作身份要求:Smart paste 从 JD 正文提取,建档时一并存下。
        if (!string.IsNullOrWhiteSpace(request.WorkAuthorization))
            app.SetWorkAuthorization(request.WorkAuthorization.Trim());

        // 岗位性质:Smart paste 从 JD 正文提取,建档时一并存下。
        if (!string.IsNullOrWhiteSpace(request.JobType))
            app.SetJobType(request.JobType.Trim());

        // 任职要求:Smart paste 从 Qualifications/Requirements 段提取,建档时一并存下。
        if (!string.IsNullOrWhiteSpace(request.Requirements))
            app.SetRequirements(request.Requirements.Trim());

        // 福利待遇:Smart paste 从 Benefits/Advantages 段提取,建档时一并存下。
        if (!string.IsNullOrWhiteSpace(request.Benefits))
            app.SetBenefits(request.Benefits.Trim());

        if (!string.IsNullOrWhiteSpace(request.MatchKeywords))
            app.SetAnalysis(null, null, request.MatchKeywords.Trim());

        app.SetDeadline(request.Deadline);
        db.Applications.Add(app);

        // 解析出"已投递"之类的状态时顺手流转。
        // ★ F-09:沿状态机路径逐级走(不绕过状态机,也不静默丢弃跨级目标)——
        //   旧逻辑把"越级到 Interview"直接忽略,建出来的投递状态与真实进度矛盾。
        if (!string.IsNullOrWhiteSpace(request.Status)
            && Enum.TryParse<ApplicationStatus>(request.Status, true, out var target)
            && target != ApplicationStatus.Saved)
        {
            app.TryWalkToStatus(target, "智能粘贴建档");
        }

        await db.SaveChangesAsync(ct);

        return Result.Success(new SmartAddResultDto(
            app.Id, existing.Id, existing.Name, created, app.Status.ToString()));
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}


public sealed record ParseInviteCommand(string Raw, bool UseAi = true) : IRequest<Result<ParsedInviteDto>>;

public sealed class ParseInviteCommandHandler(IAiGatewayClient ai)
    : IRequestHandler<ParseInviteCommand, Result<ParsedInviteDto>>
{
    public async Task<Result<ParsedInviteDto>> Handle(ParseInviteCommand request, CancellationToken ct)
    {
        var raw = request.Raw ?? string.Empty;
        if (raw.Trim().Length == 0)
            return Result.Failure<ParsedInviteDto>(Error.Validation("Parse.Empty", "粘贴内容为空"));
        if (raw.Length > 100_000)
            return Result.Failure<ParsedInviteDto>(Error.Validation("Parse.TooLong", "邮件内容过长"));

        var draft = InvitePasteParser.Parse(raw);
        var usedAi = false;
        var aiNoteKind = ParseMessageCodes.AiNoteKind.None;

        if (request.UseAi)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var enriched = await InviteAiEnricher.EnrichAsync(ai, raw, draft, cts.Token);
                if (enriched is not null) { draft = enriched; usedAi = true; }
            }
            catch (AiGatewayException ex)
            {
                aiNoteKind = ex.IsConfigurationError
                    ? ParseMessageCodes.AiNoteKind.NoCredentials
                    : ParseMessageCodes.AiNoteKind.Failed;
            }
            catch (OperationCanceledException) { aiNoteKind = ParseMessageCodes.AiNoteKind.Timeout; }
            catch (Exception) { aiNoteKind = ParseMessageCodes.AiNoteKind.Failed; }
        }

        var filled = InvitePasteParser.CountFilled(draft);
        var status = filled >= 3 ? ParseOutcome.Ok : filled >= 1 ? ParseOutcome.Partial : ParseOutcome.Failed;
        return Result.Success(draft with
        {
            Status = status,
            MessageCodes = ParseMessageCodes.ComposeInvite(status, aiNoteKind),
            UsedAi = usedAi,
            NeedsManualReview = status != ParseOutcome.Ok
        });
    }
}

/// <summary>
/// 一键关联:把解析出的邀请落到某条投递上 —— 登记轮次 + 推进到面试中。
/// 走聚合的 AddRound 而不是直接改状态:轮次是邀请的载体,
/// 而且 AddRound 会自动发出"邀请已登记"事件去建机经草稿(既有链路,不重复实现)。
/// </summary>
public sealed record LinkInviteCommand(
    Guid UserId, Guid ApplicationId, string? Stage, DateOnly? ScheduledDate, string? Interviewer,
    string? Format, string? Notes)
    : IRequest<Result<LinkInviteResultDto>>;

public sealed record LinkInviteResultDto(Guid ApplicationId, Guid RoundId, int RoundNo, string Status);

public sealed class LinkInviteCommandHandler(JobsDbContext db)
    : IRequestHandler<LinkInviteCommand, Result<LinkInviteResultDto>>
{
    public async Task<Result<LinkInviteResultDto>> Handle(LinkInviteCommand request, CancellationToken ct)
    {
        var app = await db.Applications.Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == request.ApplicationId && x.UserId == request.UserId, ct);
        if (app is null) return Result.Failure<LinkInviteResultDto>(Error.NotFound("投递记录"));

        var stage = string.IsNullOrWhiteSpace(request.Stage) ? "面试" : request.Stage.Trim();

        // 已在终局状态(已拒/失联/已录用)时不再推进状态 —— 但轮次照记,
        // 用户明确点"关联"就说明这条信息要留下,不该因为状态机被吞掉。
        // 是否真的推进由聚合内的 AddRound 判断(只有 已投递/初筛 会转面试中)。
        var round = app.AddRound(stage, request.ScheduledDate, NullIfBlank(request.Interviewer),
            NullIfBlank(request.Format), NullIfBlank(request.Notes));

        await db.SaveChangesAsync(ct);
        return Result.Success(new LinkInviteResultDto(app.Id, round.Id, round.Order, app.Status.ToString()));
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

// ============================================================================

internal static class JdAiEnricher
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>返回 null 表示"这次精修没拿到可用结果",调用方保持原样即可。</summary>
    public static async Task<ParsedJdDto?> EnrichAsync(IAiGatewayClient ai, string raw,
        ParsedJdDto draft, CancellationToken ct)
    {
        var material = draft.JdText ?? raw;
        if (material.Length > 12_000) material = material[..12_000];

        var prompt = $$"""
            你是招聘信息结构化提取器。从下面的内容中提取字段,只输出一个 JSON 对象,不要任何解释文字。

            字段(缺失就给 null,不要编造):
            {
              "company": "公司名称",
              "role": "职位名称",
              "location": "工作地点",
              "salary": "薪资范围原文或规范化写法",
              "workMode": "Remote | Hybrid | Onsite",
              "jobType": "Full-time | Part-time | Permanent | Contract | Temporary | Freelance | Internship 或 null",
              "jdSummary": "80-150 字的中文摘要",
              "companyDescription": "公司介绍段落原文(About the company 段)",
              "requirements": "任职要求/资格段落原文",
              "benefits": "福利待遇段落原文",
              "workAuthorization": "工作身份要求原文或归一写法(如 Citizen or PR required / Open work permit accepted / Must be legally authorized to work in Canada / No sponsorship available)",
              "matchKeywords": "与岗位强相关的技术关键词,中文逗号分隔,最多 12 个",
              "priority": "Low | Medium | High | Critical",
              "deadline": "YYYY-MM-DD 或 null"
            }

            内容(三短横之间):
            ---
            {{material}}
            ---
            """;

        var result = await ai.CompleteAsync("jd-parse", prompt,
            "你只输出 JSON,不要 markdown 代码块,不要解释。", 0.1, 1200, ct);

        var json = ExtractJson(result.Text);
        if (json is null) return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? S(string name) => root.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()!.Trim() : null;

        var company = draft.Company ?? S("company");
        var role = draft.Role ?? S("role");
        var location = draft.Location ?? S("location");
        var salary = draft.Salary ?? S("salary");
        var workMode = draft.WorkMode ?? S("workMode");
        var jobType = draft.JobType ?? S("jobType");

        // 摘要与关键词:AI 的理解力强于取前 N 字,所以 AI 结果优先
        var jdSummary = S("jdSummary") ?? draft.JdSummary;

        // 段落级字段:规则没解出来时用 AI 的
        var companyDescription = draft.CompanyDescription ?? S("companyDescription");
        var requirements = draft.Requirements ?? S("requirements");
        var benefits = draft.Benefits ?? S("benefits");
        var workAuthorization = draft.WorkAuthorization ?? S("workAuthorization");

        // 关键词取并集 —— 规则命中的偏"技术名词",模型给的偏"岗位要求",两者互补
        var keywords = MergeKeywords(draft.MatchKeywords, S("matchKeywords"));

        var priority = draft.Priority;
        var p = S("priority");
        if (Enum.TryParse<Priority>(p, true, out _)) priority = p;

        var deadline = draft.Deadline;
        var d = S("deadline");
        if (deadline is null && DateOnly.TryParse(d, CultureInfo.InvariantCulture, out var dd)) deadline = dd;

        var conf = new Dictionary<string, int>(draft.Confidence);
        if (company is not null) conf["company"] = Math.Max(conf.GetValueOrDefault("company"), 90);
        if (role is not null) conf["role"] = Math.Max(conf.GetValueOrDefault("role"), 90);
        if (jobType is not null) conf["jobType"] = Math.Max(conf.GetValueOrDefault("jobType"), 85);
        if (jdSummary is not null) conf["jdSummary"] = 85;
        if (companyDescription is not null) conf["companyDescription"] = Math.Max(conf.GetValueOrDefault("companyDescription"), 85);
        if (requirements is not null) conf["requirements"] = Math.Max(conf.GetValueOrDefault("requirements"), 85);
        if (benefits is not null) conf["benefits"] = Math.Max(conf.GetValueOrDefault("benefits"), 85);
        if (workAuthorization is not null) conf["workAuthorization"] = Math.Max(conf.GetValueOrDefault("workAuthorization"), 85);

        return draft with
        {
            Company = company, Role = role, Location = location, Salary = salary,
            WorkMode = workMode, JobType = jobType, JdSummary = jdSummary, MatchKeywords = keywords,
            Priority = priority, Deadline = deadline, Confidence = conf,
            CompanyDescription = companyDescription, Requirements = requirements, Benefits = benefits,
            WorkAuthorization = workAuthorization
        };
    }

    private static string? MergeKeywords(string? ruleBased, string? aiBased)
    {
        var set = new List<string>();
        void Add(string? src)
        {
            if (string.IsNullOrWhiteSpace(src)) return;
            foreach (var part in src.Split(new char[] { ',', '、', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                if (!set.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) set.Add(p);
            }
        }
        Add(ruleBased);
        Add(aiBased);
        return set.Count == 0 ? null : string.Join(", ", set.Take(16));
    }

    /// <summary>模型偶尔会裹一层 ```json 或前后加寒暄,这里把真正的 JSON 抠出来。</summary>
    private static string? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        s = Regex.Replace(s, @"^```(?:json)?", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"```$", string.Empty).Trim();

        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return s[start..(end + 1)];
    }
}

// ============================================================================

internal static class InviteAiEnricher
{
    public static async Task<ParsedInviteDto?> EnrichAsync(IAiGatewayClient ai, string raw,
        ParsedInviteDto draft, CancellationToken ct)
    {
        var material = raw.Length > 8_000 ? raw[..8_000] : raw;

        var prompt = $$"""
            你是面试邀请信息提取器。从下面的邮件/消息中提取字段,只输出一个 JSON 对象,不要任何解释。

            字段(缺失就给 null,不要编造):
            {
              "company": "公司名称",
              "role": "职位名称",
              "roundNo": 轮次数字(第几轮,整数),
              "stage": "Screen | Technical | SystemDesign | Behavioral | Final",
              "scheduledAt": "面试时间,ISO 8601(如 2026-10-05T14:30:00),带时区偏移;无法确定时区就按 -04:00(多伦多夏令时)",
              "format": "Phone | Video | Onsite",
              "location": "地点或会议平台",
              "interviewer": "面试官姓名或团队"
            }

            内容(三短横之间):
            ---
            {{material}}
            ---
            """;

        var result = await ai.CompleteAsync("invite-parse", prompt,
            "你只输出 JSON,不要 markdown 代码块,不要解释。", 0.1, 800, ct);

        var json = ExtractJson(result.Text);
        if (json is null) return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? S(string name) => root.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

        int? N(string name) => root.TryGetProperty(name, out var v) switch
        {
            true when v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) => n,
            true when v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n2) => n2,
            _ => null
        };

        var at = draft.ScheduledAt;
        var s = S("scheduledAt");
        if (at is null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var parsed))
            at = parsed;

        return draft with
        {
            Company = draft.Company ?? S("company"),
            Role = draft.Role ?? S("role"),
            RoundNo = draft.RoundNo ?? N("roundNo"),
            Stage = draft.Stage ?? S("stage"),
            ScheduledAt = at,
            Format = draft.Format ?? S("format"),
            Location = draft.Location ?? S("location"),
            Interviewer = draft.Interviewer ?? S("interviewer")
        };
    }

    private static string? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = Regex.Replace(text.Trim(), @"^```(?:json)?", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"```$", string.Empty).Trim();
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start < 0 || end <= start ? null : s[start..(end + 1)];
    }
}
