using YourInterview.Services.Assessment.Application;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票109:SaveMaterialTreeCommand 此前无校验器,素材 Name 只有 EF 硬限(300)
/// —— 超限穿透 DB 回 500。新校验器只补 Name 长度且整树递归:
/// 逐路断言超限(+1)拒绝且文案为票107 定版长度文案、整值放行;
/// 守恒断言:Content 在 EF 是 text(无上限),超长正文放行。
/// </summary>
public sealed class SaveMaterialTreeValidatorTests
{
    private static readonly Guid User = Guid.NewGuid();

    private static MaterialNodeIn File(string name, string? content = null)
        => new(null, name, false, content, 0, true, null);

    private static MaterialNodeIn Folder(string name, params MaterialNodeIn[] children)
        => new(null, name, true, null, 0, true, children);

    private static SaveMaterialTreeCommand Cmd(params MaterialNodeIn[] nodes)
        => new(User, nodes);

    private static List<FluentValidation.Results.ValidationFailure> Errors(
        SaveMaterialTreeCommand cmd)
        => new SaveMaterialTreeCommandValidator().Validate(cmd).Errors.ToList();

    [Fact]
    public void ValidTree_Passes()
        => Assert.Empty(Errors(Cmd(File("notes"), Folder("dir", File("child")))));

    [Fact] // boundary: 上限 +1(根节点)
    public void RootName_301_Rejected_WithLengthMessage()
    {
        var e = Assert.Single(Errors(Cmd(File(new string('n', 301)))),
            x => x.PropertyName == "Nodes[0].Name");
        Assert.Equal("素材名长度不能超过 300 个字符", e.ErrorMessage);
    }

    [Fact] // boundary: 上限整值(根节点)
    public void RootName_300_Accepted()
        => Assert.Empty(Errors(Cmd(File(new string('n', 300)))));

    [Fact] // 整树递归:嵌套子节点同样受检(错误键带路径)
    public void ChildName_301_Rejected()
    {
        var e = Assert.Single(Errors(Cmd(Folder("dir", File(new string('n', 301))))),
            x => x.PropertyName == "Nodes[0].Children[0].Name");
        Assert.Equal("素材名长度不能超过 300 个字符", e.ErrorMessage);
    }

    [Fact] // 整树递归:孙节点
    public void GrandchildName_301_Rejected()
        => Assert.Contains(Errors(Cmd(Folder("a", Folder("b", File(new string('n', 301)))))),
            x => x.PropertyName == "Nodes[0].Children[0].Children[0].Name");

    [Fact]
    public void ChildName_300_Accepted()
        => Assert.Empty(Errors(Cmd(Folder("dir", File(new string('n', 300))))));

    [Fact] // 守恒:Content 无上限(EF text),超长正文不得被误杀
    public void LongContent_Accepted()
        => Assert.Empty(Errors(Cmd(File("notes", new string('c', 100000)))));

    [Fact] // 守恒:空树合法(Handler 的整树覆盖/清空路径)
    public void EmptyTree_Accepted() => Assert.Empty(Errors(Cmd()));
}
