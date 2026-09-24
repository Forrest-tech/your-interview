using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using YourInterview.BuildingBlocks.Hosting;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using YourInterview.Services.Interviews.Infrastructure.Services;
using YourInterview.Services.Interviews.Infrastructure.Storage;
using YourInterview.SharedContracts;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("interviews-api");

// ---------- 录音文件存储(本地卷,数据库只存相对路径 + SHA-256) ----------
builder.Services.AddSingleton<IInterviewAudioStore, LocalInterviewAudioStore>();

// ---------- 当前用户(集成事件要带 UserId 给下游 Worker/Analytics) ----------
// ⚠️ AddCurrentUser 不在 AddServiceDefaults 里 —— 按需显式调用(见其注释)。
//    事件发布器 InterviewsIntegrationEventPublisher 构造注入 ICurrentUser,
//    不注册会让 DI 校验直接失败 → 服务起不来。
builder.Services.AddCurrentUser();

// ---------- 数据库:独占 schema "interviews" ----------
builder.Services.AddDbContext<InterviewsDbContext>((sp, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("InterviewsDb"),
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", InterviewsDbContext.Schema));
    options.AddInterceptors(new DomainEventDispatchInterceptor(sp.GetRequiredService<MediatR.IPublisher>()));
});

builder.Services.AddHealthChecks().AddDbContextCheck<InterviewsDbContext>("postgres");

// ---------- 消息总线:转写完成 → 触发分析流水线 ----------
builder.Services.AddMassTransitWithRabbitMq(builder.Configuration);

// ---------- 鉴权 ----------
var jwtSection = builder.Configuration.GetSection("Jwt");
var signingKey = jwtSection["SigningKey"] ?? throw new InvalidOperationException("缺少 Jwt:SigningKey");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization(options => PermissionPolicy.AddPermissionPolicies(options));

var app = builder.Build();

app.UseServiceDefaults("interviews-api");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

try { await InterviewsDbSeeder.MigrateAndSeedAsync(app.Services, app.Configuration, app.Logger); }
catch (Exception ex) { app.Logger.LogError(ex, "Interviews 数据库初始化失败 —— 服务继续启动"); }

app.Run();
