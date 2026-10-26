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
        string? location = null, string? logoUrl = null, string? notes = null,
        Guid userId = default)
    {
        UserId = userId;
        Name = name;
        Website = website;
        Industry = industry;
        Location = location;
        LogoUrl = logoUrl;
        Notes = notes;
        IsBlacklisted = false;
    }

    /// <summary>归属用户(COD-01/票03):公司按用户隔离,同名公司在不同用户下各自一份。</summary>
    public Guid UserId { get; private set; }

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

    /// <summary>
    /// 票 150:只改 logo 地址(回填/单家刷新/SmartAdd 补填共用)。
    /// 与 Update 分开:回填不该要求把公司基本资料全量重传。
    /// </summary>
    public void SetLogoUrl(string? logoUrl)
    {
        LogoUrl = logoUrl;
        Touch();
    }

    /// <summary>
    /// 按公司官网解析一个 Logo 地址(确定性,不做网络请求)。
    /// 用 Google 的 favicon 服务:输入一个能解析出 host 的网址即返回该 host 的图标,
    /// 浏览器侧再叠加"加载失败则降级为首字母头像"的兜底。
    /// 返回 null = 没有可依据的网址,交给前端用首字母头像。
    /// </summary>
    public static string? LogoUrlFromWebsite(string? website)
    {
        if (string.IsNullOrWhiteSpace(website)) return null;
        if (!Uri.TryCreate(website, UriKind.Absolute, out var u)) return null;
        var host = u.Host;
        if (string.IsNullOrWhiteSpace(host)) return null;
        return $"https://www.google.com/s2/favicons?sz=128&domain={host}";
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
        string? salary = null, string? workMode = null, string? source = null, string? jdSummary = null,
        Guid userId = default)
    {
        UserId = userId;
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

    /// <summary>归属用户(COD-01/票03):投递按用户隔离。</summary>
    public Guid UserId { get; private set; }

    public Guid CompanyId { get; private set; }
    public string Role { get; private set; } = string.Empty;
    /** ★ 归档:Simplify 对等功能,归档的不删,只是隐藏。 */
    public bool IsArchived { get; private set; }

    public void Archive() { IsArchived = true; Touch(); }
    public void Unarchive() { IsArchived = false; Touch(); }
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

    /// <summary>
    /// 公司介绍(完整文本,不截断)。Smart paste 从 "About the company" 段提取,
    /// 与 JdText 并存:公司介绍是公司级信息,JD 全文是岗位级信息。
    /// </summary>
    public string? CompanyDescription { get; private set; }

    /// <summary>
    /// 工作身份要求(归一短文本,如 "Citizen or PR required" / "Open work permit accepted")。
    /// Smart paste 从 JD 正文提取 —— 持工签求职者的关键过滤项。
    /// </summary>
    public string? WorkAuthorization { get; private set; }

    /// <summary>
    /// 岗位性质(归一短文本:Full-time / Part-time / Permanent / Contract / Temporary / Freelance / Internship)。
    /// Smart paste 从 JD 正文提取。
    /// </summary>
    public string? JobType { get; private set; }

    /// <summary>
    /// 任职要求(完整文本)。Smart paste 从 Qualifications/Requirements 段提取。
    /// </summary>
    public string? Requirements { get; private set; }

    /// <summary>
    /// 福利待遇(完整文本)。Smart paste 从 Benefits/Advantages 段提取。
    /// </summary>
    public string? Benefits { get; private set; }

    /// <summary>
    /// 票 154：JD 正文小节列表 JSON（[{title, key, body, origin, edited}]，原文顺序）。
    /// 真相是 JdText + 本列表；requirements / benefits 是从本列表派生的兼容投影。
    /// null = 老记录（未产出小节），显示走 legacy 路径；不批量回填。
    /// </summary>
    public string? SectionsJson { get; private set; }

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
        [ApplicationStatus.Saved] = [ApplicationStatus.Applied, ApplicationStatus.Paused, ApplicationStatus.Rejected, ApplicationStatus.Withdrawn],
        [ApplicationStatus.Applied] = [ApplicationStatus.Screen, ApplicationStatus.Interview, ApplicationStatus.Rejected, ApplicationStatus.Ghosted, ApplicationStatus.Paused, ApplicationStatus.Withdrawn],
        [ApplicationStatus.Screen] = [ApplicationStatus.Interview, ApplicationStatus.Rejected, ApplicationStatus.Ghosted, ApplicationStatus.Paused, ApplicationStatus.Withdrawn],
        [ApplicationStatus.Interview] = [ApplicationStatus.Offer, ApplicationStatus.Rejected, ApplicationStatus.Ghosted, ApplicationStatus.Paused, ApplicationStatus.Withdrawn],
        [ApplicationStatus.Offer] = [ApplicationStatus.Accepted, ApplicationStatus.Rejected, ApplicationStatus.Paused],
        [ApplicationStatus.Accepted] = [],
        [ApplicationStatus.Rejected] = [ApplicationStatus.Saved],
        [ApplicationStatus.Ghosted] = [ApplicationStatus.Saved],
        [ApplicationStatus.Paused] = [ApplicationStatus.Saved, ApplicationStatus.Applied, ApplicationStatus.Withdrawn],
        [ApplicationStatus.Withdrawn] = [ApplicationStatus.Saved]
    };

    public bool CanTransitionTo(ApplicationStatus target)
        => AllowedTransitions.TryGetValue(Status, out var allowed) && allowed.Contains(target);

    /// <summary>
    /// ★ F-09 修复:导入/建单指定初始状态时,沿状态机最短路径逐级流转到目标状态。
    /// 旧逻辑只认"一步可达",Saved→Interview 这种跨级请求被静默丢弃 ——
    /// 结果:数据明明已在面试,Tracker 却停在 Saved、统计 Interviews=0,自相矛盾。
    /// 规则:初始状态必须被如实落库;沿途每一级都是合法流转(事件照常发),
    /// 真正无路可达(如 Accepted→Saved)才返回 false 并保持原状态。
    /// </summary>
    public bool TryWalkToStatus(ApplicationStatus target, string? note = null)
    {
        if (target == Status) return true;

        // BFS 找最短路径
        var prev = new Dictionary<ApplicationStatus, ApplicationStatus> { [Status] = Status };
        var queue = new Queue<ApplicationStatus>();
        queue.Enqueue(Status);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!AllowedTransitions.TryGetValue(cur, out var nexts)) continue;
            foreach (var n in nexts)
            {
                if (prev.ContainsKey(n)) continue;
                prev[n] = cur;
                if (n == target) { queue.Clear(); break; }
                queue.Enqueue(n);
            }
        }
        if (!prev.ContainsKey(target)) return false;

        var path = new List<ApplicationStatus>();
        for (var s = target; s != Status; s = prev[s]) path.Add(s);
        path.Reverse();
        foreach (var step in path) ChangeStatus(step, note);
        return Status == target;
    }

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
            Id, CompanyId, from.ToString(), target.ToString(), UserId));

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

    public void SetAppliedDate(DateOnly? date) => AppliedDate = date;

    public void UpdateDetails(string role, string? location, string? link, string? salary,
        string? workMode, string? source, string? jdSummary, string? notes,
        string? companyDescription = null, string? workAuthorization = null, string? jobType = null,
        string? requirements = null, string? benefits = null)
    {
        Role = role;
        Location = location;
        Link = link;
        Salary = salary;
        WorkMode = workMode;
        Source = source;
        JdSummary = jdSummary;
        Notes = notes;
        if (companyDescription is not null)
            CompanyDescription = string.IsNullOrWhiteSpace(companyDescription) ? null : companyDescription;
        if (workAuthorization is not null)
            WorkAuthorization = string.IsNullOrWhiteSpace(workAuthorization) ? null : workAuthorization;
        if (jobType is not null)
            JobType = string.IsNullOrWhiteSpace(jobType) ? null : jobType;
        if (requirements is not null)
            Requirements = string.IsNullOrWhiteSpace(requirements) ? null : requirements;
        if (benefits is not null)
            Benefits = string.IsNullOrWhiteSpace(benefits) ? null : benefits;
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

    /// <summary>
    /// 设置备注。独立方法,理由同 SetJdContent:手动建单时备注随单落库,
    /// 不该逼迫调用方走整份 UpdateDetails。空串归一为 null。
    /// </summary>
    public void SetNotes(string? notes)
    {
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes;
        Touch();
    }

    /// <summary>
    /// 设置公司介绍。独立方法:Smart paste 抓到公司介绍是高频动作,
    /// 不该逼迫调用方传齐所有投递字段。空串归一为 null。
    /// </summary>
    public void SetCompanyDescription(string? companyDescription)
    {
        CompanyDescription = string.IsNullOrWhiteSpace(companyDescription) ? null : companyDescription;
        Touch();
    }

    /// <summary>
    /// 设置工作身份要求。独立方法,理由同 SetCompanyDescription。空串归一为 null。
    /// </summary>
    public void SetWorkAuthorization(string? workAuthorization)
    {
        WorkAuthorization = string.IsNullOrWhiteSpace(workAuthorization) ? null : workAuthorization;
        Touch();
    }

    /// <summary>
    /// 设置岗位性质。独立方法,理由同 SetCompanyDescription。空串归一为 null。
    /// </summary>
    public void SetJobType(string? jobType)
    {
        JobType = string.IsNullOrWhiteSpace(jobType) ? null : jobType;
        Touch();
    }

    /// <summary>
    /// 设置任职要求。独立方法,理由同 SetCompanyDescription。空串归一为 null。
    /// </summary>
    public void SetRequirements(string? requirements)
    {
        Requirements = string.IsNullOrWhiteSpace(requirements) ? null : requirements;
        Touch();
    }

    /// <summary>
    /// 设置福利待遇。独立方法,理由同 SetCompanyDescription。空串归一为 null。
    /// </summary>
    public void SetBenefits(string? benefits)
    {
        Benefits = string.IsNullOrWhiteSpace(benefits) ? null : benefits;
        Touch();
    }

    /// <summary>
    /// 设置 JD 小节列表 JSON。空串归一为 null（语义 = 无小节，走 legacy 显示）。
    /// 调用方负责先过 JdSectionJson.Canonicalize（去空节、key 重算），本方法只存。
    /// </summary>
    public void SetSections(string? sectionsJson)
    {
        SectionsJson = string.IsNullOrWhiteSpace(sectionsJson) ? null : sectionsJson;
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
        // 票04:结果回写补建的投影轮次号可不连续(如只有第 2 轮),新轮次号必须取 max+1,
        // 用 Count+1 会与已有投影轮次撞号(Interviews 侧 AddRound 同口径)。
        var round = new InterviewRound(Id, _rounds.Count == 0 ? 1 : _rounds.Max(r => r.Order) + 1,
            stage, scheduledDate, interviewer, format, notes);
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
            Id, CompanyId, Role, roundNo, stage, scheduledDate, interviewer, format, UserId));

    public void UpdateRound(Guid roundId, string? stage, DateOnly? date, string? interviewer,
        string? format, RoundOutcome outcome, string? notes)
    {
        var round = _rounds.FirstOrDefault(r => r.Id == roundId)
            ?? throw new KeyNotFoundException("面试轮次不存在");
        round.Update(stage, date, interviewer, format, outcome, notes);
        Touch();
    }

    /// <summary>
    /// 面试结果回写(M1.5;票04 REQ-02+ARC-07 重写语义):把实战机经里登记的结果落到对应轮次。
    /// 轮次按 Order 匹配(M1.4 自动建的草稿 RoundNo = 轮次号,天然对齐)。
    ///
    /// Jobs 侧没有对应轮次时**按轮号补建投影**(轮次主数据在 Interviews,这里只是
    /// Tracker 侧投影):旧实现直接抛 KeyNotFound、消费者吞掉跳过 —— Playbook 已填
    /// 结果,Tracker 却停在 Saved、interviewed=0,同屏矛盾(REQ-02)。补建投影
    /// **不发邀请事件**(RaiseInvitation):这是回写,不是新登记一轮,
    /// 发了会让 Interviews 反向建草稿形成回环。
    ///
    /// 状态语义(与枚举一一对应,不再只处理 Failed@Interview 一种组合):
    ///   Failed   → Saved/Applied/Screen/Interview 一律转 Rejected(用户写下"被拒"就是明确判断);
    ///   Passed   → Saved/Applied/Screen 沿状态机走到 Interview(面过是事实,计数口径同源);
    ///   Ghosted  → Saved/Applied/Screen/Interview 沿状态机走到 Ghosted;
    ///   其余结果(Pending/Cancelled/NoShow)只落投影,不动申请状态;
    ///   Offer/Accepted/Paused/Withdrawn 等用户已推进/已决定的状态不被回写覆盖。
    /// </summary>
    public void RecordRoundOutcome(int roundOrder, RoundOutcome outcome, string? note)
    {
        var round = _rounds.FirstOrDefault(r => r.Order == roundOrder);
        if (round is null)
        {
            round = new InterviewRound(Id, roundOrder, "Technical", null, null, null, note);
            _rounds.Add(round);
        }

        round.RecordOutcome(outcome);

        switch (outcome)
        {
            case RoundOutcome.Failed when Status is ApplicationStatus.Saved
                or ApplicationStatus.Applied or ApplicationStatus.Screen
                or ApplicationStatus.Interview:
                // note 进流转历史,rejectionReason 进拒因字段 —— 两者都要,别只留一半
                ChangeStatus(ApplicationStatus.Rejected, note ?? $"第 {roundOrder} 轮面试未通过",
                    rejectionReason: note ?? $"第 {roundOrder} 轮面试未通过");
                break;
            case RoundOutcome.Passed when Status is ApplicationStatus.Saved
                or ApplicationStatus.Applied or ApplicationStatus.Screen:
                // Saved 不能一步到 Interview:先逐级到 Applied,再进 Interview。
                // 进 Interview 必须走 fromAddRound 静默路径 —— 普通 ChangeStatus 会
                // 顺带发「下一轮邀请」让 Interviews 反向建一张新草稿(回写不成回环)。
                if (Status == ApplicationStatus.Saved)
                    ChangeStatus(ApplicationStatus.Applied, note ?? $"第 {roundOrder} 轮面试已通过");
                ChangeStatusCore(ApplicationStatus.Interview, note ?? $"第 {roundOrder} 轮面试已通过",
                    null, fromAddRound: true);
                break;
            case RoundOutcome.Ghosted when Status is ApplicationStatus.Saved
                or ApplicationStatus.Applied or ApplicationStatus.Screen
                or ApplicationStatus.Interview:
                TryWalkToStatus(ApplicationStatus.Ghosted, note ?? $"第 {roundOrder} 轮后无回复");
                break;
            default:
                Touch();
                break;
        }
    }

    /// <summary>
    /// 轮次排期回写(票114,REQ-02 残余/CONF-DASH-1):Playbook 里排了期,
    /// Tracker 投影轮次同步排期,投递状态按口径推进 —— interviewed 计数
    /// (Jobs 状态口径)与 Dashboard Upcoming(Interviews 排期口径)同屏不矛盾。
    ///
    /// 语义(定版,evidence-114/STATUS §4):
    ///   · 投影同步:按 Order 找轮次,无则补建(带 Stage/日期,**不发邀请事件**防回环,
    ///     与结果回写补建同型);有则同步 Stage(非空才覆盖)与 ScheduledDate。
    ///   · 推进:ScheduledDate 非空、该轮投影 Outcome 仍 Pending、且状态 ∈
    ///     {Saved, Applied, Screen} → 沿状态机走 Interview(与 Passed 回写同路:
    ///     Saved 先 Applied,再 ChangeStatusCore 静默进 Interview)。
    ///   · 不覆盖:状态 ∈ {Interview, Offer, Accepted, Rejected, Ghosted, Paused,
    ///     Withdrawn} 一律不动 —— 已决定状态不因新排期「复活」;结果已先落
    ///     (投影 Outcome 非 Pending)的轮次,迟到的排期事件只同步日期、永不
    ///     推进状态(乱序不回退)。
    ///   · 撤销(ScheduledDate=null:改期清空/删轮次):只清投影日期,**不回退
    ///     状态** —— 状态机无 Interview→Applied 合法路径,「曾进入面试阶段」
    ///     是历史事实。
    ///   · 幂等:同事件重放 → 同值写入、推进分支不重复触发,无重复流转历史。
    /// </summary>
    public void RecordRoundSchedule(int roundOrder, string? stage, DateOnly? scheduledDate)
    {
        var round = _rounds.FirstOrDefault(r => r.Order == roundOrder);
        if (round is null)
        {
            round = new InterviewRound(Id, roundOrder,
                string.IsNullOrWhiteSpace(stage) ? "Technical" : stage.Trim(),
                scheduledDate, null, null, null);
            _rounds.Add(round);
        }
        else
        {
            round.RecordSchedule(stage, scheduledDate);
        }

        if (scheduledDate is not null
            && round.Outcome == RoundOutcome.Pending
            && Status is ApplicationStatus.Saved or ApplicationStatus.Applied
                or ApplicationStatus.Screen)
        {
            var note = $"第 {roundOrder} 轮面试已排期";
            if (Status == ApplicationStatus.Saved)
                ChangeStatus(ApplicationStatus.Applied, note);
            ChangeStatusCore(ApplicationStatus.Interview, note, null, fromAddRound: true);
        }
        else
        {
            Touch();
        }
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

    /// <summary>
    /// 只同步排期、不动结果/其它字段(票114)—— Stage 非空才覆盖(事件 Stage
    /// 为空时不抹掉投影已有阶段),ScheduledDate 无条件同步(null = 排期撤销)。
    /// </summary>
    internal void RecordSchedule(string? stage, DateOnly? scheduledDate)
    {
        if (!string.IsNullOrWhiteSpace(stage)) Stage = stage;
        ScheduledDate = scheduledDate;
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
    Paused = 8,
    /// <summary>主动撤回(已投递后不想继续)。可由 Applied/Screen/Interview/Paused 进入,可重新打开回 Saved。</summary>
    Withdrawn = 9
}

public enum Priority { Low = 0, Medium = 1, High = 2, Critical = 3 }

public enum RoundOutcome { Pending = 0, Passed = 1, Failed = 2, Cancelled = 3, NoShow = 4, Ghosted = 5 }

public sealed record JobApplicationStatusChangedDomainEvent(
    Guid ApplicationId, Guid CompanyId, string FromStatus, string ToStatus,
    Guid UserId = default)
    : DomainEventBase;

/// <summary>
/// 邀请登记(进入面试状态或新增轮次)→ Interviews 自动建 Playbook 草稿(M1.4)。
/// 公司名/JD 摘要聚合不认识 —— 由发布器查库补全后发集成事件。
/// </summary>
public sealed record InterviewInvitationRecordedDomainEvent(
    Guid ApplicationId, Guid CompanyId, string Role, int RoundNo,
    string? Stage, DateOnly? ScheduledDate, string? Interviewer, string? Format,
    Guid UserId = default)
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

/// <summary>
/// 申请问答库 —— 用户个人沉淀的"常见申请问题 → 我的标准回答"。
///
/// 为什么用户级(UserId):这是私人素材,不同用户的答题风格/经历不同,必须按用户隔离。
/// 与 Company/JdText 的单租户空间不同 —— 简历已经是用户级,问答库跟它同口径。
///
/// 用途:填每一家投递的备注时,从库里一键插入现成答案(为什么想来、薪资预期、
/// 可到岗时间等),避免每次重写。
/// </summary>
public sealed class ApplicationAnswerTemplate : AuditableAggregateRoot
{
    private ApplicationAnswerTemplate() { }

    public ApplicationAnswerTemplate(Guid userId, string category, string question, string answer, int sortOrder)
    {
        UserId = userId;
        Category = category;
        Question = question;
        Answer = answer;
        SortOrder = sortOrder;
    }

    /// <summary>一人一套,按 UserId 隔离。</summary>
    public Guid UserId { get; private set; }

    /// <summary>分类(如 动机 / 经历 / 薪资 / 到岗),便于筛选与组织。</summary>
    public string Category { get; private set; } = string.Empty;

    /// <summary>问题(提示词),如"为什么想加入我们?"。</summary>
    public string Question { get; private set; } = string.Empty;

    /// <summary>我的标准回答(可复用正文)。</summary>
    public string Answer { get; private set; } = string.Empty;

    /// <summary>同分类内的排序。</summary>
    public int SortOrder { get; private set; }

    public void Update(string category, string question, string answer, int sortOrder)
    {
        Category = category;
        Question = question;
        Answer = answer;
        SortOrder = sortOrder;
        Touch();
    }
}

/// <summary>
/// 沟通记录 —— 每条投递的对外沟通时间线。
///
/// 记录类型 Type:Email / Call / Interview / Message / Note。
/// 与投递同属单租户空间(挂在 ApplicationId 下),不作用户级隔离 ——
/// 投递本身是共享空间,沟通记录是投递的子对象,跟随投递走。
/// </summary>
public sealed class ApplicationCommunication : AuditableAggregateRoot
{
    private ApplicationCommunication() { }

    public ApplicationCommunication(Guid applicationId, string type, string? subject, string content,
        string? contactName, string? contactEmail, DateTimeOffset occurredAt,
        Guid userId = default)
    {
        UserId = userId;
        ApplicationId = applicationId;
        Type = type;
        Subject = subject;
        Content = content;
        ContactName = contactName;
        ContactEmail = contactEmail;
        OccurredAt = occurredAt;
    }

    public Guid ApplicationId { get; private set; }

    /// <summary>归属用户(COD-01/票03):写入时从所属投递盖章,与投递同主人。</summary>
    public Guid UserId { get; private set; }

    /// <summary>Email / Call / Interview / Message / Note。</summary>
    public string Type { get; private set; } = string.Empty;

    public string? Subject { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public string? ContactName { get; private set; }
    public string? ContactEmail { get; private set; }

    /// <summary>发生时间(用户填写,不一定等于创建时间)。</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>
    /// 元数据 JSON(2026-10-20 Playbook 详情页 v2):统一入口解析字段、AI 摘要、
    /// 提取到的面试安排、关联的 Playbook 材料 id 等。Tracker 旧 UI 不读此字段。
    /// </summary>
    public string? MetaJson { get; private set; }

    /// <summary>安排处理状态(2026-10-20):Pending(未处理)/ Done(已处理)/ Ignored(忽略)。null = 无安排。</summary>
    public string? HandlingStatus { get; private set; }

    public void SetMeta(string? metaJson) { MetaJson = metaJson; Touch(); }

    public void SetHandlingStatus(string? status) { HandlingStatus = status; Touch(); }

    public void Update(string type, string? subject, string content, string? contactName,
        string? contactEmail, DateTimeOffset occurredAt)
    {
        Type = type;
        Subject = subject;
        Content = content;
        ContactName = contactName;
        ContactEmail = contactEmail;
        OccurredAt = occurredAt;
        Touch();
    }
}
