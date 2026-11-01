using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using static YourInterview.Services.Interviews.Application.MailHtmlSanitizer;

namespace YourInterview.Services.Interviews.Application;

/// <summary>
/// 票 309 §5：入库 HTML 永不原文直出。GET /html 每次对入库 HTML 跑一遍
/// 幂等 serve pass（AngleSharp 1.7.2，与消毒器同解析器大版本，见
/// MailHtmlSanitizer 的 §17(b) 决议），顺序 Jobs 0–4：
///
/// Job 0：<style> 元素连内容整体删除（入库层已 strip，此处是纵深防御）。
/// Job 1：cid: → /inline/ 绝对路径（已映射且类型在 /inline/ 白名单内且
///   文件存在）或占位图（未映射、非图片/SVG 类型、文件缺失、路径逃逸）；
///   跑完后 served HTML 不含任何 cid: 子串。类型检查是测试 11-4 的要求：
///   映射到 SVG/非图片 part 的 CID 若给了 /inline/ URL，浏览器请求必 404
///   成破图，违背“永无无样式破图”标准，故与缺失文件同等处理为占位图。
/// Job 2：默认拦截远端图（https: → 占位图，记 X-Images-Blocked）；
///   ?images=1 时 https: 光栅保留、https .svg 仍占位；data: 光栅与
///   Job 1 产出的 /inline/ 绝对路径永不拦截；缺失/空/非法 scheme → 占位图。
/// Job 3：全部 <a> 强制 target=_blank rel=noopener noreferrer（替换非合并）。
/// Job 4：style 属性值含 url( 或 @import（大小写不敏感）→ 整个属性删除。
/// </summary>
public static class MailHtmlServePass
{
    /// <summary>§6 /inline/ 仅放行图片类型（SVG 永不放行）。</summary>
    public static readonly string[] InlineContentTypes =
        ["image/png", "image/jpeg", "image/gif", "image/webp"];

    public static bool IsInlineContentType(string? contentType) =>
        contentType is not null
        && InlineContentTypes.Contains(contentType.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// §6 check 4 通用解析：cidMap 的 file 值是存储基相对路径
    ///（mail-attachments/{assetId}/attachments/{name}）。解析时：
    /// - 默认布局（AttachmentRootDir 未设）：基 = INTERVIEW_STORAGE_DIR 或
    ///   /app/storage/interviews，file 全值拼接后即写盘位置；
    /// - 测试布局（AttachmentRootDir 设为临时根）：该根本身就是
    ///   mail-attachments 目录，故先剥掉 file 值的 mail-attachments/ 前缀再拼接，
    ///   落点与写盘位置一致。两种布局下结果与写盘位置相同，计划 §6 的
    ///   base+file 规则在默认布局下逐字成立。
    /// 逃逸（.. 或绝对路径跑出根）返回 null → 调用方按 404（/inline/）或
    /// 占位图（Job 1）处理，绝不把 file 值当裸路径用。
    /// </summary>
    public static string? ResolveInlinePath(MailboxOptions opts, string file)
    {
        var root = MailboxImportHelper.ResolveRoot(opts);
        var relative = file.StartsWith("mail-attachments/", StringComparison.OrdinalIgnoreCase)
            ? file["mail-attachments/".Length..]
            : file;
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        return full;
    }

    /// <param name="imagesAllowed">对应 ?images=1（Job 2 对 https: 光栅放行）。</param>
    /// <returns>（serve 后 HTML，是否拦截了至少一张远端图 → X-Images-Blocked）。</returns>
    public static (string Html, bool ImagesBlocked) Apply(
        string storedHtml, Guid assetId, IReadOnlyDictionary<string, CidMapEntry> cidMap,
        bool imagesAllowed, MailboxOptions opts)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(storedHtml);
        var body = doc.Body;
        if (body is null) return (storedHtml, false);

        // Job 0：<style> 连内容整体删除。
        foreach (var style in body.QuerySelectorAll("style").ToList())
            style.Parent?.RemoveChild(style);

        // Job 1：CID 改写（映射 + 文件存在才给 /inline/ URL，否则占位图）。
        foreach (var img in body.QuerySelectorAll("img").ToList())
        {
            var src = img.GetAttribute("src");
            if (src is null || !src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                continue;
            var cid = NormalizeCid(src["cid:".Length..]);
            string next;
            if (cid is not null && cidMap.TryGetValue(cid, out var entry)
                && IsInlineContentType(entry.ContentType)
                && ResolveInlinePath(opts, entry.File) is { } full && File.Exists(full))
            {
                next = $"/api/interviews/mailbox/assets/{assetId}/inline/{Uri.EscapeDataString(cid)}";
            }
            else
            {
                next = PlaceholderDataUri;
            }
            img.SetAttribute("src", next);
        }

        // Job 2：远端图默认拦截（仅默认模式下 https: 替换计入 X-Images-Blocked）。
        var blocked = false;
        foreach (var img in body.QuerySelectorAll("img").ToList())
        {
            var src = img.GetAttribute("src");
            if (string.IsNullOrWhiteSpace(src))
            {
                img.SetAttribute("src", PlaceholderDataUri);
                continue;
            }
            var scheme = GetScheme(src);
            if (scheme == "https")
            {
                if (!imagesAllowed)
                {
                    img.SetAttribute("src", PlaceholderDataUri);
                    blocked = true;
                }
                else if (IsRemoteSvg(src))
                {
                    // ?images=1 下远端 SVG 仍占位（入库层已换，此处是纵深防御）。
                    img.SetAttribute("src", PlaceholderDataUri);
                }
            }
            else if (scheme == "data")
            {
                if (!IsRasterDataUri(src)) img.SetAttribute("src", PlaceholderDataUri);
            }
            else if (scheme is null && src.StartsWith("/api/interviews/mailbox/assets/",
                         StringComparison.Ordinal))
            {
                // Job 1 产出的同源 /inline/ 绝对路径：保留。
            }
            else
            {
                // http:、残留 cid:、相对路径、其它非法 scheme → 占位图。
                img.SetAttribute("src", PlaceholderDataUri);
            }
        }

        // Job 3：链接硬化（替换非合并，保证 noopener 一定存在）。
        foreach (var a in body.QuerySelectorAll("a").ToList())
        {
            a.SetAttribute("target", "_blank");
            a.SetAttribute("rel", "noopener noreferrer");
        }

        // Job 4：style 属性值层防御（整属性删除，非逐声明）。
        foreach (var el in body.QuerySelectorAll("*").ToList())
        {
            var style = el.GetAttribute("style");
            if (style is null) continue;
            if (style.Contains("url(", StringComparison.OrdinalIgnoreCase)
                || style.Contains("@import", StringComparison.OrdinalIgnoreCase))
                el.RemoveAttribute("style");
        }

        return (body.InnerHtml, !imagesAllowed && blocked);
    }
}
