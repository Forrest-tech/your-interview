using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;

namespace YourInterview.Services.Interviews.Application;

// ============================ 票 296：邮箱刷新异步任务 ============================
// 动机：同步 POST 跑大邮箱回填需数分钟，代理/网关层先超时 → 504；
// 本票把长活挪到服务端后台任务，POST 立刻 202， 前端轮询状态。
// 任务跟踪只放内存；294 的断点表仍是唯一持久化续跑源（服务重启后下次 Refresh 续跑）。
// 导入路径绝不另起：任务体就是既有 RefreshMailboxCommandHandler（294 流式/legacy 共用）。

/// <summary>刷新任务态（终态全部可由前端按今日同步语义处理）。</summary>
public enum MailboxRefreshJobState
{
    None,
    Running,
    Done,
    Cancelled,
    RateLimited,
    Failed,
    NeedsReconnect
}

/// <summary>任务快照（终态计数取 handler 结果；运行中计数恒为 0，实时进度见状态查询）。</summary>
public sealed record MailboxRefreshJobSnapshot(
    Guid JobId, Guid UserId, Guid? EntryId, MailboxRefreshJobState State,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    int Added, int Pending, int SkippedDuplicates, int Failed,
    IReadOnlyList<string> FailedReasons,
    bool Cancelled, bool RateLimited, bool NeedsReconnect, bool CursorAdvanced,
    string? ErrorMessage,
    int Scanned, int Checked, string? Phase, DateTimeOffset LastActivityAt, int? BackfillPage);

/// <summary>
/// 票 306：刷新任务按作用域分槽（key = (userId, entryId)；全局任务 entryId = null，占独立槽）。
/// 同槽运行中再点返回同一任务（不重复取数）；不同槽互不干扰——条目刷新不再被全局任务遮蔽。
/// 单例；状态变更全在锁内，CTS 只在 Running 态可取消、终态后才 Dispose，杜绝竞态误触。
/// 扫描进度（Scanned/Checked/Phase）与心跳（LastActivityAt）由 handler 经 Report* 回填；
/// 强制取消直接删槽内条目，迟到的后台收尾只改已脱离的 Job 对象，绝不复活条目。
/// </summary>
public sealed class MailboxRefreshJobTracker
{
    /// <summary>票 306：无进展判定阈值（Running 且心跳超过此时长未更新 → Stale）。</summary>
    public TimeSpan StaleAfter { get; }

    public MailboxRefreshJobTracker(TimeSpan? staleAfter = null)
    {
        StaleAfter = staleAfter ?? TimeSpan.FromMinutes(5);
    }

    private sealed class Job
    {
        public Guid JobId { get; } = Guid.NewGuid();
        public required Guid UserId { get; init; }
        public required Guid? EntryId { get; init; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
        public int Scanned { get; set; }
        public int Checked { get; set; }
        public string? Phase { get; set; }
        public int? BackfillPage { get; set; }
        public CancellationTokenSource Cts { get; } = new();
        public MailboxRefreshJobState State { get; set; } = MailboxRefreshJobState.Running;
        public DateTimeOffset? FinishedAt { get; set; }
        public RefreshMailboxResult? FinalResult { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<(Guid UserId, Guid? EntryId), Job> _jobs = [];

    /// <summary>启动新任务；同槽已有运行任务时返回它（不重复取数）。</summary>
    public MailboxRefreshJobSnapshot StartOrAttach(Guid userId, Guid? entryId,
        Func<CancellationToken, Task<Result<RefreshMailboxResult>>> run)
    {
        Job job;
        lock (_gate)
        {
            if (_jobs.TryGetValue((userId, entryId), out var existing)
                && existing.State == MailboxRefreshJobState.Running)
                return SnapshotOf(existing);
            job = new Job { UserId = userId, EntryId = entryId };
            _jobs[(userId, entryId)] = job;
        }
        _ = Task.Run(() => RunAsync(job, run));
        return SnapshotOf(job);
    }

    /// <summary>取消运行任务（已落库保留，任务态随后记 Cancelled）；无运行任务时返回 false。</summary>
    public (bool Cancelled, MailboxRefreshJobSnapshot Snapshot) TryCancel(Guid userId, Guid? entryId)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue((userId, entryId), out var job)
                || job.State != MailboxRefreshJobState.Running)
                return (false, job is null ? NoneSnapshot(userId, entryId) : SnapshotOf(job));
            job.Cts.Cancel();
            return (true, SnapshotOf(job));
        }
    }

