using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.Services.Knowledge.Domain;

/// <summary>
/// Tech Stack 自定义空分类(COD-10c/票16)—— 用户在分类管理弹窗里手动新建、
/// 尚无条目的分类名。
///
/// 此前只存浏览器 localStorage(ts.customCategories),换设备/清缓存即丢。
/// 有条目的分类由条目 Topic 聚合得出,不在此表重复存;此表只承载「空分类」
/// 这一后端原本无处安放的形态。前端合并展示时同名以后端条目计数为准。
///
/// 归属(票03/票14 口径):带 UserId,查询恒带归属谓词;(UserId, Name) 唯一。
/// 名称比较口径与前端现状一致:大小写不敏感查重(Handler 层),存 Trim 后原文。
/// </summary>
public sealed class TechStackCategory : AuditableAggregateRoot
{
    private TechStackCategory() { }

    public TechStackCategory(Guid userId, string name)
    {
        UserId = userId;
        Name = name;
    }

    public Guid UserId { get; private set; }

    /// <summary>分类名(用户原文,Trim 后存;与 KnowledgeItem.Topic 同上限 100)。</summary>
    public string Name { get; private set; } = string.Empty;
}

/// <summary>
/// 每日复习活动(COD-10c/票16)—— 热力图/连击的数据源:一天一行,(日期, 次数)。
///
/// 此前只存浏览器 localStorage(ts.reviewActivity,形状 Record&lt;日期串,次数&gt;)。
///
/// 日期口径(数据语义,不得变更):Date 是**用户本地日历日期**。前端写入端
/// (techstack recordReviewActivity)历来用本地年月日拼键并注明「不用 UTC,
/// 21:00 EDT 会记到第二天」;本实体以 DateOnly 逐字保存该日期,后端永不以
/// UTC 时间戳推导日期,导入亦不做任何时区换算 —— 上后端前后口径零变更。
///
/// 归属(票03/票14 口径):带 UserId,查询恒带归属谓词;(UserId, Date) 唯一。
/// </summary>
public sealed class ReviewActivityDay : AuditableAggregateRoot
{
    private ReviewActivityDay() { }

    public ReviewActivityDay(Guid userId, DateOnly date, int count)
    {
        UserId = userId;
        Date = date;
        Count = count;
    }

    public Guid UserId { get; private set; }

    /// <summary>用户本地日历日期(逐字保存,不做时区换算)。</summary>
    public DateOnly Date { get; private set; }

    /// <summary>当天复习次数。</summary>
    public int Count { get; private set; }

    /// <summary>记录一次复习:次数 +1。</summary>
    public void Increment()
    {
        Count++;
        Touch();
    }

    /// <summary>
    /// 导入合并:只升不降(取 max)。首次导入时后端为空,新行即原值逐字保真;
    /// 重复导入同值不虚增 —— 防「本地与后端双份」被求和放大(见 Handler 注释)。
    /// 返回是否发生了变化。
    /// </summary>
    public bool RaiseTo(int count)
    {
        if (count <= Count) return false;
        Count = count;
        Touch();
        return true;
    }
}
