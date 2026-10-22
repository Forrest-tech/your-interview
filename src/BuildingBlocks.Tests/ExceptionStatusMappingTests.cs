using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;
using YourInterview.BuildingBlocks.Hosting;

namespace YourInterview.BuildingBlocks.Tests;

/// <summary>
/// COD-05 / 票07 seam:共享管线异常→状态码映射的公开契约 ——
/// DbUpdateConcurrencyException(xmin 乐观并发冲突)必须映射 409 而非默认 500,
/// 且其余既有映射(Validation→400 / 401 / 404 / Argument→400 / 未知→500)保持不变。
/// UseServiceDefaults 的全局异常处理器直接调用 ServiceDefaults.MapExceptionToStatusCode,
/// 本测试即管线层映射成立的证明;真实端点并发实测见 evidence-07。
/// COD-13 / 票24 追加:JsonException(AttachAsset 以 ReadFromJsonAsync 直读请求体,
/// 坏 JSON 时由 STJ 抛 JsonException 且无局部捕获)必须映射 400 而非默认 500;
/// 真实端点坏 JSON 实测见 evidence-24。既有映射在本文件逐项锁定,不许回退。
/// 票76 追加:BadHttpRequestException(Kestrel 请求体超上限时抛,框架 StatusCode=413)
/// 必须透传其框架 StatusCode(超限即 413)而非默认 500;票24 在此处的
/// 「仍落 500」不变项锁定随本票翻转。真实端点超限实测见 evidence-76。
/// </summary>
public class ExceptionStatusMappingTests
{
    [Fact]
    public void ConcurrencyException_MapsTo409Conflict()
    {
        ServiceDefaults.MapExceptionToStatusCode(new DbUpdateConcurrencyException())
            .Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public void ValidationException_StillMapsTo400()
    {
        ServiceDefaults.MapExceptionToStatusCode(new ValidationException("bad input"))
            .Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void UnauthorizedAccess_StillMapsTo401()
    {
        ServiceDefaults.MapExceptionToStatusCode(new UnauthorizedAccessException())
            .Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public void KeyNotFound_StillMapsTo404()
    {
        ServiceDefaults.MapExceptionToStatusCode(new KeyNotFoundException())
            .Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public void ArgumentException_StillMapsTo400()
    {
        ServiceDefaults.MapExceptionToStatusCode(new ArgumentException("bad arg"))
            .Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void UnknownException_StillMapsTo500()
    {
        ServiceDefaults.MapExceptionToStatusCode(new InvalidOperationException("boom"))
            .Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public void NullException_MapsTo500()
    {
        ServiceDefaults.MapExceptionToStatusCode(null)
            .Should().Be(StatusCodes.Status500InternalServerError);
    }

    // ---- COD-13 / 票24:JsonException → 400 ----

    [Fact] // 核心:畸形 JSON 请求体的异常类型本体
    public void JsonException_MapsTo400BadRequest()
    {
        ServiceDefaults.MapExceptionToStatusCode(new JsonException("bad json"))
            .Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact] // 真实形态:与 AttachAsset 的 ReadFromJsonAsync 同一 STJ 解析路径产生的 JsonException
    public void MalformedJson_RealDeserialization_MapsTo400()
    {
        var ex = CaptureJsonException(() =>
            JsonSerializer.Deserialize<Dictionary<string, string>>("{\"kind\": broken-json"));
        ServiceDefaults.MapExceptionToStatusCode(ex)
            .Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact] // 真实形态:JSON 合法但字段类型不符(活体 $.kind 传数字同型)同样抛 JsonException
    public void TypeMismatchJson_RealDeserialization_MapsTo400()
    {
        var ex = CaptureJsonException(() => JsonSerializer.Deserialize<int>("\"not-a-number\""));
        ServiceDefaults.MapExceptionToStatusCode(ex)
            .Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact] // boundary:空输入 / 纯空白输入 —— 活体空体形态,STJ 抛 JsonException(无 JSON 令牌)
    public void EmptyOrWhitespaceInput_JsonException_MapsTo400()
    {
        foreach (var input in new[] { "", "   " })
        {
            var ex = CaptureJsonException(() => JsonSerializer.Deserialize<JsonElement>(input));
            ServiceDefaults.MapExceptionToStatusCode(ex)
                .Should().Be(StatusCodes.Status400BadRequest, "input '{0}' is a client error", input);
        }
    }

    [Fact] // boundary:特殊字符 / Unicode 内容后的畸形 JSON
    public void UnicodeMalformedJson_MapsTo400()
    {
        var ex = CaptureJsonException(() =>
            JsonSerializer.Deserialize<Dictionary<string, string>>("{\"kind\":\"中文☃\", broken}"));
        ServiceDefaults.MapExceptionToStatusCode(ex)
            .Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact] // boundary:超长畸形输入(100K 字符)—— 仍是 JsonException,不得因长度改口径
    public void VeryLongMalformedJson_MapsTo400()
    {
        var ex = CaptureJsonException(() =>
            JsonSerializer.Deserialize<JsonElement>(new string('{', 100_000)));
        ServiceDefaults.MapExceptionToStatusCode(ex)
            .Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact] // boundary:继承 —— JsonException 子类同样命中 400(switch 类型模式语义)
    public void DerivedJsonException_MapsTo400()
    {
        ServiceDefaults.MapExceptionToStatusCode(new DerivedJsonException())
            .Should().Be(StatusCodes.Status400BadRequest);
    }

    // ---- 票76:BadHttpRequestException → 透传框架 StatusCode(超限 = 413) ----

    [Fact] // 核心:Kestrel 超限真实形态(消息与框架 StatusCode=413,活体三服务实证同型)
    public void BadHttpRequestException_PayloadTooLarge_MapsTo413()
    {
        ServiceDefaults.MapExceptionToStatusCode(new BadHttpRequestException(
                "Request body too large. The max request body size is 67108864 bytes.",
                StatusCodes.Status413PayloadTooLarge))
            .Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact] // 透传而非硬编码 413:框架其他细分码(请求头过大 431)原样返回
    public void BadHttpRequestException_HeaderFieldsTooLarge_PassesThrough431()
    {
        ServiceDefaults.MapExceptionToStatusCode(new BadHttpRequestException(
                "Request headers too large.", StatusCodes.Status431RequestHeaderFieldsTooLarge))
            .Should().Be(StatusCodes.Status431RequestHeaderFieldsTooLarge);
    }

    [Fact] // 透传:框架其他细分码(408)原样返回
    public void BadHttpRequestException_RequestTimeout_PassesThrough408()
    {
        ServiceDefaults.MapExceptionToStatusCode(new BadHttpRequestException(
                "Reading the request body timed out.", StatusCodes.Status408RequestTimeout))
            .Should().Be(StatusCodes.Status408RequestTimeout);
    }

    [Fact] // boundary:默认构造的框架 StatusCode 本即 400(非超限形态),透传后仍是客户端错误而非 500/413
    public void BadHttpRequestException_DefaultStatusCode_MapsTo400()
    {
        ServiceDefaults.MapExceptionToStatusCode(new BadHttpRequestException("Bad request."))
            .Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact] // 真实形态:带内部异常的构造(Kestrel 包装 IO 异常路径)同样透传 413
    public void BadHttpRequestException_WithInnerException_MapsTo413()
    {
        ServiceDefaults.MapExceptionToStatusCode(new BadHttpRequestException(
                "Request body too large.", StatusCodes.Status413PayloadTooLarge,
                new InvalidOperationException("inner")))
            .Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact] // boundary:继承 —— BadHttpRequestException 子类(Kestrel.Core 的实现即子类)同样命中透传
    public void DerivedBadHttpRequestException_MapsTo413()
    {
        ServiceDefaults.MapExceptionToStatusCode(new DerivedBadHttpRequestException())
            .Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact] // boundary-恰上限的映射侧对应面:恰在上限的请求不产生 BadHttpRequestException,
           // 穿过 Kestrel 后由校验层以 ValidationException→400 处置(活体 =64MiB→400 见 evidence-76),
           // 本条锁映射层口径不因本票误伤校验路径。
    public void AtLimitRequest_ValidationPath_StillMapsTo400()
    {
        ServiceDefaults.MapExceptionToStatusCode(new ValidationException("too long"))
            .Should().Be(StatusCodes.Status400BadRequest);
    }

    private static JsonException CaptureJsonException(Action act)
    {
        try
        {
            act();
        }
        catch (JsonException ex)
        {
            return ex;
        }
        throw new InvalidOperationException("Expected a JsonException but none was thrown.");
    }

    private sealed class DerivedJsonException : JsonException
    {
    }

    private sealed class DerivedBadHttpRequestException : BadHttpRequestException
    {
        public DerivedBadHttpRequestException()
            : base("Request body too large.", StatusCodes.Status413PayloadTooLarge)
        {
        }
    }
}
