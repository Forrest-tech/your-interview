namespace YourInterview.Services.Identity.Application.Logto;

/// <summary>
/// 票241:审批门健康快照(单例;审计行本身不足以告警 —— 审计只能证明“做过什么”,
/// 证明不了“该做的没做”:webhook 断流、扫表停摆在审计里是沉默的)。
/// 告警阈值(部署清单):扫表心跳缺失 &gt; 30 分钟、连续扫表失败 ≥ 3 次、webhook 长时间静默。
/// </summary>
public sealed class LogtoGateHealth
{
    private readonly object _lock = new();

    public DateTimeOffset? LastWebhookReceivedAt { get; private set; }
    public long WebhookReceivedCount { get; private set; }
    public DateTimeOffset? LastSweepAt { get; private set; }
    public int LastSweepActions { get; private set; }
    public string? LastSweepError { get; private set; }
    public int ConsecutiveSweepFailures { get; private set; }

    public void RecordWebhookReceived()
    {
        lock (_lock)
        {
            LastWebhookReceivedAt = DateTimeOffset.UtcNow;
            WebhookReceivedCount++;
        }
    }

    public void RecordSweepSuccess(int actions)
    {
        lock (_lock)
        {
            LastSweepAt = DateTimeOffset.UtcNow;
            LastSweepActions = actions;
            LastSweepError = null;
            ConsecutiveSweepFailures = 0;
        }
    }

    public void RecordSweepFailure(string error)
    {
        lock (_lock)
        {
            LastSweepAt = DateTimeOffset.UtcNow;
            LastSweepError = error;
            ConsecutiveSweepFailures++;
        }
    }
}
