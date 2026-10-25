using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Interviews.Api;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using YourInterview.Services.Interviews.Infrastructure.Services;
using YourInterview.SharedContracts.Security;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// CONF-CAL-2 / CAL2-T4:提醒端点 HTTP 宿主级测试(WAF 形态同票115)。
/// 链路:路由 + JWT + 权限政策 + 查询侧对账生成 + ack + 跨用户隔离。
/// 种子走真实聚合(Entry.AddRound/UpdateRound),用户为随机 Guid 的 Owner/Other,
/// 直连库上下文带参数化当前用户。无沙盒 DB 时整组跳过(同仓内先例)。
/// </summary>
public sealed class RemindersApiTests : IAsyncLifetime
{
    private const string Cs = "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=postgres";
    // 票 168:必须与 StartTranscriptionApiTests 的 SigningKey 取同一值(见该文件注释)。
    private const string SigningKey = "cal2-host-test-signing-key-0123456789abcdef";
    private const string Issuer = "your-interview.identity";
    private const string Audience = "your-interview.api";

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private WebApplicationFactory<InterviewsController>? _factory;
    private HttpClient? _client;
    private InterviewsDbContext _db = null!;
    private bool _available;

    public async Task InitializeAsync()
    {
        _db = NewDb(Owner);
        try { _available = await _db.Database.CanConnectAsync(); }
        catch { _available = false; }
        // DB 可达时迁移失败必须让测试红(不吞)。但多测试类并行 Initialize 时
        // 并发 MigrateAsync 会抢同一条 DDL(23505 pg_type / 42P07),故先拿
        // advisory lock 把「检查+迁移」串行化;已迁移时为 no-op。
        if (_available) await MigrateUnderLockAsync();
        if (!_available) return;

        var testBin = Path.GetDirectoryName(typeof(RemindersApiTests).Assembly.Location)!;
        var appName = typeof(InterviewsController).Assembly.GetName().Name;
        var depsTarget = Path.Combine(AppContext.BaseDirectory, $"{appName}.deps.json");
        if (!File.Exists(depsTarget)) File.Copy(Path.Combine(testBin, $"{appName}.deps.json"), depsTarget);

        Environment.SetEnvironmentVariable("Jwt__SigningKey", SigningKey);
        Environment.SetEnvironmentVariable("Messaging__Enabled", "false");
        Environment.SetEnvironmentVariable("ConnectionStrings__InterviewsDb", Cs);

        _factory = new WebApplicationFactory<InterviewsController>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddMediatR(cfg =>
                        cfg.RegisterServicesFromAssembly(typeof(GetRemindersQuery).Assembly));
                    var hosted = services.Where(d =>
                        d.ServiceType == typeof(IHostedService) &&
                        (d.ImplementationType == typeof(AnalysisJobDispatcher) ||
                         d.ImplementationType == typeof(AudioIntegritySweepService))).ToList();
                    foreach (var d in hosted) services.Remove(d);
                });
            });
        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(testBin);
            _client = _factory.CreateClient();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        Environment.SetEnvironmentVariable("Jwt__SigningKey", null);
        Environment.SetEnvironmentVariable("Messaging__Enabled", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__InterviewsDb", null);
        if (_available)
        {
            var rems = await _db.RoundReminders.IgnoreQueryFilters()
                .Where(x => x.UserId == Owner || x.UserId == Other).ToListAsync();
            _db.RoundReminders.RemoveRange(rems);
            var entries = await _db.Entries.IgnoreQueryFilters()
                .Where(x => x.UserId == Owner || x.UserId == Other).ToListAsync();
            _db.Entries.RemoveRange(entries);
            await _db.SaveChangesAsync();
        }
        await _db.DisposeAsync();
    }

    // ---------------- 工具 ----------------


    /// <summary>advisory lock 串行化进程内并发迁移(见 InitializeAsync 注)。</summary>
    private async Task MigrateUnderLockAsync()
    {
        await using var lockConn = new Npgsql.NpgsqlConnection(Cs);
        await lockConn.OpenAsync();
        await using (var lk = new Npgsql.NpgsqlCommand("SELECT pg_advisory_lock(90241023)", lockConn))
            await lk.ExecuteNonQueryAsync();
        try { await _db.Database.MigrateAsync(); }
        finally
        {
            await using var un = new Npgsql.NpgsqlCommand("SELECT pg_advisory_unlock(90241023)", lockConn);
            await un.ExecuteNonQueryAsync();
        }
    }

    private sealed class ParamUser(Guid id) : ICurrentUser
    {
        public Guid? UserId => id;
        public string? Email => "test@local";
        public bool IsAuthenticated => true;
        public string? IpAddress => null;
        public string? UserAgent => null;
        public IReadOnlyList<string> Roles => [];
    }

    private static InterviewsDbContext NewDb(Guid user)
    {
        // 必须镜像 Program.cs 的 DI 配置:history 表显式落在 interviews schema;
        // 缺了它,进程内 MigrateAsync 读不到已应用迁移,会从第一条重放撞表。
        var opts = new DbContextOptionsBuilder<InterviewsDbContext>()
            .UseNpgsql(Cs, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", InterviewsDbContext.Schema))
            .Options;
        return new InterviewsDbContext(opts, new ParamUser(user));
    }

    private static string Unique(string p) => p + "-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>给 user 建一条带排期的轮次;startLocal = 开场墙钟(服务器本地时区)。</summary>
    private async Task<(Guid EntryId, Guid RoundId)> SeedRoundAtAsync(Guid user, DateTime startLocal, string company)
    {
        await using var db = NewDb(user);
        var e = new InterviewEntry(Guid.NewGuid(), company, "Dev", jobApplicationId: null, userId: user);
        var r = e.AddRound("Technical");
        db.Entries.Add(e);
        await db.SaveChangesAsync();
        // 排期经公开命令路径落库(UpdateDetails 为 internal);该处理器带同步挂钩,
        // 提醒在种子阶段即生成,端点侧再对账幂等。
        var res = await new UpdateRoundCommandHandler(db).Handle(
            new UpdateRoundCommand(user, e.Id, r.Id, "Technical",
                DateOnly.FromDateTime(startLocal), null, null, null,
                InterviewRoundOutcome.Pending.ToString(), null, null, null,
                startLocal.ToString("HH:mm")), default);
        res.IsSuccess.Should().BeTrue(res.Error.Description);
        return (e.Id, r.Id);
    }

    private static string Token(Guid user, params string[] permissions)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, user.ToString()) };
        claims.AddRange(permissions.Select(p => new Claim(CustomClaims.Permission, p)));
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(Issuer, Audience, claims,
            expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static string DefaultToken(Guid user) =>
        Token(user, Permissions.InterviewsRead, Permissions.InterviewsWrite, Permissions.InterviewsAnalyze);

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetJsonAsync(string url, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _client!.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, string.IsNullOrWhiteSpace(text)
            ? default
            : JsonDocument.Parse(text).RootElement);
    }

    private async Task<HttpStatusCode> PostAckAsync(Guid reminderId, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/interviews/reminders/{reminderId}/ack");
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _client!.SendAsync(req);
        return resp.StatusCode;
    }

    private static List<JsonElement> ForRound(JsonElement list, Guid roundId) =>
        list.EnumerateArray().Where(x => x.GetProperty("roundId").GetGuid() == roundId).ToList();

    // ---------------- 用例 ----------------

    [Fact] // 本体:列表端点经查询侧对账生成两条提醒,时刻与排期口径一致
    public async Task ListReminders_GeneratesPair_ForScheduledRound()
    {
        if (!_available) return;
        var start = DateTime.Now.Date.AddDays(7).AddHours(14).AddMinutes(30);
        var (_, roundId) = await SeedRoundAtAsync(Owner, start, Unique("Co"));

        var (status, body) = await GetJsonAsync("/api/interviews/reminders", DefaultToken(Owner));

        status.Should().Be(HttpStatusCode.OK);
        var mine = ForRound(body, roundId);
        mine.Should().HaveCount(2);
        mine.Select(x => x.GetProperty("kind").GetString())
            .Should().BeEquivalentTo(["DayBefore", "HourBefore"]);
        mine.Should().OnlyContain(x => x.GetProperty("isDue").GetBoolean() == false);
        var dayBefore = mine.Single(x => x.GetProperty("kind").GetString() == "DayBefore");
        dayBefore.GetProperty("remindAt").GetDateTimeOffset()
            .Should().Be(ReminderSchedule.Compute(DateOnly.FromDateTime(start), "14:30")[0].RemindAt);
    }

    [Fact] // 到期口径:30 分钟后开场的轮次,两条提醒都已到期(SPEC 设计决定 4);未来轮次不入 due
    public async Task DueReminders_ContainsOnlyDueOnes()
    {
        if (!_available) return;
        var soon = DateTime.Now.AddMinutes(30);
        var (_, soonRound) = await SeedRoundAtAsync(Owner, soon, Unique("Soon"));
        var future = DateTime.Now.Date.AddDays(9).AddHours(10);
        var (_, futureRound) = await SeedRoundAtAsync(Owner, future, Unique("Far"));

        var (status, body) = await GetJsonAsync("/api/interviews/reminders/due", DefaultToken(Owner));

        status.Should().Be(HttpStatusCode.OK);
        ForRound(body, soonRound).Should().HaveCount(2);
        ForRound(body, futureRound).Should().BeEmpty();
    }

    [Fact] // 确认:ack 后离开 due 列表;重复 ack 幂等 204
    public async Task Ack_RemovesFromDue_AndIsIdempotent()
    {
        if (!_available) return;
        var soon = DateTime.Now.AddMinutes(45);
        var (_, roundId) = await SeedRoundAtAsync(Owner, soon, Unique("Ack"));
        var (dueStatus, dueBody) = await GetJsonAsync("/api/interviews/reminders/due", DefaultToken(Owner));
        // 票 168:先断状态再解析 —— 否则服务端/鉴权失败(401 空体、409/500 ProblemDetails 对象)
        // 会在 ForRound EnumerateArray 处抛错,掩盖真实状态。
        dueStatus.Should().Be(HttpStatusCode.OK, "due list must succeed, body: {0}",
            dueBody.ValueKind == JsonValueKind.Undefined ? "<empty body>" : dueBody.ToString());
        var target = ForRound(dueBody, roundId).First().GetProperty("id").GetGuid();

        (await PostAckAsync(target, DefaultToken(Owner))).Should().Be(HttpStatusCode.NoContent);
        (await PostAckAsync(target, DefaultToken(Owner))).Should().Be(HttpStatusCode.NoContent);

        var (_, dueAfter) = await GetJsonAsync("/api/interviews/reminders/due", DefaultToken(Owner));
        ForRound(dueAfter, roundId).Select(x => x.GetProperty("id").GetGuid())
            .Should().NotContain(target);
    }

    [Fact] // boundary: 跨用户 —— 他人确认我的提醒回 404(按不存在处理)
    public async Task Ack_OtherUsersReminder_Returns404()
    {
        if (!_available) return;
        var soon = DateTime.Now.AddMinutes(45);
        var (_, roundId) = await SeedRoundAtAsync(Owner, soon, Unique("Iso"));
        var (dueStatus, dueBody) = await GetJsonAsync("/api/interviews/reminders/due", DefaultToken(Owner));
        // 票 168:先断状态再解析(同上;本用例即 CI 两例的失败点)。
        dueStatus.Should().Be(HttpStatusCode.OK, "due list must succeed, body: {0}",
            dueBody.ValueKind == JsonValueKind.Undefined ? "<empty body>" : dueBody.ToString());
        var target = ForRound(dueBody, roundId).First().GetProperty("id").GetGuid();

        (await PostAckAsync(target, DefaultToken(Other))).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact] // boundary: 跨用户 —— 他人的列表里看不到我的提醒
    public async Task ListReminders_OtherUser_SeesNoneOfMine()
    {
        if (!_available) return;
        var start = DateTime.Now.Date.AddDays(5).AddHours(11);
        var (_, roundId) = await SeedRoundAtAsync(Owner, start, Unique("IsoL"));

        var (status, body) = await GetJsonAsync("/api/interviews/reminders", DefaultToken(Other));

        status.Should().Be(HttpStatusCode.OK);
        ForRound(body, roundId).Should().BeEmpty();
    }

    [Fact] // boundary: 未登录 401;只有写权限(无 InterviewsRead)读列表 403
    public async Task ListReminders_NoToken401_WriteOnly403()
    {
        if (!_available) return;

        var (s1, _) = await GetJsonAsync("/api/interviews/reminders", token: null);
        s1.Should().Be(HttpStatusCode.Unauthorized);

        var (s2, _) = await GetJsonAsync("/api/interviews/reminders",
            Token(Owner, Permissions.InterviewsWrite));
        s2.Should().Be(HttpStatusCode.Forbidden);
    }
}