    /// <summary>
    /// 票 306：强制取消——取消 CTS 并直接删槽内条目，随后轮询见 None 即回空闲。
    /// 迟到的后台收尾只改已脱离的 Job 对象（RunAsync 从不向字典回写），条目不会复活；
    /// 删槽后 Report* 因查无任务而静默 no-op，不抛错。
    /// </summary>
    public bool TryForceCancel(Guid userId, Guid? entryId)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue((userId, entryId), out var job)
                || job.State != MailboxRefreshJobState.Running)
                return false;
            job.Cts.Cancel();
            _jobs.Remove((userId, entryId));
            return true;
        }
    }

    /// <summary>取指定槽最新任务快照（从未启动过返回 null）。</summary>
    public MailboxRefreshJobSnapshot? TryGet(Guid userId, Guid? entryId)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue((userId, entryId), out var job) ? SnapshotOf(job) : null;
        }
    }

    /// <summary>票 306：列取进度——本轮已列出的邮件 id 数累加；同时刷新心跳。查无运行任务时 no-op。</summary>
    public void ReportListed(Guid userId, Guid? entryId, int count)
    {
        if (count <= 0) return;
        lock (_gate)
        {
            if (!_jobs.TryGetValue((userId, entryId), out var job)
                || job.State != MailboxRefreshJobState.Running)
                return;
            job.Scanned += count;
            job.Phase = "listing";
            job.LastActivityAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>票 306：逐封进度——已处理（导入/待确认/跳过/失败均算）的邮件数 +1；同时刷新心跳。查无运行任务时 no-op。</summary>
    public void ReportChecked(Guid userId, Guid? entryId)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue((userId, entryId), out var job)
                || job.State != MailboxRefreshJobState.Running)
                return;
            job.Checked++;
            job.Phase = "fetching";
            job.LastActivityAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>票 306：回填页序——当前回填页号（首列页 = 1）；同时刷新心跳。查无运行任务时 no-op。</summary>
    public void ReportBackfillPage(Guid userId, Guid? entryId, int page)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue((userId, entryId), out var job)
                || job.State != MailboxRefreshJobState.Running)
                return;
            job.BackfillPage = page;
            job.LastActivityAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task RunAsync(Job job, Func<CancellationToken, Task<Result<RefreshMailboxResult>>> run)
    {
        try
        {
            var result = await run(job.Cts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                job.FinishedAt = DateTimeOffset.UtcNow;
                if (result.IsSuccess)
                {
                    job.FinalResult = result.Value;
                    job.State = result.Value.Cancelled ? MailboxRefreshJobState.Cancelled
                        : result.Value.RateLimited ? MailboxRefreshJobState.RateLimited
                        : result.Value.NeedsReconnect ? MailboxRefreshJobState.NeedsReconnect
                        : MailboxRefreshJobState.Done;
                }
                else
                {
                    job.State = MailboxRefreshJobState.Failed;
                    job.ErrorMessage = result.Error.Description;
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                job.FinishedAt = DateTimeOffset.UtcNow;
                job.State = MailboxRefreshJobState.Cancelled;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                job.FinishedAt = DateTimeOffset.UtcNow;
                job.State = MailboxRefreshJobState.Failed;
                job.ErrorMessage = ex.Message;
            }
        }
        finally
        {
            // 终态已在锁内记好，此后 TryCancel 不再碰 CTS，Dispose 安全。
            job.Cts.Dispose();
        }
    }

    private MailboxRefreshJobSnapshot SnapshotOf(Job job)
    {
        lock (_gate)
        {
            var r = job.FinalResult;
            return new MailboxRefreshJobSnapshot(job.JobId, job.UserId, job.EntryId, job.State,
                job.StartedAt, job.FinishedAt,
                r?.Added ?? 0, r?.Pending ?? 0, r?.SkippedDuplicates ?? 0, r?.Failed ?? 0,
                r?.FailedReasons ?? [], r?.Cancelled ?? false, r?.RateLimited ?? false,
                r?.NeedsReconnect ?? false, r?.CursorAdvanced ?? false, job.ErrorMessage,
                job.Scanned, job.Checked, job.Phase, job.LastActivityAt, job.BackfillPage);
        }
    }

    private static MailboxRefreshJobSnapshot NoneSnapshot(Guid userId, Guid? entryId) => new(
        Guid.Empty, userId, entryId, MailboxRefreshJobState.None, DateTimeOffset.UtcNow, null,
        0, 0, 0, 0, [], false, false, false, false, null, 0, 0, null, DateTimeOffset.UtcNow, null);
}

/// <summary>票 296：刷新任务状态 DTO（终态字段与 RefreshMailboxResult 同形，前端可直接复用结果处理）。</summary>
public sealed record MailboxRefreshJobStatusDto(
    string State, Guid? JobId, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    int Added, int Pending, int SkippedDuplicates, int Failed,
    IReadOnlyList<string> FailedReasons,
    bool Cancelled, bool RateLimited, bool NeedsReconnect, bool CursorAdvanced,
    bool BackfillActive, int? BackfillPageIndex, int? BackfillPageSize,
    string? Message,
    int Scanned, int Checked, string? Phase, DateTimeOffset? LastActivityAt, bool Stale,
    int? BackfillPage);

