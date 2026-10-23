using FluentAssertions;
using YourInterview.Services.Assessment.Infrastructure.Storage;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票117 B6:LocalStorageRules.TryEnsureWritable 的失败原因串会经
/// GET storage/settings 的 message 字段对外。改前 IOException/通用异常
/// 分支拼接 ex.Message(OS 原文,含文件系统路径/系统错误文本);改后为
/// 固定文案,与同函数 UnauthorizedAccessException 分支同型。
/// 触发手法:对「已存在的普通文件路径」调用 —— Directory.CreateDirectory
/// 必抛 IOException,确定性可复现,不依赖权限环境。
/// </summary>
public sealed class StorageErrorWordingTests
{
    [Fact]
    public void Writable_directory_still_succeeds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "t117-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (ok, error) = LocalStorageRules.TryEnsureWritable(dir);
            ok.Should().BeTrue();
            error.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void File_path_failure_returns_fixed_wording_without_os_detail()
    {
        var file = Path.Combine(Path.GetTempPath(), "t117-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(file, "x");
        try
        {
            var (ok, error) = LocalStorageRules.TryEnsureWritable(file);
            ok.Should().BeFalse();
            error.Should().Be("目录创建/写入失败,请换一个目录或检查权限后重试。");
            // 防外泄锁:不含路径与 OS 原文片段
            error.Should().NotContain(file);
            error.Should().NotContain("already exists");
            error.Should().NotContain("IOException");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
