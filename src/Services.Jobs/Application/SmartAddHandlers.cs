using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using YourInterview.Services.Jobs.Infrastructure.Services;

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

/// <summary>解析一段粘贴内容。UseAi=false 时只做确定性解析(未配 AI 凭据也能用)。</summary>
public sealed record ParseJdCommand(string Raw, bool UseAi = true) : IRequest<Result<ParsedJdDto>>;

public sealed class ParseJdCommandHandler(IAiGatewayClient ai)
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
                kind, ParseOutcome.Failed, "已识别为图片,但当前尚未接入图片识别,请改为粘贴 JD 文本,或直接手动填写。",
                null, null, null, null, null, null, null, null, null, null, null, null,
                new Dictionary<string, int>(), false, true));
        }

        var draft = JdPasteParser.Parse(raw, kind);
        var usedAi = false;
        string? aiNote = null;

        if (request.UseAi)
        {
            // AI 只是"补缺的帮手",不是必经之路:失败一律降级为确定性结果,绝不把错误抛给用户。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var enriched = await JdAiEnricher.EnrichAsync(ai, raw, draft, cts.Token);
                if (enriched is not null) { draft = enriched; usedAi = true; }
            }
            catch (AiGatewayException ex)
            {
                aiNote = ex.IsConfigurationError
                    ? "未配置 AI 凭据,已只按规则解析。"
                    : "AI 精修未成功,已只按规则解析。";
            }
            catch (OperationCanceledException)
            {
                aiNote = "AI 精修超时,已只按规则解析。";
            }
            catch (Exception)
            {
                aiNote = "AI 精修未成功,已只按规则解析。";
            }
        }

        var filled = JdPasteParser.CountFilled(draft);
        var status = filled >= 4 ? ParseOutcome.Ok
                   : filled >= 1 ? ParseOutcome.Partial
                   : ParseOutcome.Failed;

        var message = status switch
        {
            ParseOutcome.Ok => usedAi ? "已解析,并经过 AI 补齐全字段。" : "已解析出主要字段。",
            ParseOutcome.Partial => "只解析出部分字段,请补全后再保存。"
                                    + (aiNote is null ? "" : " " + aiNote),
            _ => "没能识别出岗位信息,请手动填写。"
                 + (aiNote is null ? "" : " " + aiNote)
        };

        return Result.Success(draft with
        {
            Status = status,
            Message = message,
            UsedAi = usedAi,
            NeedsManualReview = status != ParseOutcome.Ok
        });
    }
}

// ============================ 一键建档(解析结果 → 投递记录) ============================

public sealed record SmartAddApplicationCommand(
    string? Company, string? Role, string? Location, string? Salary, string? WorkMode,
    string? JdSummary, string? MatchKeywords, string? Priority, string? Link,
    string? JdText, string? Source, string? Status, DateOnly? Deadline = null)
    : IRequest<Result<SmartAddResultDto>>;

public sealed record SmartAddResultDto(
    Guid ApplicationId, Guid CompanyId, string CompanyName, bool CompanyCreated, string Status);

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
        var existing = await db.Companies
            .FirstOrDefaultAsync(c => c.Name.ToLower() == companyName.ToLower(), ct);

        var created = false;
        if (existing is null)
        {
            // 只在链接指向公司自有站点时才把它当官网 —— LinkedIn/Indeed 这类聚合站的域名
            // 不是公司官网,存进去会让"公司官网"字段变成噪音(Logo 也会取成招聘站的图标)。
            var website = JdPasteParser.IsAggregator(request.Link) ? null : JdPasteParser.HostOf(request.Link);
            existing = new Company(companyName,
                website: website,
                logoUrl: Company.LogoUrlFromWebsite(website));
            db.Companies.Add(existing);
            created = true;
        }

        var app = new JobApplication(existing.Id, role,
            location: NullIfBlank(request.Location),
            link: NullIfBlank(request.Link),
            salary: NullIfBlank(request.Salary),
            workMode: NullIfBlank(request.WorkMode),
            source: NullIfBlank(request.Source),
            jdSummary: NullIfBlank(request.JdSummary));

        if (!string.IsNullOrWhiteSpace(request.Priority)
            && Enum.TryParse<Priority>(request.Priority, true, out var p))
            app.SetPriority(p);

        // JD 全文与出处:解析出来的正文原样存下来,日后做简历匹配/面经预测要用。
        app.SetJdContent(request.JdText, request.Link);

        if (!string.IsNullOrWhiteSpace(request.MatchKeywords))
            app.SetAnalysis(null, null, request.MatchKeywords.Trim());

        app.SetDeadline(request.Deadline);
        db.Applications.Add(app);

        // 解析出"已投递"之类的状态时顺手流转;非法流转(如越级到面试中)直接忽略,
        // 保持 Saved —— 状态机是硬约束,智能粘贴不能绕过它。
        if (!string.IsNullOrWhiteSpace(request.Status)
            && Enum.TryParse<ApplicationStatus>(request.Status, true, out var target)
            && target != ApplicationStatus.Saved
            && app.CanTransitionTo(target))
        {
            app.ChangeStatus(target, "智能粘贴建档");
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
        string? aiNote = null;

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
                aiNote = ex.IsConfigurationError ? "未配置 AI 凭据,已只按规则解析。" : "AI 精修未成功,已只按规则解析。";
            }
            catch (OperationCanceledException) { aiNote = "AI 精修超时,已只按规则解析。"; }
            catch (Exception) { aiNote = "AI 精修未成功,已只按规则解析。"; }
        }

        var filled = InvitePasteParser.CountFilled(draft);
        var status = filled >= 3 ? ParseOutcome.Ok : filled >= 1 ? ParseOutcome.Partial : ParseOutcome.Failed;
        var message = status switch
        {
            ParseOutcome.Ok => "已解析出面试安排,确认后一键关联。",
            ParseOutcome.Partial => "只解析出部分信息,请补全后再关联。" + (aiNote ?? ""),
            _ => "没能识别出面试安排,请手动填写。" + (aiNote ?? "")
        };

        return Result.Success(draft with
        {
            Status = status, Message = message, UsedAi = usedAi,
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
    Guid ApplicationId, string? Stage, DateOnly? ScheduledDate, string? Interviewer,
    string? Format, string? Notes)
    : IRequest<Result<LinkInviteResultDto>>;

public sealed record LinkInviteResultDto(Guid ApplicationId, Guid RoundId, int RoundNo, string Status);

public sealed class LinkInviteCommandHandler(JobsDbContext db)
    : IRequestHandler<LinkInviteCommand, Result<LinkInviteResultDto>>
{
    public async Task<Result<LinkInviteResultDto>> Handle(LinkInviteCommand request, CancellationToken ct)
    {
        var app = await db.Applications.Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == request.ApplicationId, ct);
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
              "jdSummary": "80-150 字的中文摘要",
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

        // 摘要与关键词:AI 的理解力强于取前 N 字,所以 AI 结果优先
        var jdSummary = S("jdSummary") ?? draft.JdSummary;

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
        if (jdSummary is not null) conf["jdSummary"] = 85;

        return draft with
        {
            Company = company, Role = role, Location = location, Salary = salary,
            WorkMode = workMode, JdSummary = jdSummary, MatchKeywords = keywords,
            Priority = priority, Deadline = deadline, Confidence = conf
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
