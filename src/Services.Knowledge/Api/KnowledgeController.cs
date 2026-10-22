using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using YourInterview.BuildingBlocks.Security;
using YourInterview.BuildingBlocks.Web;
using YourInterview.Services.Knowledge.Application;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Knowledge.Api;

/// <summary>
/// 技术栈知识库 API。
///
/// 权限模型:每个端点挂一个权限策略("perm:knowledge.read" 这种),
/// 而不是判断角色名 —— 新增角色时后端一行代码都不用改(开闭原则在权限上的落地)。
/// </summary>
[ApiController]
[Route("api/knowledge")]
[Authorize]
public sealed class KnowledgeController(ISender sender, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>当前用户(COD-01/票03):所有查询/命令显式带入,Handler 按 (Id, UserId) 收口。</summary>
    private Guid Me => currentUser.RequireUserId();

    // ---------------- 查询 ----------------

    /// <summary>分页查询知识点。dueOnly=true 只返回今天该复习的。</summary>
    [HttpGet]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeRead)]
    public async Task<IResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? topic = null, [FromQuery] string? mastery = null,
        [FromQuery] string? source = null, [FromQuery] string? tag = null,
        [FromQuery] string? search = null, [FromQuery] bool dueOnly = false,
        [FromQuery] string? company = null, [FromQuery] Guid? entryId = null,
        [FromQuery] bool includeDeleted = false,
        CancellationToken ct = default)
    {
        var r = await sender.Send(new ListKnowledgeQuery(Me, page, pageSize, topic, mastery, source,
            tag, search, dueOnly, company, entryId, includeDeleted), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>知识点详情(含复习流水与关联)。</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeRead)]
    public async Task<IResult> Get(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetKnowledgeItemQuery(Me, id), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>所有技术分类 + 每类数量(前端左侧目录树)。</summary>
    [HttpGet("topics")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeRead)]
    public async Task<IResult> Topics(CancellationToken ct)
    {
        var r = await sender.Send(new GetTopicsQuery(Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>统计:掌握度分布、按主题分布、今日待复习、最薄弱主题。</summary>
    [HttpGet("stats")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeRead)]
    public async Task<IResult> Stats(CancellationToken ct)
    {
        var r = await sender.Send(new GetKnowledgeStatsQuery(Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>未来 N 天的复习计划(前端日历视图)。</summary>
    [HttpGet("due")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeRead)]
    public async Task<IResult> Due([FromQuery] int days = 7, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetDuePlanQuery(Me, days), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>来源公司清单(需求 6.4.4:按公司反查"这题是哪场面出来的")。</summary>
    [HttpGet("sources")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeRead)]
    public async Task<IResult> Sources(CancellationToken ct)
    {
        var r = await sender.Send(new GetSourceCompaniesQuery(Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>反查某条知识点的来源:公司 / 日期 / 轮次 / 同场面试的其它题。</summary>
    [HttpGet("{id:guid}/trail")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeRead)]
    public async Task<IResult> Trail(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetSourceTrailQuery(Me, id), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>重复条目分组(需求 6.4.3 去重)。</summary>
    [HttpGet("duplicates")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeRead)]
    public async Task<IResult> Duplicates([FromQuery] int maxGroups = 50, CancellationToken ct = default)
    {
        var r = await sender.Send(new FindDuplicatesQuery(Me, maxGroups), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>合并两条重复条目(保留方的已有内容不动,只补空缺)。</summary>
    [HttpPost("merge")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> Merge([FromBody] MergeBody body, CancellationToken ct)
    {
        var r = await sender.Send(new MergeDuplicatesCommand(Me, body.KeepId, body.MergeId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>
    /// 从实战机经分析结果导入候选题(需求 6.4.2 / 验收 5)。
    /// 幂等:同一条候选重复导入只会安静跳过,不会产生重复条目。
    /// </summary>
    [HttpPost("import-candidates")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> ImportCandidates([FromBody] ImportCandidatesBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ImportInterviewCandidatesCommand(
            Me, body.EntryId, body.Company, body.Date, body.RoundNo, body.RoundStage,
            body.ApplicationId, body.Items ?? new List<InterviewCandidateRow>()), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    // ---------------- 命令 ----------------

    [HttpPost]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> Create([FromBody] CreateKnowledgeCommand body, CancellationToken ct)
    {
        var r = await sender.Send(body with { UserId = Me }, ct);
        return r.IsSuccess
            ? Results.Created($"/api/knowledge/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> Update(Guid id, [FromBody] UpdateKnowledgeBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateKnowledgeCommand(Me, id, body.Title, body.Topic,
            body.SubTopic, body.Question, body.Difficulty, body.Importance, body.TagsJson,
            body.ConceptExplanation, body.MyAnswer, body.BetterAnswer, body.KeyPointsJson,
            body.CommonMistakesJson, body.FollowUpsJson, body.ReferencesJson, body.SourcesJson), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>只更新我的回答(Playbook Q&A 页签用)。</summary>
    public sealed record UpdateMyAnswerBody(string? MyAnswer);

    [HttpPut("{id:guid}/answer")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> UpdateMyAnswer(Guid id, [FromBody] UpdateMyAnswerBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateMyAnswerCommand(Me, id, body.MyAnswer), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    public sealed record UpdateBetterAnswerBody(string? BetterAnswer);

    /// <summary>只更新推荐答案(AI Practice From Tech 编辑写回用)。</summary>
    [HttpPut("{id:guid}/better-answer")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> UpdateBetterAnswer(Guid id, [FromBody] UpdateBetterAnswerBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateBetterAnswerCommand(Me, id, body.BetterAnswer), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    public sealed record UpdateQuestionBody(string? Question);

    /// <summary>只更新题目(AI Practice From Tech 重命名写回用)。</summary>
    [HttpPut("{id:guid}/question")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> UpdateQuestion(Guid id, [FromBody] UpdateQuestionBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateQuestionCommand(Me, id, body.Question), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    public sealed record UpdateFromTechBody(string? Question, string? BetterAnswer);

    /// <summary>From Tech 一次写回题目+推荐答案(避免并发 xmin 冲突)。</summary>
    [HttpPut("{id:guid}/from-tech")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> UpdateFromTech(Guid id, [FromBody] UpdateFromTechBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateFromTechCommand(Me, id, body.Question, body.BetterAnswer), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeDelete)]
    public async Task<IResult> Delete(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteKnowledgeCommand(Me, id), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>恢复已删除的知识点 (软删除恢复)。</summary>
    [HttpPost("{id:guid}/restore")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> Restore(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new RestoreKnowledgeCommand(Me, id), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>重命名分类:批量更新所有使用该分类的条目。</summary>
    [HttpPut("topics/rename")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> RenameTopic([FromBody] RenameTopicBody body, CancellationToken ct)
    {
        var r = await sender.Send(new RenameTopicCommand(Me, body.OldName, body.NewName), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>删除分类:名下条目的 topic 置空(变未分类),不删条目。</summary>
    [HttpDelete("topics/{name}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> DeleteTopic(string name, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteTopicCommand(Me, name), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>记录一次复习 —— 会按 SM-2 算法重算下次复习时间。</summary>
    [HttpPost("{id:guid}/review")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> Review(Guid id, [FromBody] ReviewBody body, CancellationToken ct)
    {
        var r = await sender.Send(new RecordReviewCommand(Me, id, body.Result, body.ConfidenceBefore,
            body.ConfidenceAfter, body.Note, body.DurationSeconds), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>手工调整掌握度。</summary>
    [HttpPost("{id:guid}/mastery")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> ChangeMastery(Guid id, [FromBody] MasteryBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ChangeMasteryCommand(Me, id, body.Mastery), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>建立知识点之间的关联(前置/对比/延伸)—— 用于知识图谱。</summary>
    [HttpPost("{id:guid}/relations")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> AttachRelation(Guid id, [FromBody] RelationBody body, CancellationToken ct)
    {
        var r = await sender.Send(new AttachRelationCommand(Me, id, body.RelatedItemId,
            body.RelationType, body.Note), ct);
        return r.IsSuccess
            ? Results.Created($"/api/knowledge/{id}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    // ---------- COD-10c/票16:Tech Stack 本地数据(原 localStorage 上后端) ----------

    /// <summary>当前用户的自定义空分类(ts.customCategories 的后端真源)。</summary>
    [HttpGet("custom-categories")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeRead)]
    public async Task<IResult> ListCustomCategories(CancellationToken ct)
    {
        var r = await sender.Send(new ListCustomCategoriesQuery(Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>新增空分类(幂等)。</summary>
    [HttpPut("custom-categories/{name}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> AddCustomCategory(string name, CancellationToken ct)
    {
        var r = await sender.Send(new AddCustomCategoryCommand(Me, name), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>
    /// 删除空分类(幂等)。权限用 KnowledgeWrite 而非 KnowledgeDelete:默认 User
    /// 角色没有 KnowledgeDelete,删自己的空分类是普通用户日常操作 ——
    /// 与既有 DeleteTopic 用 KnowledgeWrite、票14 删问题用 JobsWrite 的先例同口径。
    /// </summary>
    [HttpDelete("custom-categories/{name}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> RemoveCustomCategory(string name, CancellationToken ct)
    {
        var r = await sender.Send(new RemoveCustomCategoryCommand(Me, name), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>
    /// 一次性导入本地分类(localStorage ts.customCategories)。幂等,返回对账
    /// { imported, total } —— 重复导入 imported=0、total 不变。
    /// </summary>
    [HttpPost("custom-categories/import")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> ImportCustomCategories([FromBody] ImportCategoriesBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ImportCustomCategoriesCommand(Me, body.Names ?? []), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>当前用户的全部复习活动(热力图/连击的后端真源,日期升序)。</summary>
    [HttpGet("review-activity")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeRead)]
    public async Task<IResult> GetReviewActivity(CancellationToken ct)
    {
        var r = await sender.Send(new GetReviewActivityQuery(Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>
    /// 记录一次复习:前端按本地日历口径算好日期上报,后端按 (用户, 日期) 累加。
    /// </summary>
    [HttpPost("review-activity/record")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> RecordReviewActivity([FromBody] RecordReviewActivityBody body, CancellationToken ct)
    {
        var r = await sender.Send(new RecordReviewActivityCommand(Me, body.Date ?? string.Empty), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>
    /// 一次性导入本地复习记录(localStorage ts.reviewActivity)。幂等(同日取
    /// max 不虚增),返回对账 { imported, total }(total = 总天数)。
    /// </summary>
    [HttpPost("review-activity/import")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> ImportReviewActivity([FromBody] ImportReviewActivityBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ImportReviewActivityCommand(Me, body.Entries ?? []), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>批量导入(前端可一次粘贴一堆题目)。幂等:同 topic+title 不重复插。</summary>
    [HttpPost("import")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> Import([FromBody] ImportBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ImportKnowledgeCommand(Me, body.Items), ct);
        return r.IsSuccess ? Results.Ok(new { imported = r.Value }) : r.ToProblemDetails();
    }
}

// ---------------- 请求体 ----------------
// 与命令分开定义:请求体是 HTTP 契约(可变),命令是应用层契约。分开了以后改 API 不动命令。

public sealed record UpdateKnowledgeBody(
    string Title, string Topic, string? SubTopic, string Question, int Difficulty, int Importance,
    string? TagsJson, string? ConceptExplanation, string? MyAnswer, string? BetterAnswer,
    string? KeyPointsJson, string? CommonMistakesJson, string? FollowUpsJson, string? ReferencesJson,
    string? SourcesJson = null);

public sealed record ReviewBody(string Result, int? ConfidenceBefore = null,
    int? ConfidenceAfter = null, string? Note = null, int? DurationSeconds = null);

public sealed record MasteryBody(string Mastery);

public sealed record RelationBody(Guid RelatedItemId, string RelationType, string? Note = null);

public sealed record ImportBody(List<ImportKnowledgeRow> Items);

/// <summary>合并重复条目请求体。</summary>
public sealed record MergeBody(Guid KeepId, Guid MergeId);

public sealed record RenameTopicBody(string OldName, string NewName);

// COD-10c/票16 请求体
public sealed record ImportCategoriesBody(List<string>? Names);
public sealed record RecordReviewActivityBody(string? Date);
public sealed record ImportReviewActivityBody(List<ReviewActivityImportItem>? Entries);

/// <summary>
/// 机经候选题导入请求体。
/// EntryId 与 ClientKey 至少给一个 —— 两者都是幂等的依据,
/// 都没有时后端只能退回"同分类 + 同标题"判重,用户改过标题就会重复插入。
/// </summary>
public sealed record ImportCandidatesBody(
    Guid? EntryId, string? Company, DateOnly? Date, int? RoundNo, string? RoundStage,
    Guid? ApplicationId, List<InterviewCandidateRow>? Items);
