using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Knowledge.Application;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Knowledge.Tests;

/// <summary>
/// 票54(ARC-08b):Knowledge 条目核心写路径 Handler 行为测试 —— 建/改/软删/
/// 恢复/复习(SM-2)/改级/关联/导入/分类改名与删除,经 Fake ICurrentUser
/// (TestCurrentUser)+ 真实持久化 seam 测公开行为,不测实现细节。
/// 与票92 分工:本文件只走 KnowledgeCommandHandlers,不碰其在途的
/// TechStackLocalDataHandlers/AddCustomCategory 面。
/// DB 门控同仓内先例(TechStackLocalDataTests):连不上库时跳过;
/// 每例独立随机 UserId 隔离,Dispose 清本例用户数据。
/// boundary 类别逐例标注;文件类不适用(本组 Handler 无文件/流输入)。
/// </summary>
public sealed class KnowledgeHandlerBehaviorTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";
    private bool _available;
    private readonly List<Guid> _users = [];

    public async Task InitializeAsync()
    {
        await using var db = NewDb(Guid.NewGuid());
        try { _available = await db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available || _users.Count == 0) return;
        await using var db = NewDb(_users[0]);
        var items = await db.Items.IgnoreQueryFilters().Where(i => _users.Contains(i.UserId)).ToListAsync();
        db.Items.RemoveRange(items);
        await db.SaveChangesAsync();
    }

    private static KnowledgeDbContext NewDb(Guid currentUser)
    {
        var opts = new DbContextOptionsBuilder<KnowledgeDbContext>().UseNpgsql(Cs).Options;
        return new KnowledgeDbContext(opts, new TestCurrentUser(currentUser));
    }

    private Guid NewUser() { var u = Guid.NewGuid(); _users.Add(u); return u; }

    private async Task<Guid> CreateAsync(Guid user, string title = "幂等是什么", string topic = "Backend",
        string source = "Personal", int difficulty = 3, int importance = 3, string? company = null)
    {
        await using var db = NewDb(user);
        var r = await new CreateKnowledgeCommandHandler(db).Handle(
            new CreateKnowledgeCommand(user, title, topic, null, "Q: " + title, source,
                difficulty, importance, SourceCompanyName: company), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<KnowledgeItemDto> GetAsync(Guid user, Guid id)
    {
        await using var db = NewDb(user);
        var r = await new GetKnowledgeItemQueryHandler(db, TimeProvider.System).Handle(
            new GetKnowledgeItemQuery(user, id), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    // ---------- Create ----------

    [Fact] // boundary: 0 与负数 / 超限 —— Difficulty/Importance 被领域钳制 1..5;新条目默认 New 且次日待复习
    public async Task Create_ClampsLevels_AndDefaults()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateAsync(user, difficulty: 0, importance: 99);
        var dto = await GetAsync(user, id);
        dto.Difficulty.Should().Be(1);
        dto.Importance.Should().Be(5);
        dto.Mastery.Should().Be("New");
        dto.Source.Should().Be("Personal");
        dto.NextReviewAt.Should().NotBeNull();
    }

    [Fact] // boundary: 无效枚举 —— Source 无法识别回落 Personal;合法值 FromInterview 解析
    public async Task Create_SourceFallback_AndKnown()
    {
        if (!_available) return;
        var user = NewUser();
        var bad = await CreateAsync(user, title: "t1", source: "Banana");
        (await GetAsync(user, bad)).Source.Should().Be("Personal");
        var good = await CreateAsync(user, title: "t2", source: "frominterview");
        (await GetAsync(user, good)).Source.Should().Be("FromInterview");
    }

    [Fact] // boundary: 特殊字符/Unicode —— 标题/分类去首尾空白逐字落库;仅公司名来源也链接(EntryId=Empty、Source 转 FromInterview)
    public async Task Create_UnicodeAndCompanyOnlySource()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateAsync(user, title: "  什么是 «eventual consistency» ☕  ", topic: " 分布式 ",
            company: "Acme 🎯");
        var dto = await GetAsync(user, id);
        dto.Title.Should().Be("什么是 «eventual consistency» ☕");
        dto.Topic.Should().Be("分布式");
        dto.SourceCompanyName.Should().Be("Acme 🎯");
        dto.SourceInterviewEntryId.Should().Be(Guid.Empty);
        dto.Source.Should().Be("FromInterview");
    }

    // ---------- Update / Delete / Restore ----------

    private static UpdateKnowledgeCommand Upd(Guid user, Guid id, string title = "改后标题")
        => new(user, id, title, "Backend", null, "改后题干?", 4, 2, null, null, null, null, null, null, null, null);

    [Fact] // boundary: 归属校验 —— 跨用户改/删均 NotFound 且数据不变;本人改字段落库
    public async Task UpdateDelete_CrossUserGuards()
    {
        if (!_available) return;
        var user = NewUser(); var other = NewUser();
        var id = await CreateAsync(user);
        await using (var db = NewDb(other))
        {
            var u = await new UpdateKnowledgeCommandHandler(db).Handle(Upd(other, id, "Hacked"), default);
            u.IsFailure.Should().BeTrue(); u.Error.Type.Should().Be(ErrorType.NotFound);
            var d = await new DeleteKnowledgeCommandHandler(db).Handle(new DeleteKnowledgeCommand(other, id), default);
            d.IsFailure.Should().BeTrue(); d.Error.Type.Should().Be(ErrorType.NotFound);
        }
        await using (var db = NewDb(user))
            (await new UpdateKnowledgeCommandHandler(db).Handle(Upd(user, id), default)).IsSuccess.Should().BeTrue();
        var dto = await GetAsync(user, id);
        dto.Title.Should().Be("改后标题");
        dto.Difficulty.Should().Be(4);
    }

    [Fact] // boundary: 状态流转 —— 软删后详情 NotFound 且列表不含;Restore 后恢复可见;跨用户 Restore NotFound
    public async Task Delete_Restore_VisibilityCycle()
    {
        if (!_available) return;
        var user = NewUser(); var other = NewUser();
        var id = await CreateAsync(user);
        await using (var db = NewDb(user))
            (await new DeleteKnowledgeCommandHandler(db).Handle(new DeleteKnowledgeCommand(user, id), default))
                .IsSuccess.Should().BeTrue();
        await using (var db = NewDb(user))
        {
            var g = await new GetKnowledgeItemQueryHandler(db, TimeProvider.System).Handle(
                new GetKnowledgeItemQuery(user, id), default);
            g.IsFailure.Should().BeTrue(); g.Error.Type.Should().Be(ErrorType.NotFound);
            var list = await new ListKnowledgeQueryHandler(db, TimeProvider.System).Handle(
                new ListKnowledgeQuery(user), default);
            list.Value.Items.Should().NotContain(i => i.Id == id);
        }
        await using (var db = NewDb(other))
        {
            var r = await new RestoreKnowledgeCommandHandler(db).Handle(new RestoreKnowledgeCommand(other, id), default);
            r.IsFailure.Should().BeTrue(); r.Error.Type.Should().Be(ErrorType.NotFound);
        }
        await using (var db = NewDb(user))
            (await new RestoreKnowledgeCommandHandler(db).Handle(new RestoreKnowledgeCommand(user, id), default))
                .IsSuccess.Should().BeTrue();
        (await GetAsync(user, id)).Id.Should().Be(id);
    }

    // ---------- RecordReview(SM-2) ----------

    private async Task<ReviewOutcomeDto> ReviewAsync(Guid user, Guid id, string result)
    {
        await using var db = NewDb(user);
        var r = await new RecordReviewCommandHandler(db, TimeProvider.System).Handle(
            new RecordReviewCommand(user, id, result), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    [Fact] // boundary: 无效枚举 —— Result 无法识别 → Validation 且不记复习
    public async Task RecordReview_InvalidResult_Fails()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateAsync(user);
        await using (var db = NewDb(user))
        {
            var r = await new RecordReviewCommandHandler(db, TimeProvider.System).Handle(
                new RecordReviewCommand(user, id, "Banana"), default);
            r.IsFailure.Should().BeTrue();
            r.Error.Code.Should().Be("Review.InvalidResult");
        }
        (await GetAsync(user, id)).ReviewCount.Should().Be(0);
    }

    [Fact] // boundary: 状态流转/SM-2 —— 首评 Good:连对 1、间隔 1 天、复习日志 +1
    public async Task RecordReview_FirstGood()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateAsync(user);
        var outcome = await ReviewAsync(user, id, "Good");
        outcome.RepetitionStreak.Should().Be(1);
        outcome.IntervalDays.Should().Be(1);
        var dto = await GetAsync(user, id);
        dto.ReviewCount.Should().Be(1);
        dto.ReviewLogs.Should().ContainSingle();
    }

    [Fact] // boundary: 状态流转/SM-2 —— 连对三次间隔 1→6→15 天;Learning 态自动升 Familiar
    public async Task RecordReview_ThreeGoods_IntervalProgressionAndAutoPromote()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateAsync(user);
        await using (var db = NewDb(user))
            (await new ChangeMasteryCommandHandler(db).Handle(new ChangeMasteryCommand(user, id, "Learning"), default))
                .IsSuccess.Should().BeTrue();
        (await ReviewAsync(user, id, "Good")).IntervalDays.Should().Be(1);
        (await ReviewAsync(user, id, "Good")).IntervalDays.Should().Be(6);
        var third = await ReviewAsync(user, id, "Good");
        third.IntervalDays.Should().Be(15); // round(6 × EF 2.5)
        third.RepetitionStreak.Should().Be(3);
        third.Mastery.Should().Be("Familiar");
    }

    [Fact] // boundary: 状态流转/SM-2 —— Mastered 答错(Again):降 NeedsReview、连对清零、EF 按公式下调、间隔回 1 天
    public async Task RecordReview_AgainOnMastered_Demotes()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateAsync(user);
        await using (var db = NewDb(user))
            (await new ChangeMasteryCommandHandler(db).Handle(new ChangeMasteryCommand(user, id, "Mastered"), default))
                .IsSuccess.Should().BeTrue();
        var outcome = await ReviewAsync(user, id, "Again");
        outcome.Mastery.Should().Be("NeedsReview");
        outcome.RepetitionStreak.Should().Be(0);
        outcome.IntervalDays.Should().Be(1);
        outcome.EasinessFactor.Should().Be(2.06); // 2.6(After Mastered +0.1) − 0.54(q=1 公式)
    }

    [Fact] // boundary: 归属校验 —— 跨用户复习 NotFound
    public async Task RecordReview_CrossUser_NotFound()
    {
        if (!_available) return;
        var user = NewUser(); var other = NewUser();
        var id = await CreateAsync(user);
        await using var db = NewDb(other);
        var r = await new RecordReviewCommandHandler(db, TimeProvider.System).Handle(
            new RecordReviewCommand(other, id, "Good"), default);
        r.IsFailure.Should().BeTrue(); r.Error.Type.Should().Be(ErrorType.NotFound);
    }

    // ---------- ChangeMastery ----------

    [Fact] // boundary: 无效枚举 → Validation;同级重复改级为 no-op(EF 不变);降级清连对且 EF −0.2
    public async Task ChangeMastery_Rules()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateAsync(user);
        await using (var db = NewDb(user))
        {
            var bad = await new ChangeMasteryCommandHandler(db).Handle(new ChangeMasteryCommand(user, id, "Guru"), default);
            bad.IsFailure.Should().BeTrue(); bad.Error.Code.Should().Be("Mastery.Invalid");
        }
        await ReviewAsync(user, id, "Good"); // 连对 1,EF 仍 2.5(q=4 增量 0)
        await using (var db = NewDb(user))
            (await new ChangeMasteryCommandHandler(db).Handle(new ChangeMasteryCommand(user, id, "Familiar"), default))
                .IsSuccess.Should().BeTrue();
        var promoted = await GetAsync(user, id);
        promoted.EasinessFactor.Should().Be(2.6); // 升 Familiar +0.1
        promoted.RepetitionStreak.Should().Be(1); // 升级不清连对
        await using (var db = NewDb(user))
            (await new ChangeMasteryCommandHandler(db).Handle(new ChangeMasteryCommand(user, id, "New"), default))
                .IsSuccess.Should().BeTrue();
        var dto = await GetAsync(user, id);
        dto.RepetitionStreak.Should().Be(0); // 降级清零
        dto.EasinessFactor.Should().Be(2.4); // 2.6 − 0.2
        await using (var db = NewDb(user))
            (await new ChangeMasteryCommandHandler(db).Handle(new ChangeMasteryCommand(user, id, "New"), default))
                .IsSuccess.Should().BeTrue();
        (await GetAsync(user, id)).EasinessFactor.Should().Be(2.4); // 同级 no-op,不再扣
    }

    // ---------- AttachRelation ----------

    [Fact] // boundary: 校验链 —— 自关联/目标不存在/目标属他人/无效类型逐项 Validation;合法关联落库且同型重复返回同一关系
    public async Task AttachRelation_ValidationChain_AndIdempotent()
    {
        if (!_available) return;
        var user = NewUser(); var other = NewUser();
        var a = await CreateAsync(user, title: "A 题");
        var b = await CreateAsync(user, title: "B 题");
        var foreign = await CreateAsync(other, title: "他人题");
        await using (var db = NewDb(user))
        {
            var self = await new AttachRelationCommandHandler(db).Handle(
                new AttachRelationCommand(user, a, a, "Prerequisite"), default);
            self.IsFailure.Should().BeTrue(); self.Error.Code.Should().Be("Relation.SelfReference");
            var ghost = await new AttachRelationCommandHandler(db).Handle(
                new AttachRelationCommand(user, a, Guid.NewGuid(), "Prerequisite"), default);
            ghost.IsFailure.Should().BeTrue(); ghost.Error.Code.Should().Be("Relation.TargetNotFound");
            var notMine = await new AttachRelationCommandHandler(db).Handle(
                new AttachRelationCommand(user, a, foreign, "Prerequisite"), default);
            notMine.IsFailure.Should().BeTrue(); notMine.Error.Code.Should().Be("Relation.TargetNotFound");
            var badType = await new AttachRelationCommandHandler(db).Handle(
                new AttachRelationCommand(user, a, b, "Banana"), default);
            badType.IsFailure.Should().BeTrue(); badType.Error.Code.Should().Be("Relation.InvalidType");
            var ok = await new AttachRelationCommandHandler(db).Handle(
                new AttachRelationCommand(user, a, b, "Prerequisite", "先学 B"), default);
            ok.IsSuccess.Should().BeTrue(ok.Error.Description);
            var dup = await new AttachRelationCommandHandler(db).Handle(
                new AttachRelationCommand(user, a, b, "Prerequisite"), default);
            dup.IsSuccess.Should().BeTrue();
            dup.Value.Should().Be(ok.Value); // 同型重复关联幂等返回既有关系
        }
        (await GetAsync(user, a)).Relations.Should().ContainSingle();
    }

    [Fact] // boundary: 归属校验 —— 跨用户对他人条目挂关联 NotFound
    public async Task AttachRelation_CrossUserSource_NotFound()
    {
        if (!_available) return;
        var user = NewUser(); var other = NewUser();
        var a = await CreateAsync(user, title: "源题");
        var b = await CreateAsync(other, title: "目标题");
        await using var db = NewDb(other);
        var r = await new AttachRelationCommandHandler(db).Handle(
            new AttachRelationCommand(other, a, b, "Compare"), default);
        r.IsFailure.Should().BeTrue(); r.Error.Type.Should().Be(ErrorType.NotFound);
    }

    // ---------- Import ----------

    private static ImportKnowledgeRow Row(string title, string topic = "Backend")
        => new(title, topic, "Q?");

    [Fact] // boundary: 空集合 —— 空导入 → Validation
    public async Task Import_Empty_Fails()
    {
        if (!_available) return;
        var user = NewUser();
        await using var db = NewDb(user);
        var r = await new ImportKnowledgeCommandHandler(db).Handle(new ImportKnowledgeCommand(user, []), default);
        r.IsFailure.Should().BeTrue(); r.Error.Code.Should().Be("Import.Empty");
    }

    [Fact] // boundary: 超长上限 +1 —— 501 条整批拒绝且零落库
    public async Task Import_501_FailsAtomically()
    {
        if (!_available) return;
        var user = NewUser();
        var rows = Enumerable.Range(1, 501).Select(i => Row("批量-" + i)).ToList();
        await using (var db = NewDb(user))
        {
            var r = await new ImportKnowledgeCommandHandler(db).Handle(new ImportKnowledgeCommand(user, rows), default);
            r.IsFailure.Should().BeTrue(); r.Error.Code.Should().Be("Import.TooMany");
        }
        await using (var db = NewDb(user))
        {
            var list = await new ListKnowledgeQueryHandler(db, TimeProvider.System).Handle(
                new ListKnowledgeQuery(user, PageSize: 1), default);
            list.Value.Total.Should().Be(0);
        }
    }

    [Fact] // boundary: 超长上限值本身 —— 500 条整批成功落库
    public async Task Import_500_Succeeds()
    {
        if (!_available) return;
        var user = NewUser();
        var rows = Enumerable.Range(1, 500).Select(i => Row("上限-" + i)).ToList();
        await using (var db = NewDb(user))
        {
            var r = await new ImportKnowledgeCommandHandler(db).Handle(new ImportKnowledgeCommand(user, rows), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
            r.Value.Should().Be(500);
        }
        await using (var db = NewDb(user))
        {
            var list = await new ListKnowledgeQueryHandler(db, TimeProvider.System).Handle(
                new ListKnowledgeQuery(user, PageSize: 1), default);
            list.Value.Total.Should().Be(500);
        }
    }

    [Fact] // boundary: 空行跳过 + 幂等 —— 空标题/空分类行跳过不整批失败;同批重导创建数为 0
    public async Task Import_SkipsBlankRows_AndIdempotent()
    {
        if (!_available) return;
        var user = NewUser();
        var rows = new List<ImportKnowledgeRow>
        {
            Row("有效一"), Row(""), Row("   "), new ImportKnowledgeRow("无分类题", "", "Q?"), Row("有效二"),
        };
        await using (var db = NewDb(user))
        {
            var r = await new ImportKnowledgeCommandHandler(db).Handle(new ImportKnowledgeCommand(user, rows), default);
            r.IsSuccess.Should().BeTrue();
            r.Value.Should().Be(2);
            var again = await new ImportKnowledgeCommandHandler(db).Handle(new ImportKnowledgeCommand(user, rows), default);
            again.IsSuccess.Should().BeTrue();
            again.Value.Should().Be(0); // 同 topic+title 幂等
        }
    }

    // ---------- RenameTopic / DeleteTopic ----------

    [Fact] // boundary: 空/null —— 改名新旧名任一为空白 → Validation;删分类名空白 → Validation
    public async Task TopicOps_BlankNames_Fail()
    {
        if (!_available) return;
        var user = NewUser();
        await using var db = NewDb(user);
        (await new RenameTopicCommandHandler(db).Handle(new RenameTopicCommand(user, "Backend", "  "), default))
            .Error.Code.Should().Be("Topic.Empty");
        (await new RenameTopicCommandHandler(db).Handle(new RenameTopicCommand(user, "", "New"), default))
            .Error.Code.Should().Be("Topic.Empty");
        (await new DeleteTopicCommandHandler(db).Handle(new DeleteTopicCommand(user, " "), default))
            .Error.Code.Should().Be("Topic.Empty");
    }

    [Fact] // boundary: 超长上限 ±1 —— 新分类名 101 字 → Validation 不落库;100 字成功(票83 口径)
    public async Task RenameTopic_LengthBoundary()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateAsync(user, topic: "OldTopic");
        await using (var db = NewDb(user))
        {
            var tooLong = await new RenameTopicCommandHandler(db).Handle(
                new RenameTopicCommand(user, "OldTopic", new string('x', 101)), default);
            tooLong.IsFailure.Should().BeTrue(); tooLong.Error.Code.Should().Be("Topic.TooLong");
            var atLimit = await new RenameTopicCommandHandler(db).Handle(
                new RenameTopicCommand(user, "OldTopic", new string('y', 100)), default);
            atLimit.IsSuccess.Should().BeTrue();
            atLimit.Value.Should().Be(1);
        }
        (await GetAsync(user, id)).Topic.Should().Be(new string('y', 100));
    }

    [Fact] // boundary: 状态流转 —— 改名大小写不敏感匹配全部条目并计数;同名(忽略大小写)no-op 返回 0;他人条目不动
    public async Task RenameTopic_ScopeAndNoOp()
    {
        if (!_available) return;
        var user = NewUser(); var other = NewUser();
        await CreateAsync(user, title: "一", topic: "MixedCase");
        await CreateAsync(user, title: "二", topic: "mixedcase");
        var foreign = await CreateAsync(other, title: "三", topic: "MixedCase");
        await using (var db = NewDb(user))
        {
            var noop = await new RenameTopicCommandHandler(db).Handle(
                new RenameTopicCommand(user, "MixedCase", "MIXEDCASE"), default);
            noop.IsSuccess.Should().BeTrue(); noop.Value.Should().Be(0);
            var r = await new RenameTopicCommandHandler(db).Handle(
                new RenameTopicCommand(user, "mixedCASE", "Renamed"), default);
            r.IsSuccess.Should().BeTrue(); r.Value.Should().Be(2);
        }
        (await GetAsync(other, foreign)).Topic.Should().Be("MixedCase");
    }

    [Fact] // boundary: 状态流转 —— 删分类把名下条目 Topic 置 Uncategorized 并计数;无匹配返回 0
    public async Task DeleteTopic_ClearsAndCounts()
    {
        if (!_available) return;
        var user = NewUser();
        var a = await CreateAsync(user, title: "甲", topic: "ToGo");
        var b = await CreateAsync(user, title: "乙", topic: "ToGo");
        await CreateAsync(user, title: "丙", topic: "Stay");
        await using (var db = NewDb(user))
        {
            var r = await new DeleteTopicCommandHandler(db).Handle(new DeleteTopicCommand(user, "togo"), default);
            r.IsSuccess.Should().BeTrue(); r.Value.Should().Be(2);
            var none = await new DeleteTopicCommandHandler(db).Handle(new DeleteTopicCommand(user, "Nope"), default);
            none.IsSuccess.Should().BeTrue(); none.Value.Should().Be(0);
        }
        (await GetAsync(user, a)).Topic.Should().Be("Uncategorized");
        (await GetAsync(user, b)).Topic.Should().Be("Uncategorized");
    }
}

