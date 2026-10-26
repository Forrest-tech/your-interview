using YourInterview.Services.Interviews.Application;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票107:校验器 WithMessage 文案拆分的回归钉(口径同 Jobs 侧同名测试)。
/// WithMessage 只挂紧邻的前一条规则:旧写法把必填文案挂在 MaximumLength 上,
/// 超限回「必填」、空值回英文默认。修后两路各归其位:B 组(CompanyName/Title)
/// 原合并句「必填且不超过 N 字」拆开 —— 必填半句逐字保留,长度半句改
/// 「…长度不能超过 {MaxLength} 个字符」。本组只测校验器(纯规则,无 DB)。
/// </summary>
public sealed class ValidatorMessageSplitTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static string S(char c, int n) => new(c, n);

    private static string[] ErrorsFor(FluentValidation.Results.ValidationResult r, string property)
        => r.Errors.Where(e => e.PropertyName == property).Select(e => e.ErrorMessage).ToArray();

    private static string[] ErrorsContaining(FluentValidation.Results.ValidationResult r, string fragment)
        => r.Errors.Where(e => e.PropertyName.Contains(fragment)).Select(e => e.ErrorMessage).ToArray();

    // ---------- CreateEntry.CompanyName(300,B 组)/ Role(300,A 组) ----------

    private static CreateEntryCommand Entry(string company = "Acme", string role = "Dev")
        => new(User, null, company, role);

    [Fact]
    public void EntryCompanyName_TwoWays()
    {
        var v = new CreateEntryCommandValidator();
        Assert.Equal(new[] { "公司名不能为空" }, ErrorsFor(v.Validate(Entry(company: "")), "CompanyName"));
        Assert.Equal(new[] { "公司名长度不能超过 300 个字符" },
            ErrorsFor(v.Validate(Entry(company: S('公', 301))), "CompanyName"));
    }

    [Fact]
    public void EntryRole_TwoWays()
    {
        var v = new CreateEntryCommandValidator();
        Assert.Equal(new[] { "岗位名不能为空" }, ErrorsFor(v.Validate(Entry(role: "")), "Role"));
        Assert.Equal(new[] { "岗位名长度不能超过 300 个字符" },
            ErrorsFor(v.Validate(Entry(role: S('岗', 301))), "Role"));
    }

    // ---------- CreateAction.Title(500,B 组) ----------

    [Fact]
    public void ActionTitle_TwoWays()
    {
        var v = new CreateActionCommandValidator();
        Assert.Equal(new[] { "待办标题不能为空" },
            ErrorsFor(v.Validate(new CreateActionCommand(User, Guid.NewGuid(), "")), "Title"));
        Assert.Equal(new[] { "待办标题长度不能超过 500 个字符" },
            ErrorsFor(v.Validate(new CreateActionCommand(User, Guid.NewGuid(), S('待', 501))), "Title"));
    }

    // ---------- UploadAssetFile.FileName(500) ----------

    private static UploadAssetFileCommand Upload(string fileName) => new(
        User, Guid.NewGuid(), fileName, null, null, null, Stream.Null);

    [Fact]
    public void UploadFileName_TwoWays()
    {
        var v = new UploadAssetFileCommandValidator();
        Assert.Equal(new[] { "请选择要上传的录音文件" }, ErrorsFor(v.Validate(Upload("")), "FileName"));
        Assert.Equal(new[] { "文件名长度不能超过 500 个字符" },
            ErrorsFor(v.Validate(Upload(S('f', 501))), "FileName"));
    }

    // ---------- UpsertStarStory.Story.ClientId(100)/ Title(500) ----------

    private static UpsertStarStoryCommand Story(string clientId = "s1", string title = "t") => new(
        User, new StarStoryInput(clientId, title, null, null, null, null, null));

    [Fact]
    public void StarStory_ClientIdAndTitle_TwoWays()
    {
        var v = new UpsertStarStoryCommandValidator();
        Assert.Equal(new[] { "故事 id 必填" }, ErrorsFor(v.Validate(Story(clientId: "")), "Story.ClientId"));
        Assert.Equal(new[] { "故事 id 长度不能超过 100 个字符" },
            ErrorsFor(v.Validate(Story(clientId: S('s', 101))), "Story.ClientId"));
        Assert.Equal(new[] { "故事标题必填" }, ErrorsFor(v.Validate(Story(title: "")), "Story.Title"));
        Assert.Equal(new[] { "故事标题长度不能超过 500 个字符" },
            ErrorsFor(v.Validate(Story(title: S('题', 501))), "Story.Title"));
    }

    // ---------- ImportLegacyDebriefs / Pasted.EntryId(500,ChildRules) ----------

    [Fact]
    public void LegacyDebriefEntryId_TwoWays()
    {
        var v = new ImportLegacyDebriefsCommandValidator();
        Assert.Equal(new[] { "复盘必须带归档键" }, ErrorsContaining(
            v.Validate(new ImportLegacyDebriefsCommand(User,
                new[] { new LegacyDebriefImportItem("", "{}") })), "EntryId"));
        Assert.Equal(new[] { "归档键长度不能超过 500 个字符" }, ErrorsContaining(
            v.Validate(new ImportLegacyDebriefsCommand(User,
                new[] { new LegacyDebriefImportItem(S('k', 501), "{}") })), "EntryId"));
    }

    [Fact]
    public void LegacyPastedEntryId_TwoWays()
    {
        var v = new ImportLegacyPastedCommandValidator();
        Assert.Equal(new[] { "粘贴材料必须带归档键" }, ErrorsContaining(
            v.Validate(new ImportLegacyPastedCommand(User,
                new[] { new LegacyPastedImportItem("", "c") })), "EntryId"));
        Assert.Equal(new[] { "归档键长度不能超过 500 个字符" }, ErrorsContaining(
            v.Validate(new ImportLegacyPastedCommand(User,
                new[] { new LegacyPastedImportItem(S('k', 501), "c") })), "EntryId"));
    }
}
