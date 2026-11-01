using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Ganss.Xss;

namespace YourInterview.Services.Interviews.Application;

/// <summary>
/// 票 309：邮件 HTML 入库消毒（Ganss HtmlSanitizer 9.2.1039，精确 pin，见
/// src/Directory.Packages.props）+ §2 存储规则（512KB cap、消毒为空判空）+
/// cidMap 归一化/解析。单例静态实例：配置一次、永不变更（库要求线程安全用法）。
///
/// §3 规则要点（实现时逐条对齐计划）：
/// - 禁止标签/属性从库默认白名单中 Remove（Remove 对本就不在者是 no-op）；
/// - 链接 scheme 只放 https:/mailto:（cid:/data: 的 href 由 PostProcessNode 再砍）；
/// - img src 只放 https:/cid:/data:image/{png,jpeg,gif,webp}，https + .svg 路径
///   在入库时即换成占位图（T2）；其它 scheme/缺失 src 由 serve 层 Job 2 兜底成占位图；
/// - 内联 style 只放 §3 显式 URL-free 属性表：AllowedCssProperties.Clear() 后
///   逐条加，绝不从库默认派生（fail-closed；AllowCssCustomProperties 保持关闭，
///   var() 无法把 URL 偷运进允许属性）。
/// - 值层 url()/@import 拦截不在入库层做（计划只要求 serve Job 4 + 客户端 hook），
///   入库层仅靠属性表排除 URL 型属性。
///
/// §17(b) 决议：计划 §5 称 HtmlSanitizer 9.x 依赖 AngleSharp 0.17.x，但
/// 9.2.1039 的 nuspec 实际依赖是 AngleSharp 1.7.2 / AngleSharp.Css 1.0.2
/// （以 nuspec 为准）。serve 层直接引用 AngleSharp 1.7.2（精确 pin），
/// 与消毒器同属一个解析器大版本，无解析差分。
/// </summary>
public static class MailHtmlSanitizer
{
    /// <summary>§5 唯一占位图常量：1×1 透明 PNG data URI（SVG、未映射 CID、
    /// 被拦截远端图、非法 scheme 图共用；/inline/ 缺失文件回落的字节亦由此派生）。</summary>
    public const string PlaceholderDataUri =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8Xw8AAwMCAO+ip1sAAAAASUVORK5CYII=";

    private const string PlaceholderPrefix = "data:image/png;base64,";

    /// <summary>占位图字节（由唯一常量派生，无第二常量、无单独占位文件）。</summary>
    public static readonly byte[] PlaceholderBytes =
        Convert.FromBase64String(PlaceholderDataUri[PlaceholderPrefix.Length..]);

    /// <summary>§2 cap：消毒后超过 512KB 不入库（按 UTF-8 字节计；截断 HTML 永不入库）。</summary>
    public const int MaxHtmlBytes = 512 * 1024;

    /// <summary>§3 显式 URL-free 内联 style 属性表（逐条列出，Clear() 后只加这些）。</summary>
    public static readonly string[] AllowedCssProperties =
    [
        // Box/layout
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "width", "height", "min-width", "min-height", "max-width", "max-height",
        "display", "float", "clear", "overflow", "overflow-x", "overflow-y",
        "visibility", "box-sizing", "table-layout", "border-collapse",
        "border-spacing", "empty-cells", "caption-side", "vertical-align",
        // Border（非图片 longhand + width/style/color 简写；border-image 等一律不在）
        "border", "border-top", "border-right", "border-bottom", "border-left",
        "border-width", "border-top-width", "border-right-width",
        "border-bottom-width", "border-left-width",
        "border-style", "border-top-style", "border-right-style",
        "border-bottom-style", "border-left-style",
        "border-color", "border-top-color", "border-right-color",
        "border-bottom-color", "border-left-color",
        "border-radius", "border-top-left-radius", "border-top-right-radius",
        "border-bottom-right-radius", "border-bottom-left-radius",
        "outline", "outline-width", "outline-style", "outline-color", "outline-offset",
        // Text/font/color
        "color", "background-color", "font", "font-family", "font-size",
        "font-style", "font-weight", "font-variant", "line-height",
        "letter-spacing", "word-spacing", "text-align", "text-decoration",
        "text-decoration-line", "text-decoration-color", "text-decoration-style",
        "text-decoration-thickness", "text-transform", "text-indent",
        "white-space", "word-break", "overflow-wrap", "text-overflow",
        "direction", "unicode-bidi",
        // Position
        "position", "top", "right", "bottom", "left", "z-index",
        // List（仅 type + position；list-style 简写可夹带 list-style-image，故排除）
        "list-style-type", "list-style-position",
        // Flex/grid spacing（无 URL 值）
        "flex", "flex-direction", "flex-wrap", "flex-flow", "flex-grow",
        "flex-shrink", "flex-basis", "justify-content", "align-items",
        "align-self", "align-content", "gap", "row-gap", "column-gap",
        "order", "opacity",
    ];

