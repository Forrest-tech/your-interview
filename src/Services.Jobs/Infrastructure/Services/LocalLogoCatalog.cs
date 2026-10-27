using System.Text.Json;
using YourInterview.Services.Jobs.Application;

namespace YourInterview.Services.Jobs.Infrastructure.Services;

// ============================================================================
//  票 249 R1:本地开源 logo 层 —— $0、离线、无 key。
//
//  数据来源(见 Infrastructure/LogoData/NOTICE.md,均为 CC0):
//  Simple Icons(data/simple-icons.json + icons/*.svg,打包为两个 JSON)与
//  gilbarbara/logos(logos.json + logos/*.svg,打包为两个 JSON),以
//  EmbeddedResource 随程序集发布,运行时懒加载一次。
//
//  查找:公司名 → TitleToSlug → 查表。Simple Icons 查表键为
//  titleToSlug(title) 与 titleToSlug(aka),值一律为文件的真实 slug
//  (数据里带显式 slug 的图标,如 F#→fsharp,以显式 slug 为准);
//  gilbarbara 查表键为 shortname 与 titleToSlug(name),值为文件名
//  (优先 *-icon.svg,其次首文件);TryResolve 返回查表键本身(无点号,
//  可直接进 URL),TryGetSvg 与端点再由键解析文件名。
//  找不到返回 null —— 调用方继续走下一环,绝不抛。
// ============================================================================

public interface ILocalLogoCatalog
{
    /// <summary>按公司名查本地层:命中返回 (provider, key),否则 null。</summary>
    (string Provider, string Key)? TryResolve(string companyName);

    /// <summary>按 (provider, key) 取 SVG 文本:命中返回 SVG,否则 null。</summary>
    string? TryGetSvg(string provider, string key);
}

public sealed class LocalLogoCatalog : ILocalLogoCatalog
{
    public const string SimpleIconsProvider = "simple-icons";
    public const string LogosProvider = "logos";

    /// <summary>前端存储的 R1 logo 地址形态:相对路径,走与 /api/jobs 相同的网关前缀。</summary>
    public static string LocalLogoUrl(string provider, string key)
        => $"/api/jobs/companies/local-logos/{provider}/{key}";

    private readonly Lazy<CatalogData> _data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public (string Provider, string Key)? TryResolve(string companyName)
    {
        if (string.IsNullOrWhiteSpace(companyName)) return null;
        var slug = CompanyLogoRules.TitleToSlug(companyName);
        if (slug.Length == 0) return null;
        var data = _data.Value;
        if (data.SimpleIconsLookup.TryGetValue(slug, out var siFile))
            return (SimpleIconsProvider, siFile);
        // gilbarbara:返回查表键本身(shortname 或标题派生 slug,无点号、可直接
        // 进 URL),文件名解析留给 TryGetSvg/端点 —— 若直接返回文件名
        // (如 adobe-icon.svg),IsSafeKey 会因点号拒绝,整层取图 404。
        if (data.LogosLookup.TryGetValue(slug, out _))
            return (LogosProvider, slug);
        return null;
    }

    public string? TryGetSvg(string provider, string key)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(key)) return null;
        if (!IsSafeKey(key)) return null;
        var data = _data.Value;
        if (provider == SimpleIconsProvider)
            return data.SimpleIconsSvgs.TryGetValue(key, out var si) ? si : null;
        if (provider == LogosProvider)
        {
            if (!data.LogosLookup.TryGetValue(key, out var file)) return null;
            return data.LogosSvgs.TryGetValue(file, out var gb) ? gb : null;
        }
        return null;
    }

    /// <summary>端点 key 白名单:小写字母数字 + -_ ,首字符须为字母数字(禁 ../ 与点号)。</summary>
    public static bool IsSafeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (!char.IsLetterOrDigit(key[0])) return false;
        foreach (var ch in key)
        {
            var ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_';
            if (!ok) return false;
        }
        return true;
    }

    private sealed record CatalogData(
        Dictionary<string, string> SimpleIconsLookup,
        Dictionary<string, string> SimpleIconsSvgs,
        Dictionary<string, string> LogosLookup,
        Dictionary<string, string> LogosSvgs);

    private static CatalogData Load()
    {
        var asm = typeof(LocalLogoCatalog).Assembly;
        string Read(string suffix)
        {
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (name is null) return string.Empty;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) return string.Empty;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        var siLookup = new Dictionary<string, string>(StringComparer.Ordinal);
        var siSvgs = new Dictionary<string, string>(StringComparer.Ordinal);
        var gbLookup = new Dictionary<string, string>(StringComparer.Ordinal);
        var gbSvgs = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var siIndex = Read("simple-icons-index.json");
            if (siIndex.Length > 0)
            {
                using var doc = JsonDocument.Parse(siIndex);
                foreach (var icon in doc.RootElement.EnumerateArray())
                {
                    var title = icon.TryGetProperty("title", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    var fileSlug = icon.TryGetProperty("slug", out var s) && !string.IsNullOrWhiteSpace(s.GetString())
                        ? s.GetString()!
                        : CompanyLogoRules.TitleToSlug(title);
                    var titleSlug = CompanyLogoRules.TitleToSlug(title);
                    if (titleSlug.Length > 0) siLookup.TryAdd(titleSlug, fileSlug);
                    if (icon.TryGetProperty("aliases", out var aliases)
                        && aliases.TryGetProperty("aka", out var aka)
                        && aka.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in aka.EnumerateArray())
                        {
                            var akaSlug = CompanyLogoRules.TitleToSlug(a.GetString());
                            if (akaSlug.Length > 0) siLookup.TryAdd(akaSlug, fileSlug);
                        }
                    }
                }
            }
            var siSvgJson = Read("simple-icons-svgs.json");
            if (siSvgJson.Length > 0)
            {
                using var doc = JsonDocument.Parse(siSvgJson);
                foreach (var kv in doc.RootElement.EnumerateObject())
                    siSvgs[kv.Name] = kv.Value.GetString() ?? string.Empty;
            }
            var gbIndex = Read("gilbarbara-index.json");
            if (gbIndex.Length > 0)
            {
                using var doc = JsonDocument.Parse(gbIndex);
                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    var shortname = entry.TryGetProperty("shortname", out var sn) ? sn.GetString() : null;
                    var name = entry.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                    string? file = null;
                    if (entry.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                    {
                        string? first = null;
                        foreach (var f in files.EnumerateArray())
                        {
                            var fn = f.GetString();
                            if (string.IsNullOrWhiteSpace(fn)) continue;
                            first ??= fn;
                            if (fn.EndsWith("-icon.svg", StringComparison.OrdinalIgnoreCase)) { file = fn; break; }
                        }
                        file ??= first;
                    }
                    if (string.IsNullOrWhiteSpace(shortname) || string.IsNullOrWhiteSpace(file)) continue;
                    gbLookup.TryAdd(shortname, file);
                    var nameSlug = CompanyLogoRules.TitleToSlug(name);
                    if (nameSlug.Length > 0) gbLookup.TryAdd(nameSlug, file);
                }
            }
            var gbSvgJson = Read("gilbarbara-svgs.json");
            if (gbSvgJson.Length > 0)
            {
                using var doc = JsonDocument.Parse(gbSvgJson);
                foreach (var kv in doc.RootElement.EnumerateObject())
                    gbSvgs[kv.Name] = kv.Value.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // 数据损坏时本地层整体不可用,调用方继续走下一环,绝不中断回填。
            return new CatalogData(siLookup, siSvgs, gbLookup, gbSvgs);
        }
        return new CatalogData(siLookup, siSvgs, gbLookup, gbSvgs);
    }
}
