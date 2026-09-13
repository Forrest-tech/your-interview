namespace YourInterview.BuildingBlocks.Domain;

/// <summary>
/// 聚合根基类。所有对内部状态的修改都必须经聚合根的方法,保证不变式(invariant)。
/// </summary>
public abstract class AggregateRoot : Entity
{
    /// <summary>
    /// 乐观并发版本号。
    /// 注意:PostgreSQL 下我们统一用 xmin 系统列做并发令牌(见 UseXminAsConcurrencyToken),
    /// 所以这里用 [NotMapped] —— 否则 EF 会把它当成第二个并发令牌,
    /// 而它永远不自增 → UPDATE 的 WHERE version=0 永远匹配不到行 → DbUpdateConcurrencyException。
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public uint Version { get; protected set; }
}

/// <summary>软删除 + 审计字段的聚合根。</summary>
public abstract class AuditableAggregateRoot : AggregateRoot
{
    public DateTimeOffset CreatedAt { get; protected set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; protected set; }
    public bool IsDeleted { get; protected set; }

    public void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
    public void MarkDeleted() { IsDeleted = true; Touch(); }
}