    private static readonly string[] ForbiddenTags =
    [
        "script", "iframe", "frame", "frameset", "object", "embed", "applet",
        "link", "meta", "base", "form", "input", "button", "select", "textarea",
        "option", "svg", "math", "video", "audio", "source", "track", "picture",
        "canvas", "dialog", "style",
    ];

    private static readonly string[] ForbiddenAttributes =
        ["srcset", "formaction", "ping", "autofocus", "poster", "background"];

    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    private static HtmlSanitizer CreateSanitizer()
    {
        var s = new HtmlSanitizer();
        foreach (var tag in ForbiddenTags) s.AllowedTags.Remove(tag);
        foreach (var attr in ForbiddenAttributes) s.AllowedAttributes.Remove(attr);
        s.AllowedSchemes.Clear();
        s.AllowedSchemes.Add("https");
        s.AllowedSchemes.Add("mailto");
        s.AllowedSchemes.Add("cid");
        s.AllowedSchemes.Add("data");
        s.AllowedCssProperties.Clear();
        foreach (var prop in AllowedCssProperties) s.AllowedCssProperties.Add(prop);
        // AllowCssCustomProperties 保持默认关闭：var() 无法把 URL 偷运进允许属性。
        s.PostProcessNode += EnforceUrlRules;
        return s;
    }

