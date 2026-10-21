using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using YourInterview.BuildingBlocks.Hosting;

namespace YourInterview.BuildingBlocks.Tests;

/// <summary>
/// COD-14 / 票02 seam:共享启动迁移 helper 的公开契约 ——
/// 迁移/种子失败必须把原异常抛回宿主(进程在 app.Run() 前中止 = fail-fast),
/// 而不是像旧的各服务 Program.cs 那样 catch 后记一条日志继续带病启动;
/// 成功时则原样放行。日志级别必须是 Error。
/// </summary>
public class StartupMigrationGuardTests
{
    [Fact]
    public async Task MigrationFailure_PropagatesOriginalException()
    {
        var boom = new InvalidOperationException("42P01: relation \"aigateway.ai_settings\" does not exist");
        var logger = new CapturingLogger();

        var act = () => ServiceDefaults.RunStartupMigrationsOrFailAsync(
            () => throw boom, logger, "assessment-api");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
    }

    [Fact]
    public async Task MigrationFailure_LogsAtErrorLevel()
    {
        var logger = new CapturingLogger();

        var act = () => ServiceDefaults.RunStartupMigrationsOrFailAsync(
            () => throw new InvalidOperationException("boom"), logger, "jobs-api");

        await act.Should().ThrowAsync<InvalidOperationException>();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception != null);
    }

    [Fact]
    public async Task MigrationSuccess_CompletesWithoutThrowing()
    {
        var logger = new CapturingLogger();
        var ran = false;

        await ServiceDefaults.RunStartupMigrationsOrFailAsync(
            () => { ran = true; return Task.CompletedTask; }, logger, "jobs-api");

        ran.Should().BeTrue();
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception));
    }
}
