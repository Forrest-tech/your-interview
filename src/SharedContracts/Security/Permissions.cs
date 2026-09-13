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

    public static readonly string[] All = [Admin, PowerUser, User, Viewer];

    public static readonly Dictionary<string, string[]> DefaultPermissions = new()
    {
        [Admin] = Permissions.All,
        [PowerUser] =
        [
            Permissions.JobsRead, Permissions.JobsWrite, Permissions.JobsDelete,
            Permissions.InterviewsRead, Permissions.InterviewsWrite, Permissions.InterviewsAnalyze, Permissions.InterviewsDelete,
            Permissions.KnowledgeRead, Permissions.KnowledgeWrite, Permissions.KnowledgeDelete,
            Permissions.MockRead, Permissions.MockAnswer, Permissions.MockManage,
            Permissions.AdminAuditRead
        ],
        [User] =
        [
            Permissions.JobsRead, Permissions.JobsWrite,
            Permissions.InterviewsRead, Permissions.InterviewsWrite, Permissions.InterviewsAnalyze,
            Permissions.KnowledgeRead, Permissions.KnowledgeWrite,
            Permissions.MockRead, Permissions.MockAnswer
        ],
        [Viewer] =
        [
            Permissions.JobsRead, Permissions.InterviewsRead,
            Permissions.KnowledgeRead, Permissions.MockRead
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
