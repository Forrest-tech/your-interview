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

    /// <summary>
    /// 公司情况长文本(规模/主营业务/技术栈/面试风格/文化/近期动态)。
    /// 面试前准备包的输入之一 —— 与 JD 全文配合,让 AI 生成有针对性的预测问题。
    /// 为什么放在 Company 而不是 JobApplication:同一家公司的多个岗位共享同一份公司情报,
    /// 存一份避免重复;JD 才是个岗位独有的。
    /// </summary>
    public string? Profile { get; private set; }

    /// <summary>公司情报的来源 URL(JSON 数组字符串),便于追溯与复核。</summary>
    public string? ProfileSourcesJson { get; private set; }

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

    /// <summary>
    /// 单独更新公司情报。与 Update 分开:编辑基本资料时不该要求把长文本一起传,
    /// 从外部粘贴公司情报时也不该覆盖已经填好的基本资料 —— 两个高频动作互不干扰。
    /// </summary>
    public void UpdateProfile(string? profile, string? profileSourcesJson)
    {
        Profile = profile;
        ProfileSourcesJson = profileSourcesJson;
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

    /// <summary>
    /// JD 全文(逐字)。与 JdSummary 并存而不是取代它:
    ///   全文 = AI 生成预测问题的原始输入,要保留招聘方的原文措辞;
    ///   摘要 = 列表页与速览用,由人提炼。
    /// 两者用途不同,合并会丢信息。
    /// </summary>
    public string? JdText { get; private set; }

    /// <summary>JD 原始链接(可选)。JD 全文可能来自上传文件,留下出处便于复核。</summary>
    public string? JdSourceUrl { get; private set; }

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
        => ChangeStatusCore(target, note, rejectionReason, fromAddRound: false);

    /// <summary>
    /// 状态流转核心。fromAddRound:AddRound 内部带进来的 Interview 状态变更 ——
    /// 那条路的邀请事件由 AddRound 自己发(带着轮次细节),这里不再重复发裸事件。
    /// </summary>
    private void ChangeStatusCore(ApplicationStatus target, string? note, string? rejectionReason,
        bool fromAddRound)
    {
        if (target == Status) return;
        if (!CanTransitionTo(target))
            throw new InvalidOperationException($"不允许从 {Status} 直接流转到 {target}");
        RecordStatusChange(target, note);
        var from = Status;
        Status = target;

        if (target == ApplicationStatus.Applied && AppliedDate is null)
            AppliedDate = DateOnly.FromDateTime(DateTime.UtcNow);
        if (target is ApplicationStatus.Rejected or ApplicationStatus.Ghosted)
            RejectionReason = rejectionReason;

        RaiseDomainEvent(new JobApplicationStatusChangedDomainEvent(
            Id, CompanyId, from.ToString(), target.ToString()));

        // 邀请登记(直接切状态,还没登记具体轮次):视为"下一轮"的邀请。
        // Rounds 里已有 2 轮却直接切 Interview → 第 3 轮草稿。
        if (target == ApplicationStatus.Interview && !fromAddRound)
            RaiseInvitation(_rounds.Count + 1, null, null, null, null);
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

    /// <summary>
    /// 更新 JD 全文与出处。独立方法,理由同 Company.UpdateProfile:
    /// 粘贴 JD 是高频动作,不该逼迫调用方传齐所有投递字段。
    /// 空串一律归一为 null —— 避免"存了个空字符串"和"没存"在查询里表现不同。
    /// </summary>
    public void SetJdContent(string? jdText, string? jdSourceUrl)
    {
        JdText = string.IsNullOrWhiteSpace(jdText) ? null : jdText;
        JdSourceUrl = string.IsNullOrWhiteSpace(jdSourceUrl) ? null : jdSourceUrl;
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
            ChangeStatusCore(ApplicationStatus.Interview, $"新增面试轮次:{stage}", null, fromAddRound: true);
        else
            Touch();

        // 每登记一轮 = 一次"要面试了"—— 带着轮次细节发给 Interviews 建草稿(M1.4)
        RaiseInvitation(round.Order, round.Stage, round.ScheduledDate, round.Interviewer, round.Format);
        return round;
    }

    private void RaiseInvitation(int roundNo, string? stage, DateOnly? scheduledDate,
        string? interviewer, string? format)
        => RaiseDomainEvent(new InterviewInvitationRecordedDomainEvent(
            Id, CompanyId, Role, roundNo, stage, scheduledDate, interviewer, format));

    public void UpdateRound(Guid roundId, string? stage, DateOnly? date, string? interviewer,
        string? format, RoundOutcome outcome, string? notes)
    {
        var round = _rounds.FirstOrDefault(r => r.Id == roundId)
            ?? throw new KeyNotFoundException("面试轮次不存在");
        round.Update(stage, date, interviewer, format, outcome, notes);
        Touch();
    }

    /// <summary>
    /// 面试结果回写(M1.5):把实战机经里登记的结果落到对应轮次。
    /// 轮次按 Order 匹配(M1.4 自动建的草稿 RoundNo = 轮次号,天然对齐)。
    /// 被拒时申请整体转 Rejected —— 用户写下"被拒"就是明确判断,不该再靠手工切状态。
    /// </summary>
    public void RecordRoundOutcome(int roundOrder, RoundOutcome outcome, string? note)
    {
        var round = _rounds.FirstOrDefault(r => r.Order == roundOrder)
            ?? throw new KeyNotFoundException($"第 {roundOrder} 轮不存在,无法回写结果");

        round.RecordOutcome(outcome);

        if (outcome == RoundOutcome.Failed && Status == ApplicationStatus.Interview)
            // note 进流转历史,rejectionReason 进拒因字段 —— 两者都要,别只留一半
            ChangeStatus(ApplicationStatus.Rejected, note ?? $"第 {roundOrder} 轮面试未通过",
                rejectionReason: note ?? $"第 {roundOrder} 轮面试未通过");
        else
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

    /// <summary>
    /// 只记结果、不动其它字段(M1.5)—— Update 会把没传的字段抹成 null,
    /// 结果回写不该清掉用户已填的时间/面试官/形式。
    /// </summary>
    internal void RecordOutcome(RoundOutcome outcome) => Outcome = outcome;

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

public sealed record JobApplicationStatusChangedDomainEvent(
    Guid ApplicationId, Guid CompanyId, string FromStatus, string ToStatus)
    : DomainEventBase;

/// <summary>
/// 邀请登记(进入面试状态或新增轮次)→ Interviews 自动建 Playbook 草稿(M1.4)。
/// 公司名/JD 摘要聚合不认识 —— 由发布器查库补全后发集成事件。
/// </summary>
public sealed record InterviewInvitationRecordedDomainEvent(
    Guid ApplicationId, Guid CompanyId, string Role, int RoundNo,
    string? Stage, DateOnly? ScheduledDate, string? Interviewer, string? Format)
    : DomainEventBase;

/// <summary>
/// 用户简历正文(2026-09-18:简历匹配分析 + 面试前准备包的输入)。
///
/// 为什么放 Jobs 域:
///   简历是求职资产 —— 与 JD/公司/投递同属求职域,主要消费者是简历匹配分析。
///   放在求职域让"简历 vs JD 关键词比对"成为同库同服务的操作,零跨服务调用。
///
/// 为什么是用户级(主键 UserId)而不是挂在某条投递上:
///   简历是长期资产,内容稳定,按岗微调不改主版本。
///   一份简历要跟所有岗位比对,挂在某条投递上会让其他岗位读不到。
///
/// ⚠️ 隐私:简历含姓名/电话/邮箱。本表只服务端存储,
///    任何对外调用(如送给 LLM)前都必须先脱敏。
/// </summary>
public sealed class UserResume : AuditableAggregateRoot
{
    private UserResume() { }

    public UserResume(Guid userId, string content)
    {
        UserId = userId;
        Content = content;
        Version = 1;
    }

    /// <summary>一人一份主版本简历。</summary>
    public Guid UserId { get; private set; }

    /// <summary>
    /// 简历全文。
    /// 用 text 不设长度上限:简历形态差异大(纯文本/Markdown/从 PDF 提的),
    /// 限长了只会造成"保存失败但不知道为什么"。
    /// </summary>
    public string Content { get; private set; } = string.Empty;

    /// <summary>
    /// 版本号 —— 每次覆盖内容递增。
    /// 用途:匹配分析结果可标注"基于简历 v3 计算",
    /// 简历更新后旧的分析结果能被识别为过期,而不是静默失效。
    /// </summary>
    public int Version { get; private set; }

    /// <summary>覆盖简历内容,版本号自增。</summary>
    public void Update(string content)
    {
        Content = content;
        Version++;
    }
}
