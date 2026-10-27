using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace YourInterview.Services.Identity.Application.Logto;

/// <summary>票248:驳回通知发信口(审批门依赖抽象,测试用 fake 替代)。</summary>
public interface IRejectionNotifier
{
    Task SendRejectionAsync(string recipientEmail, CancellationToken ct);
}

/// <summary>
/// 票248:应用侧最小 SMTP 发信器(此前应用侧零发信能力)。
/// 用 BCL System.Net.Mail,不引 MailKit —— 本地 NuGet 缓存无 MailKit
/// (见 Interviews MailboxModels 注释),零新增包依赖。
/// 文案:中性 outcome-only —— 只说申请未获通过,不披露内部原因,含重申 statement。
/// 未配 SMTP(Host 或 From 为空)时为停用态:记日志后直接返回,不抛异常
/// (删号流程绝不能因发信配置缺失而失败);真正的发送失败以异常上抛,
/// 由调用方按“不阻塞删除回执”语义记日志 + 审计。
/// </summary>
public sealed class SmtpRejectionNotifier(
    IOptions<RejectionEmailOptions> options,
    ILogger<SmtpRejectionNotifier> logger) : IRejectionNotifier
{
    public const string Subject = "Your YourInterview account application";

    /// <summary>中性 outcome-only 模板:终局 + 重申,不含内部原因。</summary>
    public static string BuildBody() =>
        "Hello,\n\n" +
        "Thank you for applying for a YourInterview account.\n\n" +
        "We are writing to let you know that your application was not approved at this time.\n\n" +
        "You are welcome to apply again in the future.\n\n" +
        "— The YourInterview team";

    public async Task SendRejectionAsync(string recipientEmail, CancellationToken ct)
    {
        var cfg = options.Value;
        if (!cfg.Configured)
        {
            logger.LogWarning("驳回邮件未发送:RejectionEmail SMTP 未配置(Host 或 From 为空)");
            return;
        }
        using var message = new MailMessage
        {
            From = new MailAddress(cfg.FromAddress, cfg.FromName),
            Subject = Subject,
            Body = BuildBody(),
            IsBodyHtml = false
        };
        message.To.Add(recipientEmail);
        using var client = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort)
        {
            EnableSsl = cfg.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        if (!string.IsNullOrWhiteSpace(cfg.Username))
            client.Credentials = new NetworkCredential(cfg.Username, cfg.Password);
        await client.SendMailAsync(message, ct);
    }
}