    /// <summary>
    /// PostProcessNode：库的全局 scheme 表无法表达“cid: 仅 img src、data: 仅光栅图”
    /// 这类分属性规则，故在已解析 DOM 上二次收紧（属性值此时已是实体解码后形态，
    /// 满足“entity decoding 后检查”的要求）。
    /// </summary>
    private static void EnforceUrlRules(object? sender, PostProcessNodeEventArgs e)
    {
        if (e.Node is not IElement el) return;
        // on* 兜底（库默认已 strip，此处是第二道）。
        foreach (var attr in el.Attributes.ToList())
        {
            if (attr.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                el.RemoveAttribute(attr.Name);
        }
        if (el.HasAttribute("href") && !IsHttpOrMailto(el.GetAttribute("href")))
            el.RemoveAttribute("href");
        if (el.HasAttribute("cite") && !IsHttpOrMailto(el.GetAttribute("cite")))
            el.RemoveAttribute("cite");
        if (!el.HasAttribute("src")) return;
        var tag = el.TagName.ToLowerInvariant();
        var src = el.GetAttribute("src") ?? string.Empty;
        if (tag != "img")
        {
            el.RemoveAttribute("src");
            return;
        }
        if (IsRemoteSvg(src))
        {
            // T2：远端 SVG 入库即占位（即使将来 Load images 也不放行 SVG）。
            el.SetAttribute("src", PlaceholderDataUri);
        }
        else if (!IsAllowedImgSrc(src))
        {
            // 其它非法 scheme/空 src：删属性，serve 层 Job 2 会补占位图。
            el.RemoveAttribute("src");
        }
    }

    /// <summary>消毒（fragment 输入；若输入是整文档，仅返回 body 部分）。</summary>
    public static string Sanitize(string html) => Sanitizer.Sanitize(html);

    /// <summary>
    /// §2 入库口径：消毒 → 消毒为空判空 → 超 512KB 判空；三者任一成立返回 null
    ///（调用方存 NULL，走纯文本回落；截断 HTML 永不入库）。
    /// </summary>
    public static string? PrepareStoredHtml(string? rawHtml)
    {
        if (string.IsNullOrEmpty(rawHtml)) return null;
        var clean = Sanitize(rawHtml);
        if (IsEmpty(clean)) return null;
        if (Encoding.UTF8.GetByteCount(clean) > MaxHtmlBytes) return null;
        return clean;
    }

    /// <summary>§2/§8 共用判空规则：去全部标签后仅剩空白（或空串）即为空。
    /// 实体先解码（&amp;nbsp; 之类解码后也是空白），与客户端同规则。</summary>
    public static bool IsEmpty(string html)
    {
        var text = Regex.Replace(html, "<[^>]*>", string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        return string.IsNullOrWhiteSpace(text);
    }

    /// <summary>§2 CID 归一化：去首尾空白、去一对尖括号、再 trim、小写（查表用）。</summary>
    public static string? NormalizeCid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cid = raw.Trim();
        if (cid.Length >= 2 && cid[0] == '<' && cid[^1] == '>')
            cid = cid[1..^1].Trim();
        if (cid.Length == 0) return null;
        return cid.ToLowerInvariant();
    }

    /// <summary>cidMap 条目（与 §2 JSON 形态一一对应）。</summary>
    public sealed record CidMapEntry(string File, string ContentType);

    /// <summary>从资产 meta JSON 解析 cidMap（键归一化；非法形态返回空表，不抛）。</summary>
    public static Dictionary<string, CidMapEntry> ParseCidMap(string? metaJson)
    {
        var map = new Dictionary<string, CidMapEntry>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(metaJson)) return map;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metaJson);
            if (!doc.RootElement.TryGetProperty("cidMap", out var cidMap)
                || cidMap.ValueKind != System.Text.Json.JsonValueKind.Object)
                return map;
            foreach (var prop in cidMap.EnumerateObject())
            {
                var key = NormalizeCid(prop.Name);
                if (key is null) continue;
                if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var file = prop.Value.TryGetProperty("file", out var f) ? f.GetString() : null;
                var ct = prop.Value.TryGetProperty("contentType", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(ct)) continue;
                map[key] = new CidMapEntry(file, ct);
            }
        }
        catch (System.Text.Json.JsonException) { /* 非法 meta 不阻断渲染 */ }
        return map;
    }

    internal static bool IsHttpOrMailto(string? url)
    {
        var scheme = GetScheme(url);
        return scheme is "https" or "mailto";
    }

    internal static bool IsAllowedImgSrc(string? src)
    {
        var scheme = GetScheme(src);
        return scheme switch
        {
            "https" => true,
            "cid" => true,
            "data" => IsRasterDataUri(src),
            _ => false,
        };
    }

    /// <summary>远端 SVG 启发式：https: URL 路径以 .svg 结尾（大小写不敏感，忽略 query）。</summary>
    internal static bool IsRemoteSvg(string? src)
    {
        if (GetScheme(src) != "https") return false;
        return Uri.TryCreate(src, UriKind.Absolute, out var u)
            && u.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsRasterDataUri(string? src)
    {
        if (src is null) return false;
        return Regex.IsMatch(src, @"^data:image/(png|jpeg|gif|webp)[;,]", RegexOptions.IgnoreCase);
    }

    /// <summary>取 URL scheme（冒号须出现在首个 / ? # 之前；无 scheme 返回 null）。</summary>
    internal static string? GetScheme(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var s = url.Trim();
        var colon = s.IndexOf(':');
        if (colon <= 0) return null;
        for (var i = 0; i < colon; i++)
        {
            if (s[i] is '/' or '?' or '#') return null;
        }
        return s[..colon].ToLowerInvariant();
    }
}
