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
    string? ErrorMessage);

/// <summary>
/// 每用户同时只一个刷新任务（key = userId，不分 entryId 作用域；运行中任务的作用域胜出）。
/// 单例；状态变更全在锁内，CTS 只在 Running 态可取消、终态后才 Dispose，杜绝竞态误触。
/// </summary>
public sealed class MailboxRefreshJobTracker
{
    private sealed class Job
    {
        public Guid JobId { get; } = Guid.NewGuid();
        public required Guid UserId { get; init; }
        public required Guid? EntryId { get; init; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public CancellationTokenSource Cts { get; } = new();
        public MailboxRefreshJobState State { get; set; } = MailboxRefreshJobState.Running;
        public DateTimeOffset? FinishedAt { get; set; }
        public RefreshMailboxResult? FinalResult { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Job> _jobs = [];

    /// <summary>启动新任务；已有运行任务时返回它（不重复取数）。</summary>
    public MailboxRefreshJobSnapshot StartOrAttach(Guid userId, Guid? entryId,
        Func<CancellationToken, Task<Result<RefreshMailboxResult>>> run)
    {
        Job job;
        lock (_gate)
        {
            if (_jobs.TryGetValue(userId, out var existing)
                && existing.State == MailboxRefreshJobState.Running)
                return SnapshotOf(existing);
            job = new Job { UserId = userId, EntryId = entryId };
            _jobs[userId] = job;
        }
        _ = Task.Run(() => RunAsync(job, run));
        return SnapshotOf(job);
    }

    /// <summary>取消运行任务（已落库保留，任务态随后记 Cancelled）；无运行任务时返回 false。</summary>
    public (bool Cancelled, MailboxRefreshJobSnapshot Snapshot) TryCancel(Guid userId)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(userId, out var job)
                || job.State != MailboxRefreshJobState.Running)
                return (false, job is null ? NoneSnapshot(userId) : SnapshotOf(job));
            job.Cts.Cancel();
            return (true, SnapshotOf(job));
        }
    }

    /// <summary>取用户最新任务快照（从未启动过返回 null）。</summary>
    public MailboxRefreshJobSnapshot? TryGet(Guid userId)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(userId, out var job) ? SnapshotOf(job) : null;
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
                r?.NeedsReconnect ?? false, r?.CursorAdvanced ?? false, job.ErrorMessage);
        }
    }

    private static MailboxRefreshJobSnapshot NoneSnapshot(Guid userId) => new(
        Guid.Empty, userId, null, MailboxRefreshJobState.None, DateTimeOffset.UtcNow, null,
        0, 0, 0, 0, [], false, false, false, false, null);
}

/// <summary>票 296：刷新任务状态 DTO（终态字段与 RefreshMailboxResult 同形，前端可直接复用结果处理）。</summary>
public sealed record MailboxRefreshJobStatusDto(
    string State, Guid? JobId, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    int Added, int Pending, int SkippedDuplicates, int Failed,
    IReadOnlyList<string> FailedReasons,
    bool Cancelled, bool RateLimited, bool NeedsReconnect, bool CursorAdvanced,
    bool BackfillActive, int? BackfillPageIndex, int? BackfillPageSize,
    string? Message);

public sealed record GetMailboxRefreshStatusQuery(Guid UserId)
    : IRequest<Result<MailboxRefreshJobStatusDto>>;

/// <summary>
/// 票 296：状态查询。运行中进度来自 DB 实时增量（导入记录 ImportedAt / 待确认 CreatedAt ≥ 任务开始；
/// 回填进行时附断点行位置），不新增持久化；终态计数取任务快照的 handler 结果。
/// 注意：待确认确认 mid-run 也会写导入记录，运行中 added 增量可能多计 1（进度展示级误差，终态为准）。
/// </summary>
public sealed class GetMailboxRefreshStatusQueryHandler(
    MailboxRefreshJobTracker tracker,
    InterviewsDbContext db)
    : IRequestHandler<GetMailboxRefreshStatusQuery, Result<MailboxRefreshJobStatusDto>>
{
    public async Task<Result<MailboxRefreshJobStatusDto>> Handle(
        GetMailboxRefreshStatusQuery request, CancellationToken ct)
    {
        var job = tracker.TryGet(request.UserId);
        if (job is null)
            return Result.Success(new MailboxRefreshJobStatusDto("None", null, null, null,
                0, 0, 0, 0, [], false, false, false, false, false, null, null, null));
        if (job.State != MailboxRefreshJobState.Running)
            return Result.Success(new MailboxRefreshJobStatusDto(job.State.ToString(), job.JobId,
                job.StartedAt, job.FinishedAt, job.Added, job.Pending, job.SkippedDuplicates,
                job.Failed, job.FailedReasons, job.Cancelled, job.RateLimited, job.NeedsReconnect,
                job.CursorAdvanced, false, null, null, job.ErrorMessage));

        var conn = await db.MailboxConnections.FirstOrDefaultAsync(
            c => c.UserId == request.UserId && c.Status == MailboxConnectionStatus.Active, ct);
        var added = 0;
        var pending = 0;
        var backfillActive = false;
        int? pageIndex = null;
        int? pageSize = null;
        if (conn is not null)
        {
            added = await db.MailboxImportRecords.CountAsync(r =>
                r.UserId == request.UserId && r.Provider == conn.Provider
                && r.AccountEmail == conn.AccountEmail && r.ImportedAt >= job.StartedAt, ct);
            pending = await db.MailboxPendingItems.CountAsync(p =>
                p.UserId == request.UserId && p.Provider == conn.Provider
                && p.AccountEmail == conn.AccountEmail && p.CreatedAt >= job.StartedAt
                && p.Status == MailboxPendingStatus.Pending, ct);
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
        return Result.Success(new MailboxRefreshJobStatusDto("Running", job.JobId,
            job.StartedAt, null, added, pending, 0, 0, [], false, false, false, false,
            backfillActive, pageIndex, pageSize, null));
    }
}
