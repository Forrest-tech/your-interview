namespace YourInterview.Services.Identity.Application.Logto;

/// <summary>
/// 票248:应用侧驳回邮件发信配置(配置节 RejectionEmail)。
/// 凭据一律来自环境变量注入,代码里只留占位(appsettings.json 不写真实值):
///   RejectionEmail__SmtpHost / __SmtpPort / __EnableSsl / __Username / __Password
///   / __FromAddress / __FromName。
/// 生产 SMTP 跟已定的 Day-0 SMTP 选择(割接时 Gmail)。
/// 未配 Host 或 From = 发信器停用(删号流程照常,仅记日志,绝不阻塞删除回执)。
/// </summary>
public sealed class RejectionEmailOptions
{
    public const string SectionName = "RejectionEmail";

    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "YourInterview";

    public bool Configured =>
        !string.IsNullOrWhiteSpace(SmtpHost)
        && !string.IsNullOrWhiteSpace(FromAddress);
}
