using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Jobs.Api;
using YourInterview.Services.Jobs.Application;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using YourInterview.SharedContracts.Security;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票68(用户裁决 B,2026-10-23):Jobs DELETE 端点授权口径由 JobsDelete 对齐 JobsWrite
/// (同 DeleteCoverLetter / favorites / 删自备问题先例)。缺口前提:默认 User 角色只有
/// JobsRead+JobsWrite、没有 JobsDelete —— 原口径下普通用户删自己的数据恒 403。
/// 两层断言:
///  ① 策略矩阵(纯反射、无 DB):JobsController 全部 HttpDelete 端点的 Authorize 策略
///     必须恰为 perm:jobs.write,无一为 perm:jobs.delete;默认角色权限前提与
///     PowerUser/Admin 保留 JobsDelete 的不变项一并锁定。
///  ② HTTP 宿主级(WAF,镜像票115 形态):默认角色权限集 token 删自己投递 → 204 且
///     落库删除;只有 JobsRead → 403;无 token → 401;仅 JobsDelete(无 JobsWrite)
///     → 403(新口径锁定);持 JobsWrite 删他人投递 → 404 且原行无损
///     (票03 跨用户隔离不变项 —— 归属由 Handler 按 UserId 校验,不靠权限点把关)。
/// DB 不可达时 ② 整组跳过(同仓内先例)。
/// </summary>
public sealed class DeleteAuthorizationApiTests : IAsyncLifetime
{
    private const string Cs = "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";
    private const string SigningKey = "t68-host-test-signing-key-0123456789abcdef";
    private const string Issuer = "your-interview.identity";
    private const string Audience = "your-interview.api";

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private WebApplicationFactory<JobsController>? _factory;
    private HttpClient? _client;
    private JobsDbContext _db = null!;
    private bool _available;

    public async Task InitializeAsync()
    {
        _db = NewDb(Owner);
        try { _available = await _db.Database.CanConnectAsync(); }
        catch { _available = false; }
        if (!_available) return;

        // WAF 宿主定位适配同票115:deps.json 补拷到 runner 目录;建宿主期间 CWD 切测试 bin。
        var testBin = Path.GetDirectoryName(typeof(DeleteAuthorizationApiTests).Assembly.Location)!;
        var appName = typeof(JobsController).Assembly.GetName().Name;
        var depsTarget = Path.Combine(AppContext.BaseDirectory, $"{appName}.deps.json");
        if (!File.Exists(depsTarget)) File.Copy(Path.Combine(testBin, $"{appName}.deps.json"), depsTarget);

        // 配置覆盖走环境变量(Program 急切读取项靠内存源会读穿,同票115 实证)。
        Environment.SetEnvironmentVariable("Jwt__SigningKey", SigningKey);
        Environment.SetEnvironmentVariable("Messaging__Enabled", "false");
        Environment.SetEnvironmentVariable("ConnectionStrings__Jobs", Cs);

        _factory = new WebApplicationFactory<JobsController>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // AddServiceDefaults 按 EntryAssembly 扫描 MediatR,测试宿主下扫不到
                    // Jobs Handler,补注册本程序集(与生产扫描互补,非重复)。
                    services.AddMediatR(cfg =>
                        cfg.RegisterServicesFromAssembly(typeof(DeleteApplicationCommand).Assembly));
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
        Environment.SetEnvironmentVariable("ConnectionStrings__Jobs", null);
        if (_available)
        {
            var apps = await _db.Applications.IgnoreQueryFilters()
                .Where(a => a.UserId == Owner || a.UserId == Other).ToListAsync();
            _db.Applications.RemoveRange(apps);
            var companies = await _db.Companies.IgnoreQueryFilters()
                .Where(c => c.UserId == Owner || c.UserId == Other).ToListAsync();
            _db.Companies.RemoveRange(companies);
            await _db.SaveChangesAsync();
        }
        await _db.DisposeAsync();
    }

    // ---------------- 工具 ----------------

    private static JobsDbContext NewDb(Guid currentUser)
    {
        var opts = new DbContextOptionsBuilder<JobsDbContext>().UseNpgsql(Cs).Options;
        return new JobsDbContext(opts, new TestCurrentUser(currentUser));
    }

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<Guid> SeedApplicationAsync(Guid user)
    {
        await using var db = NewDb(user);
        var co = await new CreateCompanyCommandHandler(db).Handle(
            new CreateCompanyCommand(user, Unique("T68Co"), null, null, null, null, null), default);
        co.IsSuccess.Should().BeTrue(co.Error.Description);
        var app = await new CreateApplicationCommandHandler(db).Handle(
            new CreateApplicationCommand(user, co.Value, "Dev", null, null, null, null, null, null), default);
        app.IsSuccess.Should().BeTrue(app.Error.Description);
        return app.Value;
    }

    private async Task<bool> ApplicationVisibleAsync(Guid user, Guid id)
    {
        await using var db = NewDb(user);
        var r = await new GetApplicationQueryHandler(db).Handle(new GetApplicationQuery(user, id), default);
        return r.IsSuccess;
    }

    /// <summary>token:sub = 用户;perm = 给定权限集。默认角色集直接取 Roles.DefaultPermissions,防手抄漂移。</summary>
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

    private static string DefaultUserToken(Guid user) => Token(user, Roles.DefaultPermissions[Roles.User]);

    private Task<HttpResponseMessage> DeleteAsync(string url, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, url);
        if (token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client!.SendAsync(req);
    }