public sealed record GetMailboxRefreshStatusQuery(Guid UserId, Guid? EntryId = null)
    : IRequest<Result<MailboxRefreshJobStatusDto>>;

/// <summary>
/// 票 296：状态查询。运行中进度来自 DB 实时增量（导入记录 ImportedAt / 待确认 CreatedAt ≥ 任务开始；
/// 回填进行时附断点行位置），不新增持久化；终态计数取任务快照的 handler 结果。
/// 注意：待确认确认 mid-run 也会写导入记录，运行中 added 增量可能多计 1（进度展示级误差，终态为准）。
/// 票 306：按作用域查槽（条目页传 entryId、设置页不传）；条目槽的运行中计数只算该条目
/// （导入记录按 EntryId、待确认按 EntryId；歧义行 EntryId 为空，只计入全局槽）；
/// 扫描进度（Scanned/Checked/Phase）与心跳取任务快照；Stale = Running 且心跳超过 StaleAfter 未更新。
/// </summary>
public sealed class GetMailboxRefreshStatusQueryHandler(
    MailboxRefreshJobTracker tracker,
    InterviewsDbContext db)
    : IRequestHandler<GetMailboxRefreshStatusQuery, Result<MailboxRefreshJobStatusDto>>
{
    public async Task<Result<MailboxRefreshJobStatusDto>> Handle(
        GetMailboxRefreshStatusQuery request, CancellationToken ct)
    {
        var job = tracker.TryGet(request.UserId, request.EntryId);
        if (job is null)
            return Result.Success(new MailboxRefreshJobStatusDto("None", null, null, null,
                0, 0, 0, 0, [], false, false, false, false, false, null, null, null,
                0, 0, null, null, false, null));
        if (job.State != MailboxRefreshJobState.Running)
            return Result.Success(new MailboxRefreshJobStatusDto(job.State.ToString(), job.JobId,
                job.StartedAt, job.FinishedAt, job.Added, job.Pending, job.SkippedDuplicates,
                job.Failed, job.FailedReasons, job.Cancelled, job.RateLimited, job.NeedsReconnect,
                job.CursorAdvanced, false, null, null, job.ErrorMessage,
                job.Scanned, job.Checked, job.Phase, job.LastActivityAt, false, job.BackfillPage));

        var conn = await db.MailboxConnections.FirstOrDefaultAsync(
            c => c.UserId == request.UserId && c.Status == MailboxConnectionStatus.Active, ct);
        var added = 0;
        var pending = 0;
        var backfillActive = false;
        int? pageIndex = null;
        int? pageSize = null;
        if (conn is not null)
        {
            var recordsQuery = db.MailboxImportRecords.Where(r =>
                r.UserId == request.UserId && r.Provider == conn.Provider
                && r.AccountEmail == conn.AccountEmail && r.ImportedAt >= job.StartedAt);
            if (job.EntryId is { } scopedEntryId)
                recordsQuery = recordsQuery.Where(r => r.EntryId == scopedEntryId);
            added = await recordsQuery.CountAsync(ct);
            var pendingQuery = db.MailboxPendingItems.Where(p =>
                p.UserId == request.UserId && p.Provider == conn.Provider
                && p.AccountEmail == conn.AccountEmail && p.CreatedAt >= job.StartedAt
                && p.Status == MailboxPendingStatus.Pending);
            if (job.EntryId is { } scopedPendingEntryId)
                pendingQuery = pendingQuery.Where(p => p.EntryId == scopedPendingEntryId);
            pending = await pendingQuery.CountAsync(ct);
            var progress = await db.MailboxBackfillProgress.FirstOrDefaultAsync(p =>
                p.UserId == request.UserId && p.Provider == conn.Provider
                && p.AccountEmail == conn.AccountEmail, ct);
            if (progress is not null)
            {
                backfillActive = true;
                pageIndex = progress.CurrentPageIndex;
                try
                {
                    pageSize = JsonSerializer.Deserialize<List<string>>(
                        progress.CurrentPageIdsJson)?.Count;
                }
                catch { pageSize = null; }
            }
        }
        var stale = DateTimeOffset.UtcNow - job.LastActivityAt > tracker.StaleAfter;
        return Result.Success(new MailboxRefreshJobStatusDto("Running", job.JobId,
            job.StartedAt, null, added, pending, 0, 0, [], false, false, false, false,
            backfillActive, pageIndex, pageSize, null,
            job.Scanned, job.Checked, job.Phase, job.LastActivityAt, stale, job.BackfillPage));
    }
}
