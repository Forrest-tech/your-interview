namespace YourInterview.Services.Assessment.Domain;

/// <summary>
/// 练习类别 —— /practice 左侧素材树上方的「练习类别」下拉。
///
/// 需求(2026-09-25,Forrest):素材多了之后要按场景分组练习
/// (工作面试 / 生活英语 / 自定义...),下拉一选,树里只看这一类。
///
/// 设计取舍:
///   · 类别是**用户的标签**,不是树的层级 —— 一个顶层素材恰好属于一个类别
///     (nullable;null = 未分类)。比起「再造一层特殊文件夹」,
///     标签不破坏现有拖拽/整树保存语义,历史素材自动落入"未分类"。
///   · 只对**根级素材**生效:类别是练习场景,子节点跟随父节点,
///     否则同一份讲稿父子分属不同类别,过滤结果会碎掉。
///   · 删除类别时素材不删 —— 数据库层 OnDelete(SetNull),
///     素材自动回到"未分类",数据零丢失。
/// </summary>
public sealed class PracticeCategory
{
    public PracticeCategory(Guid userId, string name, int sortOrder = 0)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("类别名不能为空", nameof(name));
        UserId = userId;
        Name = name.Trim();
        SortOrder = sortOrder;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private PracticeCategory() { }

    public Guid Id { get; private set; } = Guid.NewGuid();
    /// <summary>归属用户 —— 与素材同一套按用户隔离的口径。</summary>
    public Guid UserId { get; private set; }
    /// <summary>类别名(如 工作面试 / 生活英语)。同一用户内唯一。</summary>
    public string Name { get; private set; } = string.Empty;
    /// <summary>下拉里的显示顺序,升序。排序弹窗拖完改的就是它。</summary>
    public int SortOrder { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("类别名不能为空", nameof(name));
        Name = name.Trim();
        Touch();
    }

    public void SetSortOrder(int sortOrder)
    {
        SortOrder = sortOrder;
        Touch();
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}
