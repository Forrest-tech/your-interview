using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票 242：邮箱 DataProtection 密钥目录解析规则。
/// 票 240 的默认路径是容器内路径 /app/storage/interviews，CI runner（非 root、无该环境变量）
/// 启动时无条件建目录即抛 UnauthorizedAccessException，6 个 RemindersApiTests 在 WAF 建厂时全败。
/// 优先级：显式配置 Mailbox:DataProtectionKeysDir &gt; 环境变量 INTERVIEW_STORAGE_DIR &gt; 主机可写默认。
/// 默认目录必须是主机用户可写且发布/重启不丢失的位置：绝不能落在应用二进制目录（发布清 bin）
/// 或临时目录（重启清 /tmp），否则已存 mailbox token 会静默失效。
/// </summary>
public sealed class MailboxKeysDirectoryTests
{
    [Fact]
    public void Resolve_ExplicitConfigIsUsedVerbatim()
    {
        MailboxKeysDirectory.Resolve("/custom/keys", "/env/storage")
            .Should().Be("/custom/keys");
    }

    [Fact]
    public void Resolve_EnvStorageDirGetsMailboxKeysAppended()
    {
        MailboxKeysDirectory.Resolve(null, "/env/storage")
            .Should().Be(Path.Combine("/env/storage", "mailbox-keys"));
    }

    [Fact]
    public void Resolve_BlankConfigAndEnvFallThroughToDefault()
    {
        MailboxKeysDirectory.Resolve("   ", "  ")
            .Should().Be(MailboxKeysDirectory.Resolve(null, null));
    }

    [Fact]
    public void Resolve_DefaultIsUnderLocalApplicationData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "your-interview", "interviews", "mailbox-keys");
        MailboxKeysDirectory.Resolve(null, null).Should().Be(expected);
    }

    [Fact]
    public void Resolve_DefaultIsNotUnderAppBinaryDir()
    {
        var resolved = MailboxKeysDirectory.Resolve(null, null);
        resolved.Should().NotStartWith(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Resolve_DefaultIsNotUnderTempDir()
    {
        var resolved = MailboxKeysDirectory.Resolve(null, null);
        resolved.Should().NotStartWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
    }
}
