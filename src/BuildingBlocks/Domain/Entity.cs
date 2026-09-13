namespace YourInterview.BuildingBlocks.Domain;

/// <summary>
/// 实体基类。用 id 判等,不是引用判等 —— DDD 的基本要求。
/// </summary>
public abstract class Entity
{
    /// <summary>
    /// 实体主键。
    /// 用 Guid v4 在 C# 端直接生成(不用数据库自增)——
    /// 好处:聚合内一次性构造完整对象图、领域事件里可立刻带上 Id、批量写入不依赖往返。
    ///
    /// ⚠️ 关键:必须让 EF 知道"这个值是我方给的临时值"。
    /// 否则当子实体被加入一个「已跟踪的父聚合」时,EF 看到主键非空就当成已存在的行,
    /// 生成 UPDATE 而不是 INSERT → 影响 0 行 → DbUpdateConcurrencyException。
    /// 由 DbContext 在 SaveChanges 前用 MarkNewChildEntitiesAsAdded() 统一修正。
    /// </summary>
    public Guid Id { get; protected init; } = Guid.NewGuid();

    private readonly List<IDomainEvent> _domainEvents = new();
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void RaiseDomainEvent(IDomainEvent e) => _domainEvents.Add(e);
    public void ClearDomainEvents() => _domainEvents.Clear();

    public override bool Equals(object? obj)
        => obj is Entity other && GetType() == other.GetType() && Id == other.Id;
    public override int GetHashCode() => (GetType(), Id).GetHashCode();
}
