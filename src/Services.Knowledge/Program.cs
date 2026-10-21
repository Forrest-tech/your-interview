using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using YourInterview.BuildingBlocks.Hosting;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;
using YourInterview.Services.Knowledge.Infrastructure.Services;
using YourInterview.SharedContracts;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("knowledge-api");

// ---------- 数据库 ----------
// 每个微服务独占一个 PostgreSQL schema:knowledge
builder.Services.AddDbContext<KnowledgeDbContext>((sp, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("KnowledgeDb"),
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", KnowledgeDbContext.Schema));
    options.AddInterceptors(new DomainEventDispatchInterceptor(sp.GetRequiredService<MediatR.IPublisher>()));
});

builder.Services.AddHealthChecks().AddDbContextCheck<KnowledgeDbContext>("postgres");


// ---------- 消息总线(掌握度变化 → Analytics) ----------
builder.Services.AddMassTransitWithRabbitMq(builder.Configuration);
builder.Services.AddScoped<KnowledgeMasteryChangedPublisher>();

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

// Swagger / OpenAPI 已由 AddServiceDefaults 统一注册(标题/安全方案都在那里改一处即可)。

var app = builder.Build();

app.UseServiceDefaults("knowledge-api");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Knowledge API v1"));
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// 建表 + 灌种子。COD-14/票02:迁移失败即启动失败(fail-fast,共享处置见 ServiceDefaults)—— 不再带病启动假健康
await app.RunStartupMigrationsOrFailAsync("knowledge-api", KnowledgeDbSeeder.MigrateAndSeedAsync);

app.Run();
