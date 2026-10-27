namespace YourInterview.Services.Interviews.Application;

/// <summary>
/// 票 242：邮箱 DataProtection 密钥目录解析（纯函数，可单测）。
/// 优先级：显式配置 Mailbox:DataProtectionKeysDir &gt; 环境变量 INTERVIEW_STORAGE_DIR &gt; 主机可写默认。
/// 票 240 的旧默认是容器内路径 /app/storage/interviews：CI runner（非 root、无该环境变量）
/// 启动时无条件建目录即抛 UnauthorizedAccessException，WAF 建厂失败。
/// 默认目录取用户级 LocalApplicationData（Linux 上为 ~/.local/share）：主机用户可写、
/// 发布不清理、重启不丢失。绝不能改回 AppContext.BaseDirectory（发布清 bin）
/// 或 Path.GetTempPath()（重启清 /tmp），否则已存 mailbox token 会静默失效。
/// 单服务账号假设：默认目录是按用户隔离的；同一服务若以不同服务账号重启，
/// 各账号会落在不同的密钥环，已存 token 将无法解密——本系统服务只用单一服务账号运行，
/// 部署时不得多账号混跑；显式配置或环境变量路径不受此假设影响。
/// </summary>
public static class MailboxKeysDirectory
{
    public static string Resolve(string? configuredKeysDir, string? storageDirEnv)
    {
        if (!string.IsNullOrWhiteSpace(configuredKeysDir)) return configuredKeysDir;
        var storageDir = string.IsNullOrWhiteSpace(storageDirEnv)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "your-interview", "interviews")
            : storageDirEnv;
        return Path.Combine(storageDir, "mailbox-keys");
    }
}
