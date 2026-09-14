using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using YourInterview.BuildingBlocks.Hosting;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.SharedContracts;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("assessment-api");

// ---------- 数据库:独占 schema "assessment" ----------
builder.Services.AddDbContext<AssessmentDbContext>((sp, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("AssessmentDb"),
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AssessmentDbContext.Schema));
    options.AddInterceptors(new DomainEventDispatchInterceptor(sp.GetRequiredService<MediatR.IPublisher>()));
});

builder.Services.AddHealthChecks().AddDbContextCheck<AssessmentDbContext>("postgres");

// ---------- CQRS 管道 ----------
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(System.Reflection.Assembly.GetExecutingAssembly());
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.LoggingBehavior<,>));
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.ValidationBehavior<,>));
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.PerformanceBehavior<,>));
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.UnhandledExceptionBehavior<,>));
});

// ---------- 当前用户(练习是纯私有数据,按 UserId 隔离) ----------
builder.Services.AddCurrentUser();

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

app.UseServiceDefaults("assessment-api");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

try { await AssessmentDbSeeder.MigrateAndSeedAsync(app.Services, app.Configuration, app.Logger); }
catch (Exception ex) { app.Logger.LogError(ex, "Assessment 数据库初始化失败 —— 服务继续启动"); }

app.Run();
