using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票15(COD-10b)boundary 补测(总纲第七节):Playbook 本地数据三个导入/保存
/// 校验器(纯函数,无需 DB)。每例标注 boundary 类别:
///   空/null、空集合、超长上限±1、特殊字符/Unicode、Guid.Empty、计数上限。
/// 不适用类别(本票 API 无日期/时间入参 —— CreatedAt 服务端生成;无文件入参):
/// 日期时区临界、文件类(0 字节/损坏/超限)不设用例,理由见交付报告。
/// </summary>
public sealed class PlaybookLocalDataBoundaryTests
{
    private static StarStoryInput Story(
        string clientId = "s1", string title = "T", string? situation = null,
        string? tags = null)
        => new(clientId, title, situation, null, null, null, tags);

    // ---------------- Upsert 校验 ----------------

    [Fact] // boundary: 空/null —— clientId 空
    public void Upsert_EmptyClientId_Rejected()
    {
        var v = new UpsertStarStoryCommandValidator();
        v.Validate(new UpsertStarStoryCommand(Guid.NewGuid(), Story(clientId: "")))
            .IsValid.Should().BeFalse();
    }

    [Fact] // boundary: 空 —— 标题空/纯空白由 NotEmpty 收口(空串)
    public void Upsert_EmptyTitle_Rejected()
    {
        var v = new UpsertStarStoryCommandValidator();
        v.Validate(new UpsertStarStoryCommand(Guid.NewGuid(), Story(title: "")))
            .IsValid.Should().BeFalse();
    }

    [Fact] // boundary: 超长上限±1 —— 标题 500 放行 / 501 拒
    public void Upsert_TitleLength_Boundary()
    {
        var v = new UpsertStarStoryCommandValidator();
        v.Validate(new UpsertStarStoryCommand(Guid.NewGuid(),
            Story(title: new string('x', StarStoryLimits.TitleMax)))).IsValid.Should().BeTrue();
        v.Validate(new UpsertStarStoryCommand(Guid.NewGuid(),
            Story(title: new string('x', StarStoryLimits.TitleMax + 1)))).IsValid.Should().BeFalse();
    }

    [Fact] // boundary: 超长上限±1 —— S 字段 20000 放行 / 20001 拒
    public void Upsert_FieldLength_Boundary()
    {
        var v = new UpsertStarStoryCommandValidator();
        v.Validate(new UpsertStarStoryCommand(Guid.NewGuid(),
            Story(situation: new string('s', StarStoryLimits.FieldMax)))).IsValid.Should().BeTrue();
        v.Validate(new UpsertStarStoryCommand(Guid.NewGuid(),
            Story(situation: new string('s', StarStoryLimits.FieldMax + 1)))).IsValid.Should().BeFalse();
    }

    [Fact] // boundary: 超长上限±1 —— tags 1000/1001;clientId 100/101
    public void Upsert_TagsAndClientIdLength_Boundary()
    {
        var v = new UpsertStarStoryCommandValidator();
        v.Validate(new UpsertStarStoryCommand(Guid.NewGuid(),
            Story(tags: new string('t', StarStoryLimits.TagsMax)))).IsValid.Should().BeTrue();
        v.Validate(new UpsertStarStoryCommand(Guid.NewGuid(),
            Story(tags: new string('t', StarStoryLimits.TagsMax + 1)))).IsValid.Should().BeFalse();
        v.Validate(new UpsertStarStoryCommand(Guid.NewGuid(),
            Story(clientId: new string('c', StarStoryLimits.ClientIdMax)))).IsValid.Should().BeTrue();
        v.Validate(new UpsertStarStoryCommand(Guid.NewGuid(),
            Story(clientId: new string('c', StarStoryLimits.ClientIdMax + 1)))).IsValid.Should().BeFalse();
    }

    [Fact] // boundary: Unicode/特殊字符 —— 中文+emoji+引号的合法故事放行
    public void Upsert_UnicodeAndSpecialChars_Accepted()
    {
        var v = new UpsertStarStoryCommandValidator();
        var s = new StarStoryInput("s🎯", "迁移故事 \"DealTrax\" <b>&amp;</b>",
            "情境:跨国团队 🎯", "任务", "行动", "结果", "迁移,.NET,中文标签");
        v.Validate(new UpsertStarStoryCommand(Guid.NewGuid(), s)).IsValid.Should().BeTrue();
    }