/// <summary>
/// 票54:Knowledge 校验器 boundary(纯规则,无 DB)—— 长度上限 ±1、
/// Difficulty/Importance 区间 0/6、复习结果白名单。基线口径与
/// UpdateKnowledgeValidatorTests 既有特征一致,不改断言口径。
/// </summary>
public sealed class KnowledgeValidatorBoundaryTests
{
    private static bool HasError<T>(FluentValidation.AbstractValidator<T> v, T cmd, string property)
        => v.Validate(cmd).Errors.Any(e => e.PropertyName == property);

    private static CreateKnowledgeCommand Create(string title = "t", string topic = "Backend",
        string question = "q?", int difficulty = 3, int importance = 3)
        => new(Guid.NewGuid(), title, topic, null, question, "Personal", difficulty, importance);

    [Fact] // boundary: 超长上限 ±1 —— Title 500/501、Topic 100/101、Question 8000/8001
    public void Create_LengthBoundaries()
    {
        var v = new CreateKnowledgeCommandValidator();
        HasError(v, Create(title: new string('t', 500)), "Title").Should().BeFalse();
        HasError(v, Create(title: new string('t', 501)), "Title").Should().BeTrue();
        HasError(v, Create(topic: new string('t', 100)), "Topic").Should().BeFalse();
        HasError(v, Create(topic: new string('t', 101)), "Topic").Should().BeTrue();
        HasError(v, Create(question: new string('q', 8000)), "Question").Should().BeFalse();
        HasError(v, Create(question: new string('q', 8001)), "Question").Should().BeTrue();
    }

