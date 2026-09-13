using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.Services.Jobs.Domain;

/// <summary>
/// 求职跟踪 —— 公司聚合根。
/// 模仿 Simplify.jobs 的数据模型:公司是独立实体,可挂多个岗位。
/// </summary>
public sealed class Company : AuditableAggregateRoot
{
    private Company() { }

    public Company(string name, string? website = null, string? industry = null,
        string? location = null, string? logoUrl = null, string? notes = null)
    {
        Name = name;
        Website = website;
        Industry = industry;
        Location = location;
        LogoUrl = logoUrl;
        Notes = notes;
        IsBlacklisted = false;
    }

    public string Name { get; private set; } = string.Empty;
    public string? Website { get; private set; }
    public string? Industry { get; private set; }
    public string? Location { get; private set; }
    public string? LogoUrl { get; private set; }
    public string? Notes { get; private set; }
    /// <summary>拉黑列表:外包/骚扰型猎头可标记,前端过滤。</summary>
    public bool IsBlacklisted { get; private set; }
    public int? EmployeeCount { get; private set; }
    /// <summary>公司类型:DirectEmployer / Agency / Staffing / Unknown</summary>
    public string CompanyType { get; private set; } = "Unknown";

    public void Update(string name, string? website, string? industry, string? location,
        string? logoUrl, string? notes, string companyType, int? employeeCount)
    {
        Name = name;
        Website = website;
        Industry = industry;
        Location = location;
        LogoUrl = logoUrl;
        Notes = notes;
        CompanyType = companyType;
        EmployeeCount = employeeCount;
        Touch();
    }

    public void SetBlacklisted(bool value)
    {
        IsBlacklisted = value;
        Touch();
    }
}

/// <summary>
/// 投递记录(岗位级)。状态机是核心:
/// saved → applied → screen → interview → offer
///                          ↘ rejected / paused / ghosted
/// </summary>
public sealed class JobApplication : AuditableAggregateRoot
{
    private readonly List<ApplicationStatusChange> _history = new();
    private readonly List<InterviewRound> _rounds = new();

    private JobApplication() { }

    public JobApplication(Guid companyId, string role, string? location = null, string? link = null,
        string? salary = null, string? workMode = null, string? source = null, string? jdSummary = null)
    {
        CompanyId = companyId;
        Role = role;
        Location = location;
        Link = link;
        Salary = salary;
        WorkMode = workMode;
        Source = source;
        JdSummary = jdSummary;
        Status = ApplicationStatus.Saved;
        Priority = Priority.Medium;
        RecordStatusChange(ApplicationStatus.Saved, "创建记录");
    }

    public Guid CompanyId { get; private set; }
    public string Role { get; private set; } = string.Empty;
    public string? Location { get; private set; }
    public string? Link { get; private set; }
    public string? Salary { get; private set; }
    public string? WorkMode { get; private set; }
    public string? Source { get; private set; }
    public string? JdSummary { get; private set; }

    public ApplicationStatus Status { get; private set; }
    public Priority Priority { get; private set; }
    public DateOnly? AppliedDate { get; private set; }
    public DateOnly? Deadline { get; private set; }
    public DateTimeOffset? NextFollowUpAt { get; private set; }
    public int? ResumeScore { get; private set; }
    public string? PassRateEstimate { get; private set; }
    public string? MatchKeywords { get; private set; }
    public string? Notes { get; private set; }
    public string? PosterName { get; private set; }
    public bool NeedsConnectFirst { get; private set; }
    public string? OutreachMessage { get; private set; }
    public DateTimeOffset? OutreachSentAt { get; private set; }
    public string? RejectionReason { get; private set; }

    public IReadOnlyCollection<ApplicationStatusChange> History => _history.AsReadOnly();
    public IReadOnlyCollection<InterviewRound> Rounds => _rounds.AsReadOnly();

    // ---------- 状态机(合法流转集中在这里,杜绝"乱改状态") ----------

    private static readonly Dictionary<ApplicationStatus, ApplicationStatus[]> AllowedTransitions = new()
    {
        [ApplicationStatus.Saved] = [ApplicationStatus.Applied, ApplicationStatus.Paused, ApplicationStatus.Rejected],
        [ApplicationStatus.Applied] = [ApplicationStatus.Screen, ApplicationStatus.Interview, ApplicationStatus.Rejected, ApplicationStatus.Ghosted, ApplicationStatus.Paused],
        [ApplicationStatus.Screen] = [ApplicationStatus.Interview, ApplicationStatus.Rejected, ApplicationStatus.Ghosted, ApplicationStatus.Paused],
        [ApplicationStatus.Interview] = [ApplicationStatus.Offer, ApplicationStatus.Rejected, ApplicationStatus.Ghosted, ApplicationStatus.Paused],
        [ApplicationStatus.Offer] = [ApplicationStatus.Accepted, ApplicationStatus.Rejected, ApplicationStatus.Paused],
        [ApplicationStatus.Accepted] = [],
        [ApplicationStatus.Rejected] = [ApplicationStatus.Saved],
        [ApplicationStatus.Ghosted] = [ApplicationStatus.Saved],
        [ApplicationStatus.Paused] = [ApplicationStatus.Saved, ApplicationStatus.Applied]
    };

    public bool CanTransitionTo(ApplicationStatus target)
        => AllowedTransitions.TryGetValue(Status, out var allowed) && allowed.Contains(target);

