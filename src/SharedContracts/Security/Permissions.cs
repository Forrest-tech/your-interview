namespace YourInterview.SharedContracts.Security;

/// <summary>
/// 权限点常量。RBAC 的"权限"粒度:角色 → 权限集合 → 校验点。
/// 命名 Resource.Action,与前端菜单渲染共用(前端拿 permissions[] 决定菜单可见性)。
/// </summary>
public static class Permissions
{
    // 求职跟踪
    public const string JobsRead = "jobs.read";
    public const string JobsWrite = "jobs.write";
    public const string JobsDelete = "jobs.delete";

    // 实战机经
    public const string InterviewsRead = "interviews.read";
    public const string InterviewsWrite = "interviews.write";
    public const string InterviewsAnalyze = "interviews.analyze";
    public const string InterviewsDelete = "interviews.delete";

    // 技术栈知识库
    public const string KnowledgeRead = "knowledge.read";
    public const string KnowledgeWrite = "knowledge.write";
    public const string KnowledgeDelete = "knowledge.delete";

    // AI 实战模拟
    public const string MockRead = "mock.read";
    public const string MockAnswer = "mock.answer";
    public const string MockManage = "mock.manage";

    // 数据分析(Analytics 读模型:仪表盘 / 雷达图 / 漏斗趋势)
    public const string AnalyticsRead = "analytics.read";
    public const string AnalyticsWrite = "analytics.write";
    // 服务回写专用(票56 解法A):仅 Service 角色持有。Analytics ability 端点以此 claim
    // 判定 ?ownerUserId= 是否 honoured —— 普通人类角色(含 PowerUser)不持有,带了也忽略。
    public const string AnalyticsWriteback = "analytics.writeback";

    // 管理面
    public const string AdminUsersRead = "admin.users.read";
    public const string AdminUsersWrite = "admin.users.write";
    public const string AdminRolesWrite = "admin.roles.write";
    public const string AdminContentModerate = "admin.content.moderate";
    public const string AdminAuditRead = "admin.audit.read";
    public const string AdminSystemWrite = "admin.system.write";

    public static readonly string[] All =
    [
        JobsRead, JobsWrite, JobsDelete,
        InterviewsRead, InterviewsWrite, InterviewsAnalyze, InterviewsDelete,
        KnowledgeRead, KnowledgeWrite, KnowledgeDelete,
        MockRead, MockAnswer, MockManage,
        AnalyticsRead, AnalyticsWrite, AnalyticsWriteback,
        AdminUsersRead, AdminUsersWrite, AdminRolesWrite,
        AdminContentModerate, AdminAuditRead, AdminSystemWrite
    ];
}

/// <summary>系统内置角色。</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string PowerUser = "PowerUser";
    public const string User = "User";
    public const string Viewer = "Viewer";
    /// <summary>服务账号专用角色(票56):只给 Analysis.Worker 回写链路的最小权限集,人类账号不授。</summary>
    public const string Service = "Service";

    public static readonly string[] All = [Admin, PowerUser, User, Viewer, Service];

    public static readonly Dictionary<string, string[]> DefaultPermissions = new()
    {
        [Admin] = Permissions.All,
        [PowerUser] =
        [
            Permissions.JobsRead, Permissions.JobsWrite, Permissions.JobsDelete,
            Permissions.InterviewsRead, Permissions.InterviewsWrite, Permissions.InterviewsAnalyze, Permissions.InterviewsDelete,
            Permissions.KnowledgeRead, Permissions.KnowledgeWrite, Permissions.KnowledgeDelete,
            Permissions.MockRead, Permissions.MockAnswer, Permissions.MockManage,
            Permissions.AnalyticsRead, Permissions.AnalyticsWrite,
            Permissions.AdminAuditRead
        ],
        [User] =
        [
            Permissions.JobsRead, Permissions.JobsWrite,
            Permissions.InterviewsRead, Permissions.InterviewsWrite, Permissions.InterviewsAnalyze,
            Permissions.KnowledgeRead, Permissions.KnowledgeWrite,
            Permissions.MockRead, Permissions.MockAnswer,
            Permissions.AnalyticsRead
        ],
        [Viewer] =
        [
            Permissions.JobsRead, Permissions.InterviewsRead,
            Permissions.KnowledgeRead, Permissions.MockRead, Permissions.AnalyticsRead
        ],
        // 票56:Worker 回写最小集 —— Interviews 读(取条目/转写稿)+写(存转写稿/分析结果/失败状态)
        // +分析(转写/分析状态流转端点)+ Analytics 写(ability 端点现行 policy)+ writeback(归属 honoured 门)。
        // 不给 delete、不给 analytics.read(Worker 不读 Analytics)、不给任何 admin.* 与其他模块权限。
        [Service] =
        [
            Permissions.InterviewsRead, Permissions.InterviewsWrite, Permissions.InterviewsAnalyze,
            Permissions.AnalyticsWrite, Permissions.AnalyticsWriteback
        ]
    };
}

/// <summary>JWT 自定义声明名。</summary>
public static class CustomClaims
{
    public const string Permission = "perm";
    public const string SecurityStamp = "sst";
    public const string FullName = "name";
    public const string Avatar = "avatar";
}
