using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.Services.Jobs.Domain;

/// <summary>
/// Tracker 收藏(COD-10a/票14)—— 用户对某条投递的收藏标记。
///
/// 为什么独立成表而不是给 JobApplication 加 IsFavorite 列:
///   收藏是「用户 ↔ 投递」的关系数据,不是投递自身的属性;独立表让收藏的
///   增删不触碰投递聚合(不 bump 投递的 UpdatedAt、不参与投递的并发令牌),
///   也为后续 Playbook/Tech Stack 两面(COD-10 的 15/16 票)留同一口径:
///   收藏 = (UserId, 目标 Id) 一行,API 形状一致。
///
/// 归属(票03 口径):带 UserId,查询恒带归属谓词;(UserId, ApplicationId) 唯一。
/// </summary>
public sealed class ApplicationFavorite : AuditableAggregateRoot
{
    private ApplicationFavorite() { }

    public ApplicationFavorite(Guid userId, Guid applicationId)
    {
        UserId = userId;
        ApplicationId = applicationId;
    }

    public Guid UserId { get; private set; }
    public Guid ApplicationId { get; private set; }
}

/// <summary>
/// 按投递自备问题(COD-10a/票14)—— 用户为某条投递准备的自定义面试问题。
///
/// 此前只存浏览器 localStorage(yi-tracker-questions-{id}),换设备/清缓存即丢,
/// 且与 Playbook 轮次问题、Tech Stack 完全不通。本实体是 Jobs/Tracker 面的
/// 落点;与 Playbook/Tech Stack 的互通不在本票,但 DTO 形状(id/applicationId/
/// text/sortOrder)与后续两面保持一致口径。
///
/// 归属(票03 口径):带 UserId(写入时从所属投递盖章,与沟通记录同口径),
/// 查询恒带归属谓词。
/// </summary>
public sealed class ApplicationQuestion : AuditableAggregateRoot
{
    private ApplicationQuestion() { }

    public ApplicationQuestion(Guid userId, Guid applicationId, string text, int sortOrder)
    {
        UserId = userId;
        ApplicationId = applicationId;
        Text = text;
        SortOrder = sortOrder;
    }

    public Guid UserId { get; private set; }
    public Guid ApplicationId { get; private set; }

    /// <summary>问题正文(用户原文,Trim 后存)。</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>同一投递内的展示顺序(追加时取当前 max+1)。</summary>
    public int SortOrder { get; private set; }
}