    public void ChangeStatus(ApplicationStatus target, string? note = null, string? rejectionReason = null)
    {
        if (target == Status) return;
        if (!CanTransitionTo(target))
            throw new InvalidOperationException($"不允许从 {Status} 直接流转到 {target}");
        RecordStatusChange(target, note);
        Status = target;

        if (target == ApplicationStatus.Applied && AppliedDate is null)
            AppliedDate = DateOnly.FromDateTime(DateTime.UtcNow);
        if (target is ApplicationStatus.Rejected or ApplicationStatus.Ghosted)
            RejectionReason = rejectionReason;

        RaiseDomainEvent(new JobApplicationStatusChangedDomainEvent(Id, CompanyId, target.ToString()));
        Touch();
    }

    private void RecordStatusChange(ApplicationStatus status, string? note)
        => _history.Add(new ApplicationStatusChange(Id, status, note));

    // ---------- 其它行为 ----------

    public void MarkApplied(DateOnly? date = null, string? note = null)
    {
        AppliedDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        ChangeStatus(ApplicationStatus.Applied, note ?? "已投递");
    }

    public void UpdateDetails(string role, string? location, string? link, string? salary,
        string? workMode, string? source, string? jdSummary, string? notes)
    {
        Role = role;
        Location = location;
        Link = link;
        Salary = salary;
        WorkMode = workMode;
        Source = source;
        JdSummary = jdSummary;
        Notes = notes;
        Touch();
    }

    public void SetAnalysis(int? resumeScore, string? passRate, string? matchKeywords)
    {
        ResumeScore = resumeScore;
        PassRateEstimate = passRate;
        MatchKeywords = matchKeywords;
        Touch();
    }

    public void SetPriority(Priority priority) { Priority = priority; Touch(); }

    public void SetDeadline(DateOnly? deadline) { Deadline = deadline; Touch(); }

    public void ScheduleFollowUp(DateTimeOffset? at) { NextFollowUpAt = at; Touch(); }

    public void SetOutreach(string? posterName, bool needsConnect, string? message)
    {
        PosterName = posterName;
        NeedsConnectFirst = needsConnect;
        OutreachMessage = message;
        Touch();
    }

    public void MarkOutreachSent() { OutreachSentAt = DateTimeOffset.UtcNow; Touch(); }

    public InterviewRound AddRound(string stage, DateOnly? scheduledDate, string? interviewer,
        string? format, string? notes)
    {
        var round = new InterviewRound(Id, _rounds.Count + 1, stage, scheduledDate, interviewer, format, notes);
        _rounds.Add(round);
        if (Status is ApplicationStatus.Applied or ApplicationStatus.Screen)
            ChangeStatus(ApplicationStatus.Interview, $"新增面试轮次:{stage}");
        else
            Touch();
        return round;
    }

    public void UpdateRound(Guid roundId, string? stage, DateOnly? date, string? interviewer,
        string? format, RoundOutcome outcome, string? notes)
    {
        var round = _rounds.FirstOrDefault(r => r.Id == roundId)
            ?? throw new KeyNotFoundException("面试轮次不存在");
        round.Update(stage, date, interviewer, format, outcome, notes);
        Touch();
    }
}

public sealed class ApplicationStatusChange : Entity
{
    private ApplicationStatusChange() { }
    internal ApplicationStatusChange(Guid applicationId, ApplicationStatus status, string? note)
    {
        ApplicationId = applicationId;
        Status = status;
        Note = note;
        ChangedAt = DateTimeOffset.UtcNow;
    }

    public Guid ApplicationId { get; private set; }
    public ApplicationStatus Status { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset ChangedAt { get; private set; }
}

public sealed class InterviewRound : Entity
{
    private InterviewRound() { }
    internal InterviewRound(Guid applicationId, int order, string stage, DateOnly? scheduledDate,
        string? interviewer, string? format, string? notes)
    {
        ApplicationId = applicationId;
        Order = order;
        Stage = stage;
        ScheduledDate = scheduledDate;
        Interviewer = interviewer;
        Format = format;
        Notes = notes;
        Outcome = RoundOutcome.Pending;
    }

    public Guid ApplicationId { get; private set; }
    public int Order { get; private set; }
    public string Stage { get; private set; } = string.Empty;   // Screen / Technical / SystemDesign / Behavioral / Final
    public DateOnly? ScheduledDate { get; private set; }
    public string? Interviewer { get; private set; }
    public string? Format { get; private set; }                 // Phone / Video / Onsite
    public string? Notes { get; private set; }
    public RoundOutcome Outcome { get; private set; }
    public string? Feedback { get; private set; }

    internal void Update(string? stage, DateOnly? date, string? interviewer, string? format,
        RoundOutcome outcome, string? notes)
    {
        if (!string.IsNullOrWhiteSpace(stage)) Stage = stage;
        ScheduledDate = date;
        Interviewer = interviewer;
        Format = format;
        Outcome = outcome;
        Notes = notes;
    }

    public void SetFeedback(string? feedback) => Feedback = feedback;
}

public enum ApplicationStatus
{
    Saved = 0,
    Applied = 1,
    Screen = 2,
    Interview = 3,
    Offer = 4,
    Accepted = 5,
    Rejected = 6,
    Ghosted = 7,
    Paused = 8
}

public enum Priority { Low = 0, Medium = 1, High = 2, Critical = 3 }

public enum RoundOutcome { Pending = 0, Passed = 1, Failed = 2, Cancelled = 3, NoShow = 4 }

public sealed record JobApplicationStatusChangedDomainEvent(Guid ApplicationId, Guid CompanyId, string ToStatus)
    : DomainEventBase;
