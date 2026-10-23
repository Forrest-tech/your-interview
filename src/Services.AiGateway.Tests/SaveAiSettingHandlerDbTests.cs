using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using YourInterview.Services.AiGateway.Application;
using YourInterview.Services.AiGateway.Infrastructure.Persistence;
using YourInterview.SharedContracts.Ai;
using Xunit;

namespace YourInterview.Services.AiGateway.Tests;

/// <summary>
/// 票109 定性实证(Handler + 真实 PG,门控/隔离/清理同 Assessment 类别测试先例):
/// SaveAiSetting 的保存前真测用假 ILlmClientFactory 恒成功替代 —— 沙盒里
/// 服务进程出站 HTTP 被 egress 守卫拦截,API 层无法让真测通过,故在此层证明:
///  ① 整值(上限内)命令经 Handler 保存成功且回读一致(合法行为);
///  ② 超限命令若绕过校验器直达 Handler(修复前线上路径),SaveChanges 被
///     PG 的 varchar 硬限拒绝抛 DbUpdateException —— 即线上 500 的来源。
/// 修复后超限在校验器层已被拦(见 SaveAiSettingValidatorTests + 活体 400),
/// 本组 ② 是对「缺口机制」的存证,不代表修复后仍可达。
/// </summary>
public sealed class SaveAiSettingHandlerDbTests : IAsyncLifetime
{
    private const string Cs = "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";
    private bool _available;
    private readonly List<Guid> _users = [];

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        try { _available = await db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available || _users.Count == 0) return;
        await using var db = NewDb();
        var rows = await db.AiSettings.Where(x => _users.Contains(x.UserId)).ToListAsync();
        db.AiSettings.RemoveRange(rows);
        await db.SaveChangesAsync();
    }

    private static AiGatewayDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AiGatewayDbContext>().UseNpgsql(Cs).Options;
        return new AiGatewayDbContext(opts);
    }

    private Guid NewUser() { var u = Guid.NewGuid(); _users.Add(u); return u; }

    private static ILlmClientFactory OkFactory()
    {
        var client = Substitute.For<ILlmClient>();
        client.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse("OK", "stub"));
        var factory = Substitute.For<ILlmClientFactory>();
        factory.Create(Arg.Any<LlmConnection>()).Returns(client);
        return factory;
    }

    private static SaveAiSettingCommand Cmd(Guid user, string model) => new(
        user, LlmProviders.OpenAiCompatible, "sk-test", "http://127.0.0.1:9/v1",
        model, null, null, null);

    [Fact]
    public async Task AtLimitModel_SavesAndReadsBack()
    {
        if (!_available) return;
        var user = NewUser();
        await using var db = NewDb();
        var handler = new SaveAiSettingCommandHandler(db, OkFactory(),
            NullLogger<SaveAiSettingCommandHandler>.Instance);
        var model = new string('m', 200);
        var r = await handler.Handle(Cmd(user, model), default);
        Assert.True(r.IsSuccess, r.Error?.Description);
        await using var db2 = NewDb();
        var saved = await db2.AiSettings.SingleAsync(x => x.UserId == user);
        Assert.Equal(model, saved.Model);
    }

    [Fact]
    public async Task OverLimitModel_DirectToHandler_ThrowsDbUpdateException()
    {
        if (!_available) return;
        var user = NewUser();
        await using var db = NewDb();
        var handler = new SaveAiSettingCommandHandler(db, OkFactory(),
            NullLogger<SaveAiSettingCommandHandler>.Instance);
        await Assert.ThrowsAsync<DbUpdateException>(
            () => handler.Handle(Cmd(user, new string('m', 201)), default));
    }
}
