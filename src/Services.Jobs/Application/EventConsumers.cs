using MassTransit;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using YourInterview.SharedContracts.Events;

namespace YourInterview.Services.Jobs.Application;

/// <summary>
/// 面试结果回写 Tracker(M1.5;票04 REQ-02+ARC-07 重写)。
///
/// 结构化契约优先:事件带 SharedContracts 的 InterviewOutcome 枚举时以枚举为准
/// 更新轮次投影与投递状态(Result 自由文本只作展示/日志,哪怕文本与枚举矛盾也不猜)。
/// Jobs 侧无对应轮次时由聚合按轮号补建投影并按枚举语义推进状态 —— 不再静默跳过
/// (旧实现 RecordRoundOutcome 抛 KeyNotFound 被吞,Tracker 与 Playbook 同屏矛盾)。
///
/// 无枚举的旧事件(契约升级前已发出的消息)才走 LegacyOutcomeTextAdapter 回落:
/// 关键词映射**只许存在于该 adapter 一处**,新发布端一律填枚举。
///
/// 申请不存在/归属不一致仍是记日志跳过(消息重投也变不出结果,别进死信;票03 语义)。
/// </summary>
public sealed class InterviewOutcomeRecordedConsumer(
JobsDbContext db,
ILogger<InterviewOutcomeRecordedConsumer> logger): IConsumer<InterviewOutcomeRecorded>
{
public Task Consume(ConsumeContext<InterviewOutcomeRecorded> context)
=> HandleAsync(context.Message, context.CancellationToken);

/// <summary>消费者核心处理(与 MassTransit 上下文解耦,便于测试与直调验证)。</summary>
public async Task HandleAsync(InterviewOutcomeRecorded e, CancellationToken ct)
{
var outcome = e.Outcome is not null
? FromContract(e.Outcome.Value)
: LegacyOutcomeTextAdapter.MapOutcome(e.Result);
if (outcome is null)
{
logger.LogInformation("结果「{Result}」无结构化枚举且不含可识别关键词,不回写(申请 {AppId} 第 {Round} 轮)",
e.Result, e.ApplicationId, e.RoundNo);
return;
}

// COD-01:消费者无 HTTP 上下文,全局过滤器 fail-closed —— 必须 IgnoreQueryFilters
// 按 Id 取,再以事件自带的 UserId 显式校验归属,不一致记日志跳过(不进死信)。
var app = await db.Applications.IgnoreQueryFilters().Include(x => x.Rounds)
.FirstOrDefaultAsync(x => x.Id == e.ApplicationId &&!x.IsDeleted, ct);
if (app is null)
{
logger.LogWarning("申请 {AppId} 不存在(可能已删),结果回写跳过", e.ApplicationId);
return;
}
if (app.UserId!= e.UserId)
{
logger.LogWarning("申请 {AppId} 归属用户与事件用户 {UserId} 不一致,结果回写跳过",
e.ApplicationId, e.UserId);
return;
}

// 聚合负责:无对应轮次 → 补建投影;按枚举语义推进投递状态(不再抛 KeyNotFound)。
app.RecordRoundOutcome(e.RoundNo, outcome.Value, $"实战机经回写:{e.Result}");
await db.SaveChangesAsync(ct);
logger.LogInformation("结果回写完成:申请 {AppId} 第 {Round} 轮 → {Outcome}(结构化枚举 {ContractOutcome})",
e.ApplicationId, e.RoundNo, outcome.Value, e.Outcome?.ToString()?? "(旧事件回落)");
}

/// <summary>
/// SharedContracts 契约枚举 → Jobs 轮次枚举(票04):显式映射,两侧枚举各自演进时回这里对账。
/// 唯一命名差异:契约的 Rejected 在 Jobs 侧叫 Failed(同一语义:本轮未通过)。
/// </summary>
private static RoundOutcome FromContract(InterviewOutcome outcome) => outcome switch
{
InterviewOutcome.Pending => RoundOutcome.Pending,
InterviewOutcome.Passed => RoundOutcome.Passed,
InterviewOutcome.Rejected => RoundOutcome.Failed,
InterviewOutcome.Ghosted => RoundOutcome.Ghosted,
InterviewOutcome.Cancelled => RoundOutcome.Cancelled,
InterviewOutcome.NoShow => RoundOutcome.NoShow,
_ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "未知契约轮次结果")
};
}