    [Fact] // boundary: null 字段 —— S/T/A/R/tags 全 null 是合法的半成品故事
    public void Upsert_NullOptionalFields_Accepted()
    {
        var v = new UpsertStarStoryCommandValidator();
        v.Validate(new UpsertStarStoryCommand(Guid.NewGuid(),
            new StarStoryInput("s1", "只有标题", null, null, null, null, null)))
            .IsValid.Should().BeTrue();
    }

    // ---------------- 故事导入校验 ----------------

    [Fact] // boundary: null 列表 —— NotNull 拒,且 Must 谓词 null-safe 不抛 NRE(票14 教训)
    public void ImportStories_NullList_RejectedWithoutThrowing()
    {
        var v = new ImportStarStoriesCommandValidator();
        var act = () => v.Validate(new ImportStarStoriesCommand(Guid.NewGuid(), null!));
        act.Should().NotThrow();
        act().IsValid.Should().BeFalse();
    }

    [Fact] // boundary: 空集合 —— 空导入合法(imported=0 的对账路径)
    public void ImportStories_EmptyList_Accepted()
    {
        var v = new ImportStarStoriesCommandValidator();
        v.Validate(new ImportStarStoriesCommand(Guid.NewGuid(), [])).IsValid.Should().BeTrue();
    }

    [Fact] // boundary: 计数上限±1 —— 1000 放行 / 1001 拒
    public void ImportStories_BatchCount_Boundary()
    {
        var v = new ImportStarStoriesCommandValidator();
        var atMax = Enumerable.Range(0, StarStoryLimits.ImportBatchMax)
            .Select(i => Story(clientId: "s" + i)).ToList();
        v.Validate(new ImportStarStoriesCommand(Guid.NewGuid(), atMax)).IsValid.Should().BeTrue();
        var overMax = Enumerable.Range(0, StarStoryLimits.ImportBatchMax + 1)
            .Select(i => Story(clientId: "s" + i)).ToList();
        v.Validate(new ImportStarStoriesCommand(Guid.NewGuid(), overMax)).IsValid.Should().BeFalse();
    }

    [Fact] // boundary: 批内单项超长 —— 任一项字段超限整批拒(校验层把关,不落库 500)
    public void ImportStories_ItemOverLimit_Rejected()
    {
        var v = new ImportStarStoriesCommandValidator();
        var batch = new[] { Story(), Story(clientId: "s2", title: new string('x', 501)) };
        v.Validate(new ImportStarStoriesCommand(Guid.NewGuid(), batch)).IsValid.Should().BeFalse();
    }

    // ---------------- 复盘导入校验 ----------------

    [Fact] // boundary: Guid.Empty —— 条目 id 为空 Guid 拒
    public void ImportDebriefs_EmptyEntryId_Rejected()
    {
        var v = new ImportLegacyDebriefsCommandValidator();
        v.Validate(new ImportLegacyDebriefsCommand(Guid.NewGuid(),
            [new LegacyDebriefImportItem(Guid.Empty, "{}")])).IsValid.Should().BeFalse();
    }

    [Fact] // boundary: 空 —— payload 空串拒(NotEmpty)
    public void ImportDebriefs_EmptyPayload_Rejected()
    {
        var v = new ImportLegacyDebriefsCommandValidator();
        v.Validate(new ImportLegacyDebriefsCommand(Guid.NewGuid(),
            [new LegacyDebriefImportItem(Guid.NewGuid(), "")])).IsValid.Should().BeFalse();
    }

    [Fact] // boundary: 超长上限±1 —— payload 100000 放行 / 100001 拒
    public void ImportDebriefs_PayloadLength_Boundary()
    {
        var v = new ImportLegacyDebriefsCommandValidator();
        v.Validate(new ImportLegacyDebriefsCommand(Guid.NewGuid(),
            [new LegacyDebriefImportItem(Guid.NewGuid(), new string('p', LegacyImportLimits.PayloadMax))]))
            .IsValid.Should().BeTrue();
        v.Validate(new ImportLegacyDebriefsCommand(Guid.NewGuid(),
            [new LegacyDebriefImportItem(Guid.NewGuid(), new string('p', LegacyImportLimits.PayloadMax + 1))]))
            .IsValid.Should().BeFalse();
    }