    [Theory] // boundary: 0 与负数 / 上限 +1 —— Difficulty 与 Importance 均 1..5
    [InlineData(0)] [InlineData(-1)] [InlineData(6)]
    public void Create_LevelOutOfRange_Rejected(int level)
    {
        var v = new CreateKnowledgeCommandValidator();
        HasError(v, Create(difficulty: level), "Difficulty").Should().BeTrue();
        HasError(v, Create(importance: level), "Importance").Should().BeTrue();
    }

    [Theory] // boundary: 区间端点 —— 1 与 5 放行
    [InlineData(1)] [InlineData(5)]
    public void Create_LevelEndpoints_Accepted(int level)
        => new CreateKnowledgeCommandValidator().Validate(Create(difficulty: level, importance: level))
            .IsValid.Should().BeTrue();

    [Fact] // boundary: 空/null —— Title/Topic/Question 空均拒绝
    public void Create_EmptyFields_Rejected()
    {
        var v = new CreateKnowledgeCommandValidator();
        HasError(v, Create(title: ""), "Title").Should().BeTrue();
        HasError(v, Create(topic: ""), "Topic").Should().BeTrue();
        HasError(v, Create(question: ""), "Question").Should().BeTrue();
    }

    [Theory] // boundary: 白名单 —— 复习结果仅 Again/Hard/Good/Easy(大小写不敏感)
    [InlineData("Again", true)] [InlineData("good", true)] [InlineData("EASY", true)]
    [InlineData("Perfect", false)] [InlineData("", false)]
    public void RecordReview_ResultWhitelist(string result, bool valid)
    {
        var cmd = new RecordReviewCommand(Guid.NewGuid(), Guid.NewGuid(), result);
        var errs = new RecordReviewCommandValidator().Validate(cmd).Errors;
        errs.Any(e => e.PropertyName == "Result").Should().Be(!valid);
    }

    [Fact] // boundary: 空 Guid —— RecordReview 的 Id 必填;RenameTopic 新名 100/101 与 Handler 守卫同口径
    public void Misc_Validators()
    {
        HasError(new RecordReviewCommandValidator(),
            new RecordReviewCommand(Guid.NewGuid(), Guid.Empty, "Good"), "Id").Should().BeTrue();
        var rv = new RenameTopicCommandValidator();
        HasError(rv, new RenameTopicCommand(Guid.NewGuid(), "a", new string('x', 100)), "NewName").Should().BeFalse();
        HasError(rv, new RenameTopicCommand(Guid.NewGuid(), "a", new string('x', 101)), "NewName").Should().BeTrue();
    }
}
