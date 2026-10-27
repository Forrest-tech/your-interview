using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YourInterview.Services.Identity.Application.Logto;

namespace YourInterview.Services.Identity.Infrastructure.Services;

/// <summary>
/// 票241:审批门对账扫表后台任务(每 15 分钟,PLAN §4.2a)。
/// 未配 Management 凭据时直接退出(不报错、不空转);每轮异常只记日志、
/// 更新健康快照(连续失败计数),不终止任务(下一周期继续扫 —— 扫表本身
/// 就是兜底,不能因一次失败停摆)。首轮在第一个间隔后跑,避免与服务启动期
/// 的迁移/种子抢资源。每轮无论成败都写一条心跳审计行:心跳缺失本身就是
/// 告警信号(审计只能证明“做过什么”,证明不了“该做的没做”)。
/// </summary>
public sealed class LogtoApprovalReconciliationService(
    IServiceScopeFactory scopes,
    IOptions<LogtoManagementOptions> options,
    LogtoGateHealth health,
    ILogger<LogtoApprovalReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ManagementConfigured)
        {
            logger.LogInformation("Logto Management 未配置,对账扫表不启动(配好凭据后重启 Identity 即生效)");
            return;
        }
        var interval = TimeSpan.FromMinutes(options.Value.SweepIntervalMinutes > 0
            ? options.Value.SweepIntervalMinutes : 15);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
            try
            {
                using var scope = scopes.CreateScope();
                var gate = scope.ServiceProvider.GetRequiredService<LogtoApprovalGate>();
                var actions = await gate.ReconcileAsync(stoppingToken);
                health.RecordSweepSuccess(actions.Count);
                var audit = scope.ServiceProvider.GetRequiredService<ILogtoSweepAudit>();
                await audit.WriteAsync("logto.approval.sweep.heartbeat", "sweep",
                    $"扫表心跳:本轮动作 {actions.Count} 条", stoppingToken);
                logger.LogInformation("Logto 审批门扫表完成,动作 {Count} 条", actions.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                health.RecordSweepFailure(ex.Message);
                logger.LogError(ex, "Logto 审批门扫表失败,下一周期重试");
            }
        }
    }
}