    [Fact] // boundary: null 列表 + 计数上限 —— null-safe;5000 放行 / 5001 拒
    public void ImportDebriefs_NullAndBatchCount_Boundary()
    {
        var v = new ImportLegacyDebriefsCommandValidator();
        var act = () => v.Validate(new ImportLegacyDebriefsCommand(Guid.NewGuid(), null!));
        act.Should().NotThrow();
        act().IsValid.Should().BeFalse();

        var atMax = Enumerable.Range(0, LegacyImportLimits.BatchMax)
            .Select(_ => new LegacyDebriefImportItem(Guid.NewGuid(), "{}")).ToList();
        v.Validate(new ImportLegacyDebriefsCommand(Guid.NewGuid(), atMax)).IsValid.Should().BeTrue();
        var overMax = Enumerable.Range(0, LegacyImportLimits.BatchMax + 1)
            .Select(_ => new LegacyDebriefImportItem(Guid.NewGuid(), "{}")).ToList();
        v.Validate(new ImportLegacyDebriefsCommand(Guid.NewGuid(), overMax)).IsValid.Should().BeFalse();
    }

    [Fact] // boundary: Unicode —— 中文/emoji 的原始 JSON 逐字放行
    public void ImportDebriefs_UnicodePayload_Accepted()
    {
        var v = new ImportLegacyDebriefsCommandValidator();
        v.Validate(new ImportLegacyDebriefsCommand(Guid.NewGuid(),
            [new LegacyDebriefImportItem(Guid.NewGuid(), "{\"stuck\":\"卡点🎯 <script>\"}")]))
            .IsValid.Should().BeTrue();
    }

    // ---------------- 粘贴材料导入校验 ----------------

    [Fact] // boundary: Guid.Empty + 空内容
    public void ImportPasted_EmptyEntryIdAndContent_Rejected()
    {
        var v = new ImportLegacyPastedCommandValidator();
        v.Validate(new ImportLegacyPastedCommand(Guid.NewGuid(),
            [new LegacyPastedImportItem(Guid.Empty, "x")])).IsValid.Should().BeFalse();
        v.Validate(new ImportLegacyPastedCommand(Guid.NewGuid(),
            [new LegacyPastedImportItem(Guid.NewGuid(), "")])).IsValid.Should().BeFalse();
    }

    [Fact] // boundary: 超长上限±1 —— 内容 500000 放行 / 500001 拒
    public void ImportPasted_ContentLength_Boundary()
    {
        var v = new ImportLegacyPastedCommandValidator();
        v.Validate(new ImportLegacyPastedCommand(Guid.NewGuid(),
            [new LegacyPastedImportItem(Guid.NewGuid(), new string('c', LegacyImportLimits.ContentMax))]))
            .IsValid.Should().BeTrue();
        v.Validate(new ImportLegacyPastedCommand(Guid.NewGuid(),
            [new LegacyPastedImportItem(Guid.NewGuid(), new string('c', LegacyImportLimits.ContentMax + 1))]))
            .IsValid.Should().BeFalse();
    }

    [Fact] // boundary: null 列表 null-safe + 空集合放行
    public void ImportPasted_NullAndEmpty_Behavior()
    {
        var v = new ImportLegacyPastedCommandValidator();
        var act = () => v.Validate(new ImportLegacyPastedCommand(Guid.NewGuid(), null!));
        act.Should().NotThrow();
        act().IsValid.Should().BeFalse();
        v.Validate(new ImportLegacyPastedCommand(Guid.NewGuid(), [])).IsValid.Should().BeTrue();
    }

    [Fact] // boundary: 删除校验 —— clientId 空拒、合法放行
    public void DeleteStory_ClientId_Boundary()
    {
        var v = new DeleteStarStoryCommandValidator();
        v.Validate(new DeleteStarStoryCommand(Guid.NewGuid(), "")).IsValid.Should().BeFalse();
        v.Validate(new DeleteStarStoryCommand(Guid.NewGuid(), "s123")).IsValid.Should().BeTrue();
    }
}
