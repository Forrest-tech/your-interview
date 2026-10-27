using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using YourInterview.Services.Jobs.Infrastructure.Services;

namespace YourInterview.Services.Jobs.Api;

// ============================================================================
//  票 249 R1:本地开源 logo 的匿名取图端点。
//
//  回填把 R1 命中的 logo 存为相对地址
//  /api/jobs/companies/local-logos/{provider}/{key}(走与 /api/jobs 相同的
//  网关前缀,前端 <img> 直取,无需 Authorization——<img> 本来也带不上
//  token)。本端点只吐内置的 CC0 品牌 SVG(无任何用户数据),故 AllowAnonymous;
//  key 白名单 + provider 白名单双重把关,查无返回 404。SVG 一律
//  image/svg+xml + public 缓存 86400 秒(内置数据随重 vendor 才变)。
// ============================================================================
[ApiController]
[Route("api/jobs/companies/local-logos")]
public sealed class LocalLogosController(ILocalLogoCatalog catalog) : ControllerBase
{
    [HttpGet("{provider}/{key}")]
    [AllowAnonymous]
    public IResult GetLogo(string provider, string key)
    {
        if (provider != LocalLogoCatalog.SimpleIconsProvider
            && provider != LocalLogoCatalog.LogosProvider)
            return Results.NotFound();
        if (!LocalLogoCatalog.IsSafeKey(key)) return Results.NotFound();
        var svg = catalog.TryGetSvg(provider, key);
        if (string.IsNullOrWhiteSpace(svg)) return Results.NotFound();
        Response.Headers.CacheControl = "public, max-age=86400";
        return Results.Content(svg, "image/svg+xml", statusCode: 200);
    }
}