/// <summary>
/// 轮次排期回写 Tracker(票114,REQ-02 残余/CONF-DASH-1)。
///
/// Playbook 排期落库 → Interviews 发 InterviewRoundScheduled → 本消费者调聚合
/// RecordRoundSchedule:同步投影轮次排期;投递尚在 Saved/Applied/Screen 且该轮
/// 未出结果时推进到 Interview,使 Dashboard interviewed 计数(Jobs 状态口径)
/// 与 Upcoming(Interviews 排期口径)同屏不矛盾。推进/不覆盖/撤销/幂等/乱序
/// 语义全在聚合 RecordRoundSchedule(定版见 evidence-114/STATUS §4)。
///
/// 与结果消费者同构:无 HTTP 上下文 → IgnoreQueryFilters 按 Id 取投递、
/// 事件 UserId 显式校验归属;申请不存在/归属不一致记日志跳过(不进死信)。
/// 本消费者不发任何事件 —— Jobs 侧排期同步不成回环。
/// </summary>
public sealed class InterviewRoundScheduledConsumer(
JobsDbContext db,
ILogger<InterviewRoundScheduledConsumer> logger): IConsumer<InterviewRoundScheduled>
{
public Task Consume(ConsumeContext<InterviewRoundScheduled> context)
=> HandleAsync(context.Message, context.CancellationToken);

/// <summary>消费者核心处理(与 MassTransit 上下文解耦,便于测试与直调验证)。</summary>
public async Task HandleAsync(InterviewRoundScheduled e, CancellationToken ct)
{
var app = await db.Applications.IgnoreQueryFilters().Include(x => x.Rounds)
.FirstOrDefaultAsync(x => x.Id == e.ApplicationId &&!x.IsDeleted, ct);
if (app is null)
{
logger.LogWarning("申请 {AppId} 不存在(可能已删),排期回写跳过", e.ApplicationId);
return;
}
if (app.UserId!= e.UserId)
{
logger.LogWarning("申请 {AppId} 归属用户与事件用户 {UserId} 不一致,排期回写跳过",
e.ApplicationId, e.UserId);
return;
}

app.RecordRoundSchedule(e.RoundNo, e.Stage, e.ScheduledDate);
await db.SaveChangesAsync(ct);
logger.LogInformation("排期回写完成:申请 {AppId} 第 {Round} 轮 → {Date}",
e.ApplicationId, e.RoundNo, e.ScheduledDate?.ToString("yyyy-MM-dd")?? "(撤销)");
}
}

/// <summary>
///
/// 服务对象:契约升级(票04)前已发出、没有 Outcome 枚举的 InterviewOutcomeRecorded 消息。
/// 新发布端一律填结构化枚举,不得再扩大此映射的适用范围;
/// 待旧消息自然排空后,本 adapter 应整体删除。
/// 规则(顺序即优先级,上者胜):
///  0. 英文受控词整串精确匹配(票58):与 Interviews 侧 InterviewHandlers.DeriveOutcome
///     规则 2 完全一致 —— Trim 后以契约枚举名(Pending/Passed/Rejected/Ghosted/
///     Cancelled/NoShow)忽略大小写精确解析,名字对上才算(防 "1" 之类数字串被
///     Enum.TryParse 误收)。命中按契约枚举走既有消费者语义,枚举映射同
///     FromContract(唯一命名差异:Rejected → Jobs 侧 Failed)。
///     ⚠ 本词表与 DeriveOutcome 规则 2 是同一张表:任一侧改词表,必须回此对账。
///  1. 中文关键词回落(用户原话 → 轮次结果,注意"未通过"先于"通过"判断):
///     未通过/不通过/拒/挂 → Failed;通过 → Passed;待定/其它 → null(不动,等人确认)。
/// </summary>
internal static class LegacyOutcomeTextAdapter
{
public static RoundOutcome? MapOutcome(string result)
{
var r = result.Trim();

// 票58:受控词整串精确匹配(对照 DeriveOutcome 规则 2,见类注释),先于关键词回落。
if (Enum.TryParse<InterviewOutcome>(r, ignoreCase: true, out var parsed)
&& string.Equals(parsed.ToString(), r, StringComparison.OrdinalIgnoreCase))
{
return parsed switch
{
InterviewOutcome.Pending => RoundOutcome.Pending,
InterviewOutcome.Passed => RoundOutcome.Passed,
InterviewOutcome.Rejected => RoundOutcome.Failed,
InterviewOutcome.Ghosted => RoundOutcome.Ghosted,
InterviewOutcome.Cancelled => RoundOutcome.Cancelled,
InterviewOutcome.NoShow => RoundOutcome.NoShow,
_ => null
};
}

if (r.Contains("未通过") || r.Contains("不通过") || r.Contains("拒") || r.Contains("挂"))
return RoundOutcome.Failed;
if (r.Contains("通过"))
return RoundOutcome.Passed;
return null;
}
}
