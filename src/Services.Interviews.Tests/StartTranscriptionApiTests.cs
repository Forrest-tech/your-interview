using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using YourInterview.Services.Interviews.Api;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using YourInterview.Services.Interviews.Infrastructure.Services;
using YourInterview.SharedContracts.Security;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票115:InterviewsController.StartTranscription Api 异步包装的 HTTP 宿主级集成测试。
/// 点名缺口(正式验收覆盖率报告):该包装此前 0/4 行 —— 域层与 Handler 层已有测试,
/// 但「路由 + JWT 鉴权 + 权限政策 + MediatR 转发 + Result→HTTP 形态」整链从未被触达。
/// 形态:WebApplicationFactory(工程首例;Interviews Program 无 partial 标记,
/// 故以同程序集公开类型 InterviewsController 作入口标记,产品代码零改动)。
/// 测试侧适配(均在 ConfigureWebHost/ConfigureTestServices 内,不改产品):
///  · Jwt:SigningKey 经环境变量覆盖为测试密钥,token 同密钥自签;用户口径沿用本套件惯例
///    TestCurrentUser —— sub = Guid.Empty,种子条目 userId 同为 Guid.Empty。
///  · Messaging:Enabled=false(同走环境变量:Program 急切读取,内存配置源会读穿)
///    → 既有 NoOpPublishEndpoint 口径,不连 RabbitMQ(探针实证)。
///  · AddServiceDefaults 按 EntryAssembly 扫描 MediatR —— 测试宿主下扫不到本服务
///    Handler,故补注册 Interviews 程序集(与生产扫描互补,非重复)。
///  · 摘除 AnalysisJobDispatcher / AudioIntegritySweepService 两个后台服务,
///    避免派发/巡检与断言竞态。
/// 无沙盒 DB 的环境整类跳过(同 CreateEntryDedupeTests 先例)。
/// 断言与现行契约逐字对齐(读码定版,见 evidence-115):
///  成功 204 空体;未授权 401;无写权限 403;条目不存在 404 ProblemDetails
///  (code=面试条目.NotFound);无材料 409 ProblemDetails(code=Transcription.InvalidState)。
/// 顺带同型:StartAssetTranscription(同命令同形态包装)成功转发 + 未知材料 409 两路。
/// </summary>
public sealed class StartTranscriptionApiTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";
    private const string SigningKey = "t115-host-test-signing-key-0123456789abcdef";
    private const string Issuer = "your-interview.identity";
    private const string Audience = "your-interview.api";

    private WebApplicationFactory<InterviewsController>? _factory;
    private HttpClient? _client;
    private InterviewsDbContext _db = null!;
    private bool _available;

    public async Task InitializeAsync()
    {
        var opts = new DbContextOptionsBuilder<InterviewsDbContext>().UseNpgsql(Cs).Options;
        _db = new InterviewsDbContext(opts, new TestCurrentUser());
        try { _available = await _db.Database.CanConnectAsync(); }
        catch { _available = false; }
        if (!_available) return;

        // WAF 两处按宿主进程定位文件,xunit console 宿主下都偏离测试 bin:
        //  ① deps.json 按 AppContext.BaseDirectory(= runner 目录)找 —— 补拷过去;
        //  ② 内容根清单 MvcTestingAppManifest.json 按进程 CWD 相对查找,找不到即回落
        //     「向上找 .sln」而抛错 —— 建宿主期间临时把 CWD 切到测试 bin,建完恢复。
        // (VSTest/dotnet test 下 BaseDirectory/CWD 口径不同但两处文件都在测试 bin,同样成立。)
        var testBin = Path.GetDirectoryName(typeof(StartTranscriptionApiTests).Assembly.Location)!;
        var appName = typeof(InterviewsController).Assembly.GetName().Name;
        var depsTarget = Path.Combine(AppContext.BaseDirectory, $"{appName}.deps.json");
        if (!File.Exists(depsTarget)) File.Copy(Path.Combine(testBin, $"{appName}.deps.json"), depsTarget);

        // 配置覆盖走环境变量而非 ConfigureAppConfiguration 内存源:WAF 延迟宿主下内存源
        // 只对懒求值(JWT 校验)生效,Program 对 Messaging:Enabled 的急切读取会读穿到
        // appsettings 的 true(实测真总线被注册);环境变量由 CreateBuilder 急切加载,两头都稳。
        Environment.SetEnvironmentVariable("Jwt__SigningKey", SigningKey);
        Environment.SetEnvironmentVariable("Messaging__Enabled", "false");

        _factory = new WebApplicationFactory<InterviewsController>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // EntryAssembly 扫描在测试宿主下扫不到 Interviews Handler,补注册本程序集。
                    services.AddMediatR(cfg =>
                        cfg.RegisterServicesFromAssembly(typeof(BeginTranscriptionCommand).Assembly));
                    // 摘除后台巡检/派发,避免与断言竞态(派发本身非本票断言面)。
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
        await _db.DisposeAsync();
    }

    // ---------------- 种子与请求工具 ----------------

    private static string UniqueCompany() => "T115-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<(InterviewEntry Entry, Guid AudioId)> SeedEntryAsync(bool withAudio)
    {
        var e = new InterviewEntry(Guid.NewGuid(), UniqueCompany(), "Senior .NET Developer");
        var audioId = Guid.Empty;
        if (withAudio)
        {
            var audio = e.AttachAsset(AssetKind.Audio, "r1.wav", "audio/wav", 100,
                "audio/t115-r1.wav", null);
            audioId = audio.Id;
        }
        _db.Entries.Add(e);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return (e, audioId);
    }

    private static string Token(params string[] permissions)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, Guid.Empty.ToString()) };
        claims.AddRange(permissions.Select(p => new Claim(CustomClaims.Permission, p)));
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(Issuer, Audience, claims,
            expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private Task<HttpResponseMessage> PostAsync(string url, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client!.SendAsync(req);
    }

    private static async Task<JsonElement> ProblemBodyAsync(HttpResponseMessage resp)
    {
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var json = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement;
    }

    private async Task<InterviewStatus> ReloadStatusAsync(Guid entryId)
    {
        _db.ChangeTracker.Clear();
        var e = await _db.Entries.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(x => x.Id == entryId);
        return e.Status;
    }

    // ---------------- StartTranscription(条目级,点名包装) ----------------

    [Fact]
    public async Task StartTranscription_WithAudio_Returns204_AndEntryBecomesTranscribing()
    {
        if (!_available) return;
        var (e, _) = await SeedEntryAsync(withAudio: true);

        var resp = await PostAsync($"/api/interviews/{e.Id}/transcription/start",
            Token(Permissions.InterviewsWrite));

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await resp.Content.ReadAsStringAsync()).Should().BeEmpty();
        // 真转发落库,不是假绿:状态机已推进且台账任务同事务落行(Outbox 拦截器)。
        (await ReloadStatusAsync(e.Id)).Should().Be(InterviewStatus.Transcribing);
        var jobs = await _db.AnalysisJobs.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.InterviewEntryId == e.Id).ToListAsync();
        jobs.Should().ContainSingle(j => j.JobType == AnalysisJobType.Transcription);
    }

    [Fact]
    public async Task StartTranscription_NoToken_Returns401()
    {
        if (!_available) return;
        var (e, _) = await SeedEntryAsync(withAudio: true);

        var resp = await PostAsync($"/api/interviews/{e.Id}/transcription/start", token: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReloadStatusAsync(e.Id)).Should().Be(InterviewStatus.AssetsUploaded);
    }

    [Fact]
    public async Task StartTranscription_WithoutWritePermission_Returns403()
    {
        if (!_available) return;
        var (e, _) = await SeedEntryAsync(withAudio: true);

        var resp = await PostAsync($"/api/interviews/{e.Id}/transcription/start",
            Token(Permissions.InterviewsRead));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReloadStatusAsync(e.Id)).Should().Be(InterviewStatus.AssetsUploaded);
    }

    [Fact]
    public async Task StartTranscription_UnknownEntry_Returns404ProblemDetails()
    {
        if (!_available) return;

        var resp = await PostAsync($"/api/interviews/{Guid.NewGuid()}/transcription/start",
            Token(Permissions.InterviewsWrite));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await ProblemBodyAsync(resp);
        body.GetProperty("title").GetString().Should().Be("Resource not found");
        body.GetProperty("type").GetString().Should()
            .Be("https://tools.ietf.org/html/rfc9110#section-15.5.5");
        body.GetProperty("status").GetInt32().Should().Be(404);
        body.GetProperty("detail").GetString().Should().Be("面试条目 不存在");
        body.GetProperty("code").GetString().Should().Be("面试条目.NotFound");
    }

    [Fact]
    public async Task StartTranscription_EntryWithoutAssets_Returns409ProblemDetails()
    {
        if (!_available) return;
        var (e, _) = await SeedEntryAsync(withAudio: false);

        var resp = await PostAsync($"/api/interviews/{e.Id}/transcription/start",
            Token(Permissions.InterviewsWrite));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await ProblemBodyAsync(resp);
        body.GetProperty("title").GetString().Should().Be("Conflict");
        body.GetProperty("type").GetString().Should()
            .Be("https://tools.ietf.org/html/rfc9110#section-15.5.10");
        body.GetProperty("status").GetInt32().Should().Be(409);
        body.GetProperty("detail").GetString().Should().Be("还没有上传任何材料,无法开始转写");
        body.GetProperty("code").GetString().Should().Be("Transcription.InvalidState");
        (await ReloadStatusAsync(e.Id)).Should().Be(InterviewStatus.Draft);
    }

    // ---------------- StartAssetTranscription(同型包装,顺带) ----------------

    [Fact]
    public async Task StartAssetTranscription_WithAudio_Returns204_AndJobPayloadTargetsThatAsset()
    {
        if (!_available) return;
        var (e, audioId) = await SeedEntryAsync(withAudio: true);

        var resp = await PostAsync($"/api/interviews/{e.Id}/assets/{audioId}/transcribe",
            Token(Permissions.InterviewsWrite));

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReloadStatusAsync(e.Id)).Should().Be(InterviewStatus.Transcribing);
        var jobs = await _db.AnalysisJobs.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.InterviewEntryId == e.Id).ToListAsync();
        jobs.Should().ContainSingle(j =>
            j.JobType == AnalysisJobType.Transcription &&
            j.PayloadJson.Contains(audioId.ToString()));
    }

    [Fact]
    public async Task StartAssetTranscription_UnknownAsset_Returns409ProblemDetails()
    {
        if (!_available) return;
        var (e, _) = await SeedEntryAsync(withAudio: true);

        var resp = await PostAsync(
            $"/api/interviews/{e.Id}/assets/{Guid.NewGuid()}/transcribe",
            Token(Permissions.InterviewsWrite));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await ProblemBodyAsync(resp);
        body.GetProperty("title").GetString().Should().Be("Conflict");
        body.GetProperty("status").GetInt32().Should().Be(409);
        body.GetProperty("detail").GetString().Should().Be("材料不存在");
        body.GetProperty("code").GetString().Should().Be("Transcription.InvalidState");
    }
}
