using System.Net;
using System.Net.Http.Headers;
using Xunit;
using YourInterview.Services.Assessment.Infrastructure.Dictionary;
using static YourInterview.Services.Assessment.Tests.DictionaryTestHarness;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票262：词典懒拉取瞬时失败重试（Wikimedia 429 实证：同一文件同一 UA 下一次 429、
/// 数分钟后 200；旧代码一次失败即 404，前端随即在本轮会话内排除该键、合成不可用时
/// 整轮播放失败）。懒层键 = audio-index 的 Mp3Url 文件名；核心包/牛津包均缺席、
/// 缓存为空时才走到懒拉取分支。
/// </summary>
public class DictionaryLazyFetchRetryTests
{
    private const string Key = "En-us-retryword.ogg.mp3";

    private static OpenLayerStore Store() => BuildOpenStore(
        EcdictTsv(EcRow("retryword", "", "v. to try again", "v. 重试", "")),
        WordNetJsonl(WnEntry("retryword", ("v", [("to try again", null)]))),
        audioIndexJson: "{\"retryword\": [{\"file\": \"En-us-retryword.ogg\", \"accent\": \"us\", "
            + "\"license\": \"CC BY-SA 4.0\", \"artist\": \"Synth\", "
            + "\"mp3Url\": \"https://upload.wikimedia.org/wikipedia/commons/transcoded/x/En-us-retryword.ogg/En-us-retryword.ogg.mp3\"}]}");

    private static (AudioResolver Audio, string CacheDir, CountingHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var root = Path.Combine(Path.GetTempPath(), "dict-retry-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(root, "cache");
        var handler = new CountingHandler(responder);
        var built = BuildService(Store(), packDir: null, httpHandler: handler, cacheDir: cacheDir);
        return (built.Audio, cacheDir, handler);
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage TooManyRequestsWithRetryAfterZero()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return resp;
    }

    private static HttpResponseMessage Ok(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    [Fact]
    public async Task LazyFetch_429Then200_ReturnsCachedFile_AndRetriedOnce()
    {
        var calls = 0;
        var (audio, cacheDir, handler) = Build(_ =>
        {
            calls++;
            return calls == 1 ? TooManyRequestsWithRetryAfterZero() : Ok([1, 2, 3]);
        });
        var path = await audio.GetAudioFileAsync(Key, CancellationToken.None);
        Assert.NotNull(path);
        Assert.Equal(Path.Combine(cacheDir, Key), path);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(path));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task LazyFetch_Always429_ReturnsNull_AfterThreeAttempts()
    {
        var (audio, _, handler) = Build(_ => TooManyRequestsWithRetryAfterZero());
        var path = await audio.GetAudioFileAsync(Key, CancellationToken.None);
        Assert.Null(path);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task LazyFetch_404_ReturnsNull_WithoutRetry()
    {
        var (audio, _, handler) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var path = await audio.GetAudioFileAsync(Key, CancellationToken.None);
        Assert.Null(path);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task LazyFetch_500Then200_ReturnsCachedFile()
    {
        var calls = 0;
        var (audio, cacheDir, handler) = Build(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Ok([9, 8, 7]);
        });
        var path = await audio.GetAudioFileAsync(Key, CancellationToken.None);
        Assert.NotNull(path);
        Assert.Equal(Path.Combine(cacheDir, Key), path);
        Assert.Equal([9, 8, 7], await File.ReadAllBytesAsync(path));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task LazyFetch_NetworkExceptionThen200_ReturnsCachedFile()
    {
        // 接续补：DNS/连接类异常同样按瞬时失败重试（前任只测了状态码类）。
        var calls = 0;
        var (audio, cacheDir, handler) = Build(_ =>
        {
            calls++;
            if (calls == 1) throw new HttpRequestException("simulated DNS failure");
            return Ok([5, 5, 5]);
        });
        var path = await audio.GetAudioFileAsync(Key, CancellationToken.None);
        Assert.NotNull(path);
        Assert.Equal(Path.Combine(cacheDir, Key), path);
        Assert.Equal([5, 5, 5], await File.ReadAllBytesAsync(path));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task LazyFetch_SlowFirstAttemptThen200_RetriesOnPerAttemptTimeout()
    {
        // 接续补：单次 3s 预算超时走重试分支。首呼故意拖 10s（被 attemptCts 在 3s
        // 掐断），次呼立即 200；总耗时约 3s 预算 + 1s 退避 + 次呼。
        var root = Path.Combine(Path.GetTempPath(), "dict-retry-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(root, "cache");
        var handler = new SlowThenOkHandler();
        var built = BuildService(Store(), packDir: null, httpHandler: handler, cacheDir: cacheDir);
        var path = await built.Audio.GetAudioFileAsync(Key, CancellationToken.None);
        Assert.NotNull(path);
        Assert.Equal(Path.Combine(cacheDir, Key), path);
        Assert.Equal([7, 7, 7], await File.ReadAllBytesAsync(path));
        Assert.Equal(2, handler.Calls);
    }

    private sealed class SlowThenOkHandler : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1)
            {
                // 遵守调用方取消：3s 预算到时抛出 TaskCanceledException，走超时重试分支。
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                throw new InvalidOperationException("unreachable: delay should have been cancelled");
            }
            return Ok([7, 7, 7]);
        }
    }
}
