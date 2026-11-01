using FluentAssertions;
using Microsoft.Extensions.Options;
using YourInterview.Services.Interviews.Application;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票 309 §11 后端纯单测（不连库）：消毒器（1–4 的消毒器侧）与 serve pass（13–16）。
/// 夹具全合成（example.test / example 域名，无真实邮件）。
/// </summary>
public sealed class MailHtmlSanitizeServeTests : IDisposable
{
    private readonly string _root;

    public MailHtmlSanitizeServeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "t309-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* 忽略 */ }
    }

    private IOptions<MailboxOptions> Opts() =>
        Options.Create(new MailboxOptions { AttachmentRootDir = _root });

    private static Dictionary<string, MailHtmlSanitizer.CidMapEntry> Map(
        params (string Cid, string File, string Ct)[] entries) =>
        entries.ToDictionary(e => e.Cid,
            e => new MailHtmlSanitizer.CidMapEntry(e.File, e.Ct), StringComparer.Ordinal);

    private string WritePng(Guid assetId, string name, byte[] bytes)
    {
        var dir = Path.Combine(_root, assetId.ToString(), "attachments");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), bytes);
        return $"mail-attachments/{assetId}/attachments/{name}";
    }

    /// <summary>票 309 测试 2：&lt;style&gt; 块整体删除（元素 + 内容），无追踪 URL、无 CSS 文本泄漏。</summary>
    [Fact]
    public void StyleBlock_StrippedEntirely_NoTrackerNoLeakedCss()
    {
        var html = "<style>@import url(https://tracker.example/x); " +
            ".a{background:url(https://tracker.example/y)}</style><p>hello</p>";
        var stored = MailHtmlSanitizer.PrepareStoredHtml(html);
        stored.Should().NotBeNull();
        stored.Should().Contain("hello");
        stored.Should().NotContain("<style");
        stored.Should().NotContain("@import");
        stored.Should().NotContain("tracker.example");
        stored.Should().NotContain(".a{");
    }

    /// <summary>票 309 测试 3：内联 style 只留 §3 白名单属性（URL 型属性与非白名单属性同 dropped）。</summary>
    [Fact]
    public void InlineStyle_AllowlistReplacesDefault_UrlAndNonListedDropped()
    {
        var html = "<p style=\"background:url(https://tracker.example/x); color:red; " +
            "animation: spin 2s\">hi</p>";
        var stored = MailHtmlSanitizer.PrepareStoredHtml(html);
        stored.Should().NotBeNull();
        // color 留存（库把 red 归一为 rgba 形态）；background 的 URL 载荷一定没了。
        stored.Should().Contain("color");
        stored.Should().Contain("rgba(255, 0, 0, 1)");
        stored.Should().NotContain("url(");
        stored.Should().NotContain("tracker.example");
        stored.Should().NotContain("background-image");
        // 非白名单属性即使值中无 URL 也必须掉（证明 Clear() + 逐条加，非默认相减）。
        stored.Should().NotContain("animation");
        // 注：库对 background 简写做 longhand 分解后会留一个惰性的
        // background-color: initial（无 URL、透明），属 AngleSharp.Css 的简写展开
        // 填充，并非原 background 声明留存；serve Job 4 与客户端 hook 仍会拦截
        // 任何含 url( 的 style 值，故此处不断言 background 子串全无。
    }

    /// <summary>票 309 测试 4a：远端 .svg 入库即占位（含 query 串仍判 SVG）。</summary>
    [Fact]
    public void RemoteSvg_StoreTime_ReplacedWithPlaceholder()
    {
        // 注：夹具带正文——§2 判空规则是“去标签后仅剩空白即空”，纯图 HTML
        // 本就会判空存 NULL（见报告说明）；此处测的是 SVG→占位，故带正文。
        var html = "<p><img src=\"https://cdn.example/logo.svg?v=1\" alt=\"logo\">logo caption</p>";
        var stored = MailHtmlSanitizer.PrepareStoredHtml(html);
        stored.Should().NotBeNull();
        stored.Should().Contain(MailHtmlSanitizer.PlaceholderDataUri);
        stored.Should().NotContain("cdn.example");
        stored.Should().Contain("alt=\"logo\"");
    }

    /// <summary>票 309 测试 4b：cid: 映射到 SVG part → served 为占位图（非 /inline/ URL）。</summary>
    [Fact]
    public void CidMappedToSvg_ServedAsPlaceholder()
    {
        var assetId = Guid.NewGuid();
        var stored = "<p><img src=\"cid:logo@example\" alt=\"logo\"></p>";
        var map = Map(("logo@example", $"mail-attachments/{assetId}/attachments/logo.svg",
            "image/svg+xml"));
        var (html, _) = MailHtmlServePass.Apply(stored, assetId, map, false, Opts().Value);
        html.Should().Contain(MailHtmlSanitizer.PlaceholderDataUri);
        html.Should().NotContain("/inline/");
        html.Should().NotContain("cid:");
    }

    /// <summary>票 309 测试 13：CID 改写 + 零 cid: + 幂等 + 13(e) 缺失文件占位。</summary>
    [Fact]
    public void CidRewrite_MappedInline_UnmappedPlaceholder_Idempotent_MissingFilePlaceholder()
    {
        var assetId = Guid.NewGuid();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var file = WritePng(assetId, "pic.png", png);
        var stored = "<p><img src=\"cid:pic@example\"><img src=\"cid:gone@example\"></p>";
        var map = Map(("pic@example", file, "image/png"));
        var (html, _) = MailHtmlServePass.Apply(stored, assetId, map, false, Opts().Value);
        html.Should().Contain($"/api/interviews/mailbox/assets/{assetId}/inline/pic%40example");
        html.Should().Contain(MailHtmlSanitizer.PlaceholderDataUri);
        html.Should().NotContain("cid:");
        // 幂等：对输出再跑一遍，结果不变。
        var (again, _) = MailHtmlServePass.Apply(html, assetId, map, false, Opts().Value);
        again.Should().Be(html);

        // 13(e)：已映射但文件缺失 → 占位图，不发 /inline/ URL。
        var missingMap = Map(("pic@example", $"mail-attachments/{assetId}/attachments/nope.png",
            "image/png"));
        var (missingHtml, _) = MailHtmlServePass.Apply(
            "<p><img src=\"cid:pic@example\"></p>", assetId, missingMap, false, Opts().Value);
        missingHtml.Should().Contain(MailHtmlSanitizer.PlaceholderDataUri);
        missingHtml.Should().NotContain("/inline/");
    }

    /// <summary>票 309 测试 14：链接硬化（替换非合并：旧 target/rel 一定被覆盖）。</summary>
    [Fact]
    public void LinkHardening_ReplacesTargetAndRel()
    {
        var assetId = Guid.NewGuid();
        var stored = "<p><a href=\"https://example.com\" target=\"_self\" rel=\"author\">x</a></p>";
        var (html, _) = MailHtmlServePass.Apply(stored, assetId, Map(), false, Opts().Value);
        html.Should().Contain("target=\"_blank\"");
        html.Should().Contain("rel=\"noopener noreferrer\"");
        html.Should().NotContain("_self");
        html.Should().NotContain("author");
    }

    /// <summary>票 309 测试 15：默认拦截远端图 + X-Images-Blocked 信号；?images=1 放行光栅、SVG 仍占位。</summary>
    [Fact]
    public void ImagesBlocked_DefaultBlocks_AllowedKeepsRaster_SvgStillPlaceholder()
    {
        var assetId = Guid.NewGuid();
        var stored = "<p><img src=\"https://cdn.example/a.png\"></p>";
        var (blocked, blockedFlag) = MailHtmlServePass.Apply(stored, assetId, Map(), false, Opts().Value);
        blocked.Should().Contain(MailHtmlSanitizer.PlaceholderDataUri);
        blocked.Should().NotContain("cdn.example");
        blockedFlag.Should().BeTrue();

        var (allowed, allowedFlag) = MailHtmlServePass.Apply(stored, assetId, Map(), true, Opts().Value);
        allowed.Should().Contain("https://cdn.example/a.png");
        allowedFlag.Should().BeFalse();

        var svgStored = "<p><img src=\"https://cdn.example/b.svg\"></p>";
        var (svgAllowed, _) = MailHtmlServePass.Apply(svgStored, assetId, Map(), true, Opts().Value);
        svgAllowed.Should().Contain(MailHtmlSanitizer.PlaceholderDataUri);
        svgAllowed.Should().NotContain("cdn.example");
    }

    /// <summary>票 309 测试 16：serve 层纵深防御——入库 HTML 人为含 &lt;style&gt; 块， served 一定无。</summary>
    [Fact]
    public void ServePass_StripsStyleBlock_DefenseInDepth()
    {
        var assetId = Guid.NewGuid();
        var stored = "<style>.a{color:red}</style><p>hi</p>";
        var (html, _) = MailHtmlServePass.Apply(stored, assetId, Map(), false, Opts().Value);
        html.Should().Contain("hi");
        html.Should().NotContain("<style");
        html.Should().NotContain(".a{");
    }

    /// <summary>票 309 测试 3 补充：cid: 仅 img src 保留——href 里的 cid: 一定被 strip。</summary>
    [Fact]
    public void CidInHref_Stripped_CidInImgSrc_Preserved()
    {
        var html = "<p><a href=\"cid:pic@example\">x</a><img src=\"cid:pic@example\"></p>";
        var stored = MailHtmlSanitizer.PrepareStoredHtml(html);
        stored.Should().NotBeNull();
        stored.Should().NotContain("href=\"cid:");
        stored.Should().Contain("src=\"cid:pic@example\"");
    }
}
