using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.Services.Interviews.Domain;

/// <summary>
/// 分析任务类型。M1 只有转写(触发 Worker 的完整管线);
/// Analysis 预留给 M4(LLM 结构化分析拆分独立任务)。
/// </summary>
public enum AnalysisJobType
{
    /// <summary>转写 + 六维分析的完整管线(由「开始转写」触发)。</summary>
    Transcription,
    /// <summary>仅六维分析(转写已完成时)。预留。</summary>
    Analysis
}

/// <summary>
/// 任务台账状态机:Pending → Dispatched → Succeeded / Failed / Dead。
///
/// 这是"分析任务台账 + 发件箱"的一体化设计(2026-09-23 M1.2):
///   · Pending:领域事件落库时同事务写入的行,等待派发器投递到 MQ;
///   · Dispatched:已投递(至少一次),Worker 在处理中;
///   · Succeeded/Failed:回写端点(转写稿/分析结果/失败上报)关单;
///   · Dead:投递重试耗尽(MQ 长期不可用),人工排查后可重开。
///
/// 为什么不用 MassTransit EF Outbox:它要求"显式事务内 Publish",
/// 而本仓库的事件分发在 SavedChanges 拦截器里(事务已提交),
/// 强行改造要动 BuildingBlocks 的分发语义,殃及全部 7 个服务。
/// 台账方案把同样的原子性(行与领域变更同 SaveChanges)与至少一次
/// 投递(派发器重试)拿到手,还多了一份用户可查的任务历史。
/// </summary>
public enum AnalysisJobStatus
{
    Pending,
    Dispatched,
    Succeeded,
    Failed,
    Dead
}

/// <summary>
/// 分析任务台账 —— 一行 = 一次"请 Worker 干活"的请求。
///
/// 不变量:
///   · 同一条目同一类型**同时最多一个未关单的任务**(部分唯一索引强制,
///     幂等键 = "{entryId}:{type}"),重复点「开始转写」不会堆任务;
///   · 历史全保留:任务关单后再次触发会新建一行,失败原因可回溯。
/// </summary>
public sealed class AnalysisJob : Entity
{
    private AnalysisJob() { }

    internal AnalysisJob(Guid interviewEntryId, AnalysisJobType type, string idempotencyKey,
        string payloadJson)
    {
        InterviewEntryId = interviewEntryId;
        JobType = type;
        IdempotencyKey = idempotencyKey;
        PayloadJson = payloadJson;
        Status = AnalysisJobStatus.Pending;
        Attempts = 0;
        MaxAttempts = 5;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid InterviewEntryId { get; private set; }
    public AnalysisJobType JobType { get; private set; }

    /// <summary>幂等键:"{entryId}:{type}"。未关单的任务上唯一。</summary>
    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>派发负载 JSON:{assetId, storagePath, userId}。派发器据此重建集成事件。</summary>
    public string PayloadJson { get; private set; } = string.Empty;

    public AnalysisJobStatus Status { get; private set; }

    /// <summary>已投递次数(成功或失败都算一次尝试)。</summary>
    public int Attempts { get; private set; }

    /// <summary>投递重试上限,超过判 Dead。默认 5。</summary>
    public int MaxAttempts { get; private set; } = 5;

    /// <summary>下次可投递时间(指数退避)。Pending 时由失败尝试推进。</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>终态失败原因(Worker 上报的失败,或投递耗尽的最后错误)。</summary>
    public string? FailureReason { get; private set; }

    /// <summary>最近一次投递失败的技术原因(网络/连接类,与业务失败区分)。</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DispatchedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsOpen => Status is AnalysisJobStatus.Pending or AnalysisJobStatus.Dispatched;

    /// <summary>
    /// IsOpen 的**数据库可翻译**形式(进 Where/Any 的查询一律用它)。
    /// IsOpen 是计算属性(没有对应列),直接写进 LINQ 会在查询编译期抛
    /// "could not be translated" 的 InvalidOperationException ——
    /// M1.2 联调时「开始转写 409」的根因就是它(异常被误映射成状态冲突)。
    /// </summary>
    public static readonly System.Linq.Expressions.Expression<Func<AnalysisJob, bool>> IsOpenFilter =
        j => j.Status == AnalysisJobStatus.Pending || j.Status == AnalysisJobStatus.Dispatched;

    // ---------- 派发器/回写端点调用的状态推进 ----------

    /// <summary>派发器:投递成功 → Dispatched(重复投递也会走这里,刷新时间戳)。</summary>
    internal void MarkDispatched()
    {
        Status = AnalysisJobStatus.Dispatched;
        Attempts++;
        DispatchedAt = DateTimeOffset.UtcNow;
        LastError = null;
    }

    /// <summary>派发器:投递失败 → 记错误、指数退避;耗尽判 Dead。</summary>
    internal void MarkAttemptFailed(string error)
    {
        Attempts++;
        LastError = error;
        if (Attempts >= MaxAttempts)
        {
            Status = AnalysisJobStatus.Dead;
            FailureReason = $"投递重试 {MaxAttempts} 次耗尽:{error}";
            CompletedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            // 退避:1m → 2m → 4m → 8m(封顶),MQ 抖动时不至于刷屏
            var delay = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Attempts - 1), 8));
            NextAttemptAt = DateTimeOffset.UtcNow + delay;
        }
    }

    /// <summary>回写端点:任务成功关单(幂等 —— 已关单的不再改)。</summary>
    internal void Close(AnalysisJobStatus status, string? reason)
    {
        if (!IsOpen) return;   // 已关单:重复回写不覆盖首次结论
        Status = status;
        FailureReason = reason;
        CompletedAt = DateTimeOffset.UtcNow;
    }
}