    // ---------------- ② HTTP 宿主级 ----------------

    [Fact] // 票68 本体:默认角色(无 JobsDelete)删自己的投递 —— 原口径 403,裁决 B 后应 204
    public async Task DeleteApplication_DefaultUserPermissions_OwnApplication_Returns204_AndIsDeleted()
    {
        if (!_available) return;
        var appId = await SeedApplicationAsync(Owner);

        var resp = await DeleteAsync($"/api/jobs/applications/{appId}", DefaultUserToken(Owner));

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ApplicationVisibleAsync(Owner, appId)).Should().BeFalse();
    }

    [Fact] // boundary: 无权限 —— 只有 JobsRead,写/删都应 403 且原行无损
    public async Task DeleteApplication_ReadOnlyToken_Returns403_AndRowIntact()
    {
        if (!_available) return;
        var appId = await SeedApplicationAsync(Owner);

        var resp = await DeleteAsync($"/api/jobs/applications/{appId}",
            Token(Owner, Permissions.JobsRead));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ApplicationVisibleAsync(Owner, appId)).Should().BeTrue();
    }

    [Fact] // boundary: 未登录 —— 401(类级 [Authorize] 口径,不回落 Guid.Empty)
    public async Task DeleteApplication_NoToken_Returns401()
    {
        if (!_available) return;
        var appId = await SeedApplicationAsync(Owner);

        var resp = await DeleteAsync($"/api/jobs/applications/{appId}", token: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApplicationVisibleAsync(Owner, appId)).Should().BeTrue();
    }

    [Fact] // boundary: 旧口径权限单独出现 —— 只有 JobsDelete、没有 JobsWrite,新口径下 403
    public async Task DeleteApplication_JobsDeleteOnlyToken_Returns403()
    {
        if (!_available) return;
        var appId = await SeedApplicationAsync(Owner);

        var resp = await DeleteAsync($"/api/jobs/applications/{appId}",
            Token(Owner, Permissions.JobsRead, Permissions.JobsDelete));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ApplicationVisibleAsync(Owner, appId)).Should().BeTrue();
    }

    [Fact] // boundary: 跨用户 —— 持默认权限集删他人投递,Handler 归属校验回 404 且原行无损(票03 不变项)
    public async Task DeleteApplication_OtherUsersApplication_Returns404_AndRowIntact()
    {
        if (!_available) return;
        var appId = await SeedApplicationAsync(Owner);

        var resp = await DeleteAsync($"/api/jobs/applications/{appId}", DefaultUserToken(Other));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ApplicationVisibleAsync(Owner, appId)).Should().BeTrue();
    }

    [Fact] // 同口径抽验第二个端点:删公司(默认角色,自己的公司无投递引用)
    public async Task DeleteCompany_DefaultUserPermissions_OwnCompany_Returns204()
    {
        if (!_available) return;
        Guid companyId;
        await using (var db = NewDb(Owner))
        {
            var co = await new CreateCompanyCommandHandler(db).Handle(
                new CreateCompanyCommand(Owner, Unique("T68Co"), null, null, null, null, null), default);
            co.IsSuccess.Should().BeTrue(co.Error.Description);
            companyId = co.Value;
        }

        var resp = await DeleteAsync($"/api/jobs/companies/{companyId}", DefaultUserToken(Owner));

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}

/// <summary>票68 ① 策略矩阵:纯反射锁定全部 DELETE 端点口径与角色权限前提,不依赖 DB。</summary>
public sealed class DeleteEndpointPolicyTests
{
    [Fact]
    public void AllDeleteEndpoints_RequireJobsWritePolicy_NoneRequireJobsDelete()
    {
        var deleteMethods = typeof(JobsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes(typeof(HttpDeleteAttribute), inherit: false).Any())
            .ToList();

        // 已知全集 10 个:companies / applications / resume-text / resume-file /
        // applications-resume / cover-letter / answer-templates / communications /
        // favorites / questions —— 新增 DELETE 端点时本断言提醒同步登记口径。
        deleteMethods.Should().HaveCountGreaterThanOrEqualTo(10);
        foreach (var m in deleteMethods)
        {
            var policies = m.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
                .Cast<AuthorizeAttribute>()
                .Where(a => a.Policy is not null)
                .Select(a => a.Policy)
                .ToList();
            policies.Should().ContainSingle(
                $"DELETE endpoint {m.Name} must declare exactly one permission policy");
            policies[0].Should().Be(PermissionPolicy.Name(Permissions.JobsWrite),
                $"DELETE endpoint {m.Name} follows the ticket-68 ruling B (JobsWrite)");
        }
    }

    [Fact]
    public void RolePermissionPremise_UserLacksJobsDelete_PowerUserAndAdminKeepIt()
    {
        // 缺口前提:默认 User 无 JobsDelete、有 JobsWrite —— 本票不动 Permissions.cs。
        Roles.DefaultPermissions[Roles.User].Should().NotContain(Permissions.JobsDelete);
        Roles.DefaultPermissions[Roles.User].Should().Contain(Permissions.JobsWrite);
        // 不变项:PowerUser/Admin 行为不回退,JobsDelete 权限点保留并发放如旧。
        Roles.DefaultPermissions[Roles.PowerUser].Should().Contain(Permissions.JobsDelete);
        Roles.DefaultPermissions[Roles.Admin].Should().Contain(Permissions.JobsDelete);
    }
}
