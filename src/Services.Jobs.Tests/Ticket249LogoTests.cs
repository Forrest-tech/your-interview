using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YourInterview.Services.Jobs.Application;
using YourInterview.Services.Jobs.Infrastructure.Services;

namespace YourInterview.Services.Jobs.Tests;

// ============================================================================
//  票 249 单测(纯函数 + 伪造 HTTP,无真实网络、无数据库)。
//  覆盖:TitleToSlug 边界、registrable-domain、重定向判定矩阵、图片尺寸
//  嗅探、PickBestLogo、Wikidata 平局规则、抓取器重定向/默认图标关、
//  本地目录真实内嵌资源。Handler 级(需数据库)不在此列:测试项目无
//  InMemory/SQLite 包、现有套件亦为纯函数/WAF + 模拟上下文,链序接线
//  靠构建 + 代码评审验证(见票 249 报告)。
// ============================================================================

public sealed class Ticket249LogoTests
{
    // ---------------- TitleToSlug ----------------

    [Theory]
    [InlineData("C++", "cplusplus")]
    [InlineData(".NET", "dotnet")]
    [InlineData("AT&T", "atandt")]
    [InlineData("H&M", "handm")]
    [InlineData("Škoda", "skoda")]
    [InlineData("Müller", "muller")]
    [InlineData("Łódź", "lodz")]
    [InlineData("Café", "cafe")]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("São Paulo", "saopaulo")]
    [InlineData("Žilina", "zilina")]
    // æ 在上游 NFD 下不分解、被 /[^a-z\d]/ 剔除,故此处同样丢掉(不是 ae):
    [InlineData("Æsop", "sop")]
    [InlineData("F#", "f")]
    [InlineData("Node.js", "nodedotjs")]
    [InlineData("Ferrari N.V.", "ferrarindotvdot")]
    [InlineData("Backstage", "backstage")]
    [InlineData("  Intuit  ", "intuit")]
    [InlineData("", "")]
    public void TitleToSlug_MatchesUpstreamSdk(string title, string expected)
    {
        // F#→f 说明:数据文件须以显式 slug(fsharp)建索引,光靠标题派生不够;
        // Ferrari N.V.→ferrarindotvdot 说明:点号替换是全局的(与 Node.js
        // →nodedotjs 同规则),不是只换首尾点。
        Assert.Equal(expected, CompanyLogoRules.TitleToSlug(title));
    }

    // ---------------- Registrable domain ----------------

    [Theory]
    [InlineData("www.intuit.com", "intuit.com")]
    [InlineData("intuit.com", "intuit.com")]
    [InlineData("a.b.co.uk", "b.co.uk")]
    [InlineData("co.uk", "co.uk")]
    [InlineData("WWW.Intuit.COM.", "intuit.com")]
    [InlineData("192.0.2.1", "192.0.2.1")]
    [InlineData("localhost", "localhost")]
    public void GetRegistrableDomain_Cases(string host, string expected)
    {
        Assert.Equal(expected, CompanyLogoRules.GetRegistrableDomain(host));
    }

    [Fact]
    public void GetRegistrableDomain_EmptyReturnsNull()
    {
        Assert.Null(CompanyLogoRules.GetRegistrableDomain(""));
        Assert.Null(CompanyLogoRules.GetRegistrableDomain("   "));
    }

    // ---------------- Redirect decision matrix ----------------

    [Theory]
    [InlineData("https://intuit.com/", "https://www.intuit.com/", "https://www.intuit.com/")]
    [InlineData("https://www.intuit.com/a", "https://www.intuit.com/b", "https://www.intuit.com/b")]
    [InlineData("https://intuit.com/", "/favicon.ico", "https://intuit.com/favicon.ico")]
    [InlineData("http://intuit.com/", "https://intuit.com/", "https://intuit.com/")]
    [InlineData("https://www.google.com/s2/favicons?sz=128&domain=x.com", "https://t1.gstatic.com/faviconV2?x", "https://t1.gstatic.com/faviconV2?x")]
    [InlineData("https://www.google.com/s2/favicons?sz=128&domain=x.com", "https://t2.gstatic.com/faviconV2?x", "https://t2.gstatic.com/faviconV2?x")]
    public void TryResolveRedirectUri_Allowed(string current, string location, string expected)
    {
        Assert.Equal(expected, CompanyLogoRules.TryResolveRedirectUri(current, location));
    }

    [Theory]
    [InlineData("https://intuit.com/", "https://evil.com/")]
    [InlineData("https://intuit.com/", "https://intuit.evil.com/")]
    [InlineData("https://intuit.com/", "https://evil-intuit.com/")]
    [InlineData("https://www.google.com/s2/favicons?sz=128&domain=x.com", "https://evil-t1.gstatic.com/x")]
    [InlineData("https://www.google.com/s2/favicons?sz=128&domain=x.com", "https://t1.gstatic.com.evil.com/x")]
    [InlineData("https://www.google.com/other", "https://t1.gstatic.com/x")]
    [InlineData("https://intuit.com/", "https://t1.gstatic.com/x")]
    [InlineData("https://intuit.com/", "ftp://intuit.com/x")]
    [InlineData("https://intuit.com/", "")]
    [InlineData("https://intuit.com/", "javascript:alert(1)")]
    [InlineData("not-a-url", "https://intuit.com/")]
    public void TryResolveRedirectUri_Refused(string current, string location)
    {
        Assert.Null(CompanyLogoRules.TryResolveRedirectUri(current, location));
    }

    // ---------------- SSRF host check ----------------

    [Fact]
    public async Task IsSafeHostAsync_PrivateAndLoopbackBlocked_PublicAllowed()
    {
        Task<IPAddress[]> Public(string h) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
        Task<IPAddress[]> Private(string h) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") });
        Task<IPAddress[]> Loopback(string h) => Task.FromResult(new[] { IPAddress.Parse("127.0.0.1") });
        Task<IPAddress[]> LinkLocal(string h) => Task.FromResult(new[] { IPAddress.Parse("169.254.10.20") });
        Task<IPAddress[]> Mixed(string h) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.5") });

        Assert.True(await CompanyLogoRules.IsSafeHostAsync("example.com", CancellationToken.None, Public));
        Assert.False(await CompanyLogoRules.IsSafeHostAsync("internal.local", CancellationToken.None, Private));
        Assert.False(await CompanyLogoRules.IsSafeHostAsync("localhost", CancellationToken.None, Loopback));
        Assert.False(await CompanyLogoRules.IsSafeHostAsync("metadata", CancellationToken.None, LinkLocal));
        Assert.False(await CompanyLogoRules.IsSafeHostAsync("mixed.example", CancellationToken.None, Mixed));
        Assert.False(await CompanyLogoRules.IsSafeHostAsync("127.0.0.1", CancellationToken.None, null));
        Assert.False(await CompanyLogoRules.IsSafeHostAsync("10.1.2.3", CancellationToken.None, null));
        Assert.False(await CompanyLogoRules.IsSafeHostAsync("", CancellationToken.None, Public));
    }

    // ---------------- Image dimensions ----------------

    [Fact]
    public void TryGetImageDimensions_Formats()
    {
        var png128 = Png(128, 128);
        var (isSvg, w, h) = CompanyLogoRules.TryGetImageDimensions(png128, "image/png");
        Assert.False(isSvg);
        Assert.Equal(128, w);
        Assert.Equal(128, h);

        var png16 = Png(16, 16);
        (_, w, h) = CompanyLogoRules.TryGetImageDimensions(png16, "image/png");
        Assert.Equal(16, w);

        // GIF:宽高小端 uint16 @6/@8。
        var gif = new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 64, 0, 48, 0, 0, 0, 0 };
        (_, w, h) = CompanyLogoRules.TryGetImageDimensions(gif, "image/gif");
        Assert.Equal(64, w);
        Assert.Equal(48, h);

        // ICO:首条目宽/高字节,0 表示 256。
        var ico = new byte[] { 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        (_, w, h) = CompanyLogoRules.TryGetImageDimensions(ico, "image/x-icon");
        Assert.Equal(256, w);
        Assert.Equal(256, h);

        // SVG:媒体类型即判,不读体。
        (isSvg, _, _) = CompanyLogoRules.TryGetImageDimensions(Array.Empty<byte>(), "image/svg+xml");
        Assert.True(isSvg);
        // SVG:体嗅探(前 1KB 内有 <svg)。
        (isSvg, _, _) = CompanyLogoRules.TryGetImageDimensions(
            Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><svg xmlns=\"x\"></svg>"), "application/octet-stream");
        Assert.True(isSvg);

        // 未知格式 → 未知尺寸(不抛)。
        (_, w, h) = CompanyLogoRules.TryGetImageDimensions(new byte[] { 1, 2, 3 }, "image/png");
        Assert.Null(w);
        Assert.Null(h);
    }

    [Fact]
    public void TryGetImageDimensions_JpegSof()
    {
        // 最小 JPEG:SOI + SOF0(高 48、宽 64)。
        var jpeg = new byte[]
        {
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x30, 0x00, 0x40, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
            0xFF, 0xD9,
        };
        var (_, w, h) = CompanyLogoRules.TryGetImageDimensions(jpeg, "image/jpeg");
        Assert.Equal(64, w);
        Assert.Equal(48, h);
    }

    // ---------------- PickBestLogo ----------------

    [Fact]
    public void PickBestLogo_QualityOrder()
    {
        CompanyLogoRules.LogoCandidate Cand(string url, bool svg, int? w) =>
            new(url, svg, w, w);
        var png16 = Cand("https://x/16.png", false, 16);
        var png128 = Cand("https://x/128.png", false, 128);
        var svg = Cand("https://x/logo.svg", true, null);
        var unknown = Cand("https://x/u.png", false, null);

        Assert.Equal(svg, CompanyLogoRules.PickBestLogo([png128, svg, png16]));
        Assert.Equal(png128, CompanyLogoRules.PickBestLogo([png16, png128]));
        Assert.Equal(png16, CompanyLogoRules.PickBestLogo([png16]));
        Assert.Equal(unknown, CompanyLogoRules.PickBestLogo([unknown]));
        Assert.Null(CompanyLogoRules.PickBestLogo([]));
        // 软化门:16px 唯一资产保留,不落回首字母。
        Assert.Equal(png16, CompanyLogoRules.PickBestLogo([png16, unknown]));
    }

    // ---------------- Wikidata resolver ----------------

    [Fact]
    public async Task Wikidata_TieMeansNoPersist()
    {
        var search = """
            {"search":[{"id":"Q1","label":"Acme"},{"id":"Q2","label":"Acme"}]}
            """;
        var entities = """
            {"entities":{
              "Q1":{"claims":{"P856":[{"mainsnak":{"datavalue":{"value":"https://acme-one.example/"}}}]}},
              "Q2":{"claims":{"P856":[{"mainsnak":{"datavalue":{"value":"https://acme-two.example/"}}}]}}}}
            """;
        var resolver = WikidataWith(search, entities);
        var (domain, ambiguous) = await resolver.TryResolveDomainAsync("Acme", CancellationToken.None);
        Assert.Null(domain);
        Assert.True(ambiguous);
    }

    [Fact]
    public async Task Wikidata_SingleExactMatchReturnsDomain()
    {
        var search = """
            {"search":[{"id":"Q123","label":"Intuit"},{"id":"Q999","label":"Intuition"}]}
            """;
        var entities = """
            {"entities":{"Q123":{"claims":{"P856":[{"mainsnak":{"datavalue":{"value":"https://www.intuit.com/"}}}]}}}}
            """;
        var resolver = WikidataWith(search, entities);
        var (domain, ambiguous) = await resolver.TryResolveDomainAsync("Intuit", CancellationToken.None);
        Assert.Equal("www.intuit.com", domain);
        Assert.False(ambiguous);
    }

    [Fact]
    public async Task Wikidata_NoExactMatchOrNoP856ReturnsNull()
    {
        var search = """{"search":[{"id":"Q999","label":"Intuition"}]}""";
        var entities = """{"entities":{}}""";
        var resolver = WikidataWith(search, entities);
        var (domain, ambiguous) = await resolver.TryResolveDomainAsync("Intuit", CancellationToken.None);
        Assert.Null(domain);
        Assert.False(ambiguous);

        var search2 = """{"search":[{"id":"Q123","label":"Acme"}]}""";
        var entities2 = """{"entities":{"Q123":{"claims":{}}}}""";
        var resolver2 = WikidataWith(search2, entities2);
        (domain, ambiguous) = await resolver2.TryResolveDomainAsync("Acme", CancellationToken.None);
        Assert.Null(domain);
        Assert.False(ambiguous);
    }

    // ---------------- Fetcher: redirect + s2 gate ----------------

    [Fact]
    public async Task Fetcher_S2RedirectToGstaticAccepted()
    {
        var real = Png(128, 128);
        var globe = Png(16, 16);
        var handler = new FakeHandler(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u.Contains("domain=example.com"))
                return Resp(HttpStatusCode.MovedPermanently, "image/png", null,
                    "https://t1.gstatic.com/faviconV2?x=1");
            if (u.Contains("t1.gstatic.com"))
                return Resp(HttpStatusCode.OK, "image/png", real);
            if (u.Contains("domain=invalid.invalid"))
                return Resp(HttpStatusCode.NotFound, "image/png", globe);
            return Resp(HttpStatusCode.NotFound, "text/html", null);
        });
        var fetcher = new CompanyLogoFetcher(
            new FakeFactory(handler), null!, NullLogger<CompanyLogoFetcher>.Instance, PublicResolver);
        var url = await fetcher.TryFaviconAsync("https://example.com/", CancellationToken.None);
        Assert.Equal("https://www.google.com/s2/favicons?sz=128&domain=example.com", url);
    }

    [Fact]
    public async Task Fetcher_GlobeRefused_FallsThroughToHomepageIcon()
    {
        var globe = Png(16, 16);
        var icon64 = Png(64, 64);
        var html = "<html><head><link rel=\"icon\" href=\"/icon.png\"></head></html>"
            + new string(' ', 200);
        var handler = new FakeHandler(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u.Contains("domain=example.com"))
                return Resp(HttpStatusCode.OK, "image/png", globe);
            if (u.Contains("domain=invalid.invalid"))
                return Resp(HttpStatusCode.NotFound, "image/png", globe);
            if (u == "https://example.com/")
                return Resp(HttpStatusCode.OK, "text/html", Encoding.UTF8.GetBytes(html));
            if (u == "https://example.com/icon.png")
                return Resp(HttpStatusCode.OK, "image/png", icon64);
            return Resp(HttpStatusCode.NotFound, "text/html", null);
        });
        var fetcher = new CompanyLogoFetcher(
            new FakeFactory(handler), null!, NullLogger<CompanyLogoFetcher>.Instance, PublicResolver);
        var url = await fetcher.TryFaviconAsync("https://example.com/", CancellationToken.None);
        Assert.Equal("https://example.com/icon.png", url);
    }

    [Fact]
    public async Task Fetcher_NakedToWwwRedirectFollowed_EvilRedirectRefused()
    {
        var icon64 = Png(64, 64);
        var favicon32 = Png(32, 32);
        var html = "<html><head><link rel=\"icon\" href=\"/icon.png\"></head></html>"
            + new string(' ', 200);
        var handler = new FakeHandler(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u.Contains("domain=intuit.com"))
                return Resp(HttpStatusCode.NotFound, "text/html", null);
            if (u == "https://intuit.com/")
                return Resp(HttpStatusCode.MovedPermanently, "text/html", null, "https://www.intuit.com/");
            if (u == "https://www.intuit.com/")
                return Resp(HttpStatusCode.OK, "text/html", Encoding.UTF8.GetBytes(html));
            if (u == "https://www.intuit.com/icon.png")
                return Resp(HttpStatusCode.OK, "image/png", icon64);
            if (u == "https://intuit.com/favicon.ico")
                return Resp(HttpStatusCode.OK, "image/png", favicon32);
            return Resp(HttpStatusCode.NotFound, "text/html", null);
        });
        var fetcher = new CompanyLogoFetcher(
            new FakeFactory(handler), null!, NullLogger<CompanyLogoFetcher>.Instance, PublicResolver);
        // naked→www 跟随成功,首页图标 64px 胜过 favicon 32px。
        var url = await fetcher.TryFaviconAsync("https://intuit.com/", CancellationToken.None);
        Assert.Equal("https://www.intuit.com/icon.png", url);

        // 跨域重定向拒绝:首页 301 到 evil.com 时不跟随——即使 evil 侧图标更大
        // (128px),结果仍是本站 favicon(32px),证明 evil 图未被采纳。
        var evilIcon128 = Png(128, 128);
        var evilHtml = "<html><head><link rel=\"icon\" href=\"/evil-icon.png\"></head></html>"
            + new string(' ', 200);
        var evilHandler = new FakeHandler(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u.Contains("domain=intuit.com"))
                return Resp(HttpStatusCode.NotFound, "text/html", null);
            if (u == "https://intuit.com/")
                return Resp(HttpStatusCode.MovedPermanently, "text/html", null, "https://evil.com/");
            if (u == "https://evil.com/")
                return Resp(HttpStatusCode.OK, "text/html", Encoding.UTF8.GetBytes(evilHtml));
            if (u == "https://evil.com/evil-icon.png")
                return Resp(HttpStatusCode.OK, "image/png", evilIcon128);
            if (u == "https://intuit.com/favicon.ico")
                return Resp(HttpStatusCode.OK, "image/png", favicon32);
            return Resp(HttpStatusCode.NotFound, "text/html", null);
        });
        var evilFetcher = new CompanyLogoFetcher(
            new FakeFactory(evilHandler), null!, NullLogger<CompanyLogoFetcher>.Instance, PublicResolver);
        var evilUrl = await evilFetcher.TryFaviconAsync("https://intuit.com/", CancellationToken.None);
        Assert.Equal("https://intuit.com/favicon.ico", evilUrl);
    }

    [Fact]
    public async Task Fetcher_IsDefaultFavicon_404GlobeBodyCountsAsDefault()
    {
        var globe = Png(16, 16);
        var real = Png(128, 128);
        var handler = new FakeHandler(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u.Contains("domain=unknown.example"))
                return Resp(HttpStatusCode.NotFound, "image/png", globe);
            if (u.Contains("domain=real.example"))
                return Resp(HttpStatusCode.OK, "image/png", real);
            if (u.Contains("domain=invalid.invalid"))
                return Resp(HttpStatusCode.NotFound, "image/png", globe);
            return Resp(HttpStatusCode.NotFound, "text/html", null);
        });
        var fetcher = new CompanyLogoFetcher(
            new FakeFactory(handler), null!, NullLogger<CompanyLogoFetcher>.Instance, PublicResolver);
        Assert.True(await fetcher.IsDefaultFaviconAsync(
            "https://www.google.com/s2/favicons?sz=128&domain=unknown.example", CancellationToken.None));
        Assert.False(await fetcher.IsDefaultFaviconAsync(
            "https://www.google.com/s2/favicons?sz=128&domain=real.example", CancellationToken.None));
    }

    // ---------------- Local catalog (real embedded resources) ----------------

    [Fact]
    public void Catalog_RealResources()
    {
        var catalog = new LocalLogoCatalog();
        var intuit = catalog.TryResolve("Intuit");
        Assert.NotNull(intuit);
        Assert.Equal(LocalLogoCatalog.SimpleIconsProvider, intuit.Value.Provider);
        Assert.Equal("intuit", intuit.Value.Key);
        var svg = catalog.TryGetSvg(intuit.Value.Provider, intuit.Value.Key);
        Assert.NotNull(svg);
        Assert.StartsWith("<svg", svg);

        // .NET 两层都有(Simple Icons 亦有 dotnet),目录按 Simple Icons 优先返回。
        var dotnet = catalog.TryResolve(".NET");
        Assert.NotNull(dotnet);
        Assert.Equal(LocalLogoCatalog.SimpleIconsProvider, dotnet.Value.Provider);
        Assert.Equal("dotnet", dotnet.Value.Key);
        var dotnetSvg = catalog.TryGetSvg(dotnet.Value.Provider, dotnet.Value.Key);
        Assert.NotNull(dotnetSvg);
        Assert.Contains("<svg", dotnetSvg);

        // Adobe 只在 gilbarbara 层(Simple Icons 无 adobe);取图优先 *-icon.svg。
        // gilbarbara 文件带 XML 声明,故断言 Contains 而非 StartsWith。
        var adobe = catalog.TryResolve("Adobe");
        Assert.NotNull(adobe);
        Assert.Equal(LocalLogoCatalog.LogosProvider, adobe.Value.Provider);
        Assert.Equal("adobe", adobe.Value.Key);
        // 回归:TryResolve 返回的键必须能直接进 URL(TryGetSvg 与端点白名单
        // 都认)——若返回文件名(adobe-icon.svg),点号会被白名单拒绝致整层 404。
        Assert.True(LocalLogoCatalog.IsSafeKey(adobe.Value.Key));
        Assert.Equal("/api/jobs/companies/local-logos/logos/adobe",
            LocalLogoCatalog.LocalLogoUrl(adobe.Value.Provider, adobe.Value.Key));
        var adobeSvg = catalog.TryGetSvg(adobe.Value.Provider, adobe.Value.Key);
        Assert.NotNull(adobeSvg);
        Assert.Contains("<svg", adobeSvg);

        // 诚实缺口:Geotab 不在两层本地数据里,本地层返回 null(不伪造)。
        Assert.Null(catalog.TryResolve("Geotab"));
        Assert.Null(catalog.TryResolve("Corpay"));
        Assert.Null(catalog.TryGetSvg("simple-icons", "../evil"));
        Assert.Null(catalog.TryGetSvg("nope", "intuit"));
    }

    [Fact]
    public void LocalLogoUrl_RelativePath()
    {
        Assert.Equal("/api/jobs/companies/local-logos/simple-icons/intuit",
            LocalLogoCatalog.LocalLogoUrl("simple-icons", "intuit"));
        Assert.True(LocalLogoCatalog.IsSafeKey("intuit"));
        Assert.False(LocalLogoCatalog.IsSafeKey("../intuit"));
        Assert.False(LocalLogoCatalog.IsSafeKey("in tuit"));
        Assert.False(LocalLogoCatalog.IsSafeKey(""));
    }

    // ---------------- helpers ----------------

    private static Task<IPAddress[]> PublicResolver(string host) =>
        Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    private static WikidataDomainResolver WikidataWith(string searchJson, string entitiesJson)
    {
        var handler = new FakeHandler(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u.Contains("wbsearchentities"))
                return Resp(HttpStatusCode.OK, "application/json", Encoding.UTF8.GetBytes(searchJson));
            if (u.Contains("wbgetentities"))
                return Resp(HttpStatusCode.OK, "application/json", Encoding.UTF8.GetBytes(entitiesJson));
            return Resp(HttpStatusCode.NotFound, "text/html", null);
        });
        return new WikidataDomainResolver(new FakeFactory(handler), NullLogger<WikidataDomainResolver>.Instance);
    }

    private static HttpResponseMessage Resp(HttpStatusCode status, string mediaType, byte[]? body, string? location = null)
    {
        var resp = new HttpResponseMessage(status);
        // 真实 HttpClient 的 Content 恒非空;此处同样恒设,避免被测代码走空体分支。
        resp.Content = new ByteArrayContent(body ?? []);
        if (mediaType is not null)
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        if (location is not null) resp.Headers.Location = new Uri(location);
        return resp;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // 最小合法 PNG(签名 + IHDR 宽高,余下 0 填充;尺寸嗅探只读 IHDR)。
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[33];
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
        bytes[4] = 0x0D; bytes[5] = 0x0A; bytes[6] = 0x1A; bytes[7] = 0x0A;
        bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
        bytes[16] = (byte)(width >> 24); bytes[17] = (byte)(width >> 16);
        bytes[18] = (byte)(width >> 8); bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24); bytes[21] = (byte)(height >> 16);
        bytes[22] = (byte)(height >> 8); bytes[23] = (byte)height;
        return bytes;
    }
}
