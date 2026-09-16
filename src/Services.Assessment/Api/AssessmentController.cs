using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using YourInterview.BuildingBlocks.Security;
using YourInterview.BuildingBlocks.Web;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Assessment.Api;

/// <summary>
/// AI 实战模拟 API。
///
/// 流程:建会话 → AI 出题 → 我作答 → 评分 → 总评。
/// 每一步都是显式端点,而不是"一次调用跑完全程"——
/// 因为真实练习是交互式的(答一题就想看一题反馈),不是批处理。
/// </summary>
[ApiController]
[Route("api/assessment")]
[Authorize]
public sealed class AssessmentController(ISender sender, ICurrentUser currentUser,
    PronunciationAssessor assessor, SpeechSynthesizer synthesizer) : ControllerBase
{
    private Guid Me => currentUser.UserId ?? Guid.Empty;

    // ---------------- 会话 ----------------

    [HttpGet("sessions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null, [FromQuery] string? mode = null,
        CancellationToken ct = default)
    {
        var r = await sender.Send(new ListSessionsQuery(Me, page, pageSize, status, mode), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpGet("sessions/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> Get(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetSessionQuery(id, Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpPost("sessions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> Create([FromBody] CreateSessionBody body, CancellationToken ct)
    {
        var mode = Enum.TryParse<SessionMode>(body.Mode, true, out var m) ? m : SessionMode.TopicDrill;
        var r = await sender.Send(new CreateSessionCommand(Me, body.Title, mode, body.Topic,
            body.CompanyStyle, body.TargetQuestionCount, body.Language ?? "en"), ct);
        return r.IsSuccess
            ? Results.Created($"/api/assessment/sessions/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpDelete("sessions/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> Delete(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteSessionCommand(id, Me), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>结束练习并生成总评。</summary>
    [HttpPost("sessions/{id:guid}/complete")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> Complete(Guid id, [FromBody] CompleteBody body, CancellationToken ct)
    {
        var r = await sender.Send(new CompleteSessionCommand(id, Me, body.OverallSummary,
            body.PriorityAction), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpPost("sessions/{id:guid}/abandon")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> Abandon(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new AbandonSessionCommand(id, Me), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    // ---------------- 题目 ----------------

    /// <summary>AI 出题(一次可追加多道)。</summary>
    [HttpPost("sessions/{id:guid}/questions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> AddQuestions(Guid id, [FromBody] AddQuestionsBody body, CancellationToken ct)
    {
        var r = await sender.Send(new AddQuestionsCommand(id, Me, body.Questions), ct);
        return r.IsSuccess ? Results.Ok(new { ids = r.Value }) : r.ToProblemDetails();
    }

    /// <summary>作答:提交文本或音频。</summary>
    [HttpPost("sessions/{id:guid}/questions/{questionId:guid}/answer")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> Answer(Guid id, Guid questionId, [FromBody] AnswerBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new AnswerQuestionCommand(id, questionId, Me, body.AnswerText,
            body.AudioPath, body.DurationSeconds, body.TranscriptJson), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>评分:写回六维分数 + 明细问题 + 推荐答案。</summary>
    [HttpPost("sessions/{id:guid}/questions/{questionId:guid}/score")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> Score(Guid id, Guid questionId, [FromBody] ScoreBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new ScoreQuestionCommand(id, questionId, Me,
            body.Pronunciation, body.Fluency, body.SentenceIntegrity, body.Structure,
            body.TechnicalDepth, body.Relevance, body.Comment, body.RecommendedAnswer,
            body.BetterStructure, body.FillerWordsJson, body.Issues ?? []), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    [HttpPost("sessions/{id:guid}/questions/{questionId:guid}/skip")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> Skip(Guid id, Guid questionId, CancellationToken ct)
    {
        var r = await sender.Send(new SkipQuestionCommand(id, questionId, Me), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    // ---------------- 统计与联动 ----------------

    [HttpGet("stats")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> Stats(CancellationToken ct)
    {
        var r = await sender.Send(new GetAssessmentStatsQuery(Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>六维说明(雷达图轴定义 + 提升方法)。</summary>
    [HttpGet("dimensions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> Dimensions(CancellationToken ct)
    {
        var r = await sender.Send(new GetDimensionsQuery(), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>根据历史短板推荐下一次该练什么。</summary>
    [HttpGet("suggest")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> Suggest([FromQuery] int questionCount = 5, CancellationToken ct = default)
    {
        var r = await sender.Send(new SuggestSessionCommand(Me, questionCount), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    // ---------------- 发音评估(跟读练习) ----------------

    /// <summary>
    /// 对一段跟读录音做 Azure 发音评估。
    ///
    /// 音频怎么传:前端用 Web Audio API 把录音解成 Float32 PCM,
    /// 以 JSON 数组(或 base64)发过来,服务端这边重采样 + 装 WAV 头。
    /// 这样服务端不需要 ffmpeg —— 少一个系统依赖,部署面更小。
    ///
    /// key 只在服务端;浏览器拿不到,也就无法被滥用。
    /// </summary>
    [HttpPost("pronunciation/assess")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> AssessPronunciation([FromBody] AssessPronunciationBody body,
        CancellationToken ct)
    {
        if (!await assessor.IsAvailableAsync(Me, ct))
            return Results.Problem(
                title: "Azure Speech 未配置",
                detail: "服务端缺少可用的 AzureSpeech key,发音评估不可用。请先在 AI 语音设置里保存密钥。",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        if (body.Samples is null || body.Samples.Length == 0)
            return Results.Problem(title: "音频为空",
                detail: "请先录音再提交评分。",
                statusCode: StatusCodes.Status400BadRequest);

        // 前端已解码成 Float32 PCM,这里装成 16k 单声道 WAV
        var wav = body.WavBase64 is { Length: > 0 }
            ? Convert.FromBase64String(body.WavBase64)
            : PcmWav.FromFloat32(body.Samples, body.SampleRate ?? 48000);

        if (!PcmWav.IsWav(wav))
            return Results.Problem(title: "音频格式不支持",
                detail: "需要 PCM WAV,或改传 Float32 PCM 样本由服务端封装。",
                statusCode: StatusCodes.Status400BadRequest);

        try
        {
            // 传当前用户 id —— 让评估器解析"这个用户在设置页保存的 key"
            var result = await assessor.AssessAsync(wav, body.ReferenceText ?? string.Empty,
                body.Language ?? "en-US", ct, Me);

            // 回传时把拿不到的维度如实置 null —— 不编造分数
            return Results.Ok(new
            {
                pronScore = result.PronScore,
                accuracyScore = result.AccuracyScore,
                fluencyScore = result.FluencyScore,
                completenessScore = result.CompletenessScore,
                prosodyScore = result.ProsodyScore,
                recognized = result.RecognizedText,
                words = result.Words.Select(w => new
                {
                    word = w.Word,
                    accuracy = w.Accuracy,
                    errorType = w.ErrorType
                }),
                simulated = false
            });
        }
        catch (AzureAuthException ex)
        {
            // ⚠️ 与 /tts、/speech/test 同理:Azure 凭据错误是上游网关类错误,
            // 用 502 而不是 401(401 会被前端理解为"会话过期"→ 刷新/登出)。
            // 此处必须显式捕获:AzureAuthException 继承自 Exception 而非
            // InvalidOperationException,不捕获会漏到全局处理器变成 500。
            return Results.Problem(title: "Azure 密钥/区域无效", detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "发音评估失败", detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
    // ============================================================
    // /practice 页的持久化端点(2026-09-15 第十七轮)
    //
    // 设计口径:
    //   · materials / recordings 全部**按当前用户隔离**(RequireUserId),
    //     练习内容是最私人的数据,不能串号。
    //   · 树用"整树读写"而不是细粒度增删改 —— 见 SaveMaterialTreeCommand 注释。
    //   · 评分结果落库 = 同一段音频不重复消耗 Azure 额度(需求第 4 条)。
    // ============================================================

    // ---------- 素材树 ----------

    /// <summary>取我的整棵素材树。</summary>
    [HttpGet("materials")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> GetMaterials(CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var tree = await sender.Send(new GetMaterialTreeQuery(userId), ct);
        return Results.Ok(tree);
    }

    /// <summary>
    /// 整树覆盖保存(新建/改名/改正文/拖拽排序/删除 全部走这一个端点)。
    /// 原子操作:要么全生效,要么什么都没变。
    /// </summary>
    [HttpPut("materials")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> SaveMaterials([FromBody] SaveTreeBody body, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var result = await sender.Send(new SaveMaterialTreeCommand(userId, body.Nodes ?? []), ct);
        return result.IsSuccess ? Results.Ok(new { saved = result.Value })
                                : result.ToProblemDetails();
    }

    // ---------- 录音 ----------

    /// <summary>某素材下的录音列表(含已缓存的评分)。</summary>
    [HttpGet("materials/{materialId:guid}/recordings")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> ListRecordings(Guid materialId, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var list = await sender.Send(new ListRecordingsQuery(userId, materialId), ct);
        return Results.Ok(list);
    }

    /// <summary>
    /// 上传一次录音(音频 + 元数据一步到位)。
    /// multipart/form-data:file=音频,durationSeconds/contentType 作表单字段。
    /// </summary>
    [HttpPost("materials/{materialId:guid}/recordings")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockAnswer)]
    [RequestSizeLimit(26 * 1024 * 1024)]
    public async Task<IResult> SaveRecording(Guid materialId,
        [FromForm] double durationSeconds, [FromForm] string? contentType,
        [FromForm] string? language, IFormFile file, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (file is null || file.Length == 0)
            return Results.Problem(title: "没有收到音频", detail: "请先录音再上传。",
                statusCode: StatusCodes.Status400BadRequest);

        await using var stream = file.OpenReadStream();
        var result = await sender.Send(new SaveRecordingCommand(userId, materialId, file.FileName,
            contentType ?? file.ContentType, durationSeconds, language ?? "en-US", stream), ct);

        return result.IsSuccess
            ? Results.Created($"/api/assessment/recordings/{result.Value.Id}", result.Value)
            : result.ToProblemDetails();
    }

    /// <summary>回放:取录音音频流。前端 &lt;audio src&gt; 直接指向这里。</summary>
    [HttpGet("recordings/{id:guid}/audio")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> GetRecordingAudio(Guid id, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var result = await sender.Send(new GetRecordingAudioQuery(userId, id), ct);
        if (!result.IsSuccess) return result.ToProblemDetails();

        var (stream, contentType, fileName) = result.Value;
        // enableRangeProcessing:让浏览器音频控件能拖动进度条(否则只能从头播)
        return Results.File(stream, contentType, fileName, enableRangeProcessing: true);
    }

    /// <summary>删除一条录音(元数据软删 + 尽力删文件)。</summary>
    [HttpDelete("recordings/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> DeleteRecording(Guid id, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var result = await sender.Send(new DeleteRecordingCommand(userId, id), ct);
        return result.IsSuccess ? Results.NoContent() : result.ToProblemDetails();
    }

    /// <summary>
    /// 取已缓存的评分 —— **不调 Azure**。
    /// 前端打开录音列表时先打这个:命中就不必再花额度重评。
    /// </summary>
    [HttpGet("recordings/{id:guid}/score")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> GetRecordingScore(Guid id, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var result = await sender.Send(new GetRecordingScoreQuery(userId, id), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : result.ToProblemDetails();
    }

    /// <summary>
    /// 保存评分结果(需求第 4 条:评分入库,避免重复评分)。
    ///
    /// ⚠️ 这里**不调 Azure** —— 分数是前端已经算好的,本端点只负责落库。
    ///    这样"评分"与"存分"两件事解耦:网络抖动时不会重复花额度。
    /// </summary>
    [HttpPost("recordings/{id:guid}/score")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> SaveRecordingScore(Guid id, [FromBody] RecordingScoreBody body,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var result = await sender.Send(new SaveRecordingScoreCommand(
            userId, id, body.PronScore, body.AccuracyScore, body.FluencyScore,
            body.CompletenessScore, body.ProsodyScore, body.RecognizedText ?? string.Empty,
            body.Words is null ? null : System.Text.Json.JsonSerializer.Serialize(body.Words),
            body.AzureRegion ?? "canadacentral", body.ReferenceText ?? string.Empty), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : result.ToProblemDetails();
    }

    // ---------- Azure Speech 设置 ----------

    /// <summary>
    /// 查询语音服务配置状态。
    /// ⚠️ **只返回** hasKey / region / 掩码,永远不下发 key 本身。
    /// </summary>
    [HttpGet("speech/settings")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> GetSpeechSettings(CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var status = await sender.Send(new GetSpeechSettingQuery(userId), ct);
        return Results.Ok(status);
    }

    /// <summary>保存 Azure Speech key / 区域(服务端保管,不进浏览器)。</summary>
    [HttpPut("speech/settings")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> SaveSpeechSettings([FromBody] SpeechSettingsBody body, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var result = await sender.Send(new SaveSpeechSettingCommand(
            userId, body.Key, body.Region, body.Endpoint), ct);
        if (result.IsSuccess) return Results.Ok(result.Value);

        // ⚠️ 2026-09-16(Forrest 第 1 条):保存前会先去 Azure 真测。
        //    凭据被拒时必须回 **502**(上游拒收我们的凭据),
        //    不能让它落到 ToProblemDetails 的默认分支变成 500 ——
        //    更不能是 401/403(前端会当成会话失效而登出)。
        var code = result.Error?.Code ?? string.Empty;
        if (code is "Speech.CredentialRejected" or "Speech.ProbeUnexpected")
        {
            return Results.Problem(
                title: "密钥或区域无效",
                detail: result.Error?.Description ?? "凭据验证失败,未保存。",
                statusCode: StatusCodes.Status502BadGateway,
                extensions: new Dictionary<string, object?> { ["code"] = code });
        }

        return result.ToProblemDetails();
    }

    /// <summary>
    /// 测试一把**尚未保存**的候选凭据(Forrest 第 1 条需求:先测后存)。
    ///
    /// 与 /speech/test 的区别:
    ///   · /speech/test        → 测"当前已保存的 key"
    ///   · /speech/test-credential → 测请求体里这把候选 key,**不落库**
    /// 前端流程:先在输入框填 key → 调本端点 → 通过才调 PUT /speech/settings 保存。
    ///
    /// 失败一律映射为 **502**(上游 Azure 拒收我们的凭据),
    /// 绝不用 401/403 —— 那些码在前端意味着"会话失效",会把用户踢回登录页。
    /// </summary>
    [HttpPost("speech/test-credential")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> TestSpeechCredential([FromBody] SpeechCredentialBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new TestSpeechCredentialCommand(body.Key, body.Region), ct);
        if (r.IsSuccess)
            return Results.Ok(new { ok = true, message = r.Value.Message });

        // 区分"参数没填"(400)与"Azure 拒收"(502):
        // 前者是用户输入问题,后者是凭据问题,前端提示不同。
        var code = r.Error?.Code ?? string.Empty;
        var isValidation = code is "Speech.KeyEmpty" or "Speech.RegionEmpty";
        return Results.Problem(
            title: isValidation ? "填写不完整" : "密钥或区域无效",
            detail: r.Error?.Description ?? "凭据验证失败。",
            statusCode: isValidation
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status502BadGateway);
    }

    /// <summary>
    /// 真连通性测试 —— 拿当前 key 去 Azure 走一次最小请求。
    ///
    /// 为什么要真测:只校验"字符串非空"毫无意义,用户填错区域或密钥时
    /// 界面会说"保存成功",等到练习时才炸 —— 那是骗人。
    /// 这里失败就如实报 401/403 与区域不匹配,绝不做假成功。
    /// </summary>
    [HttpPost("speech/test")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> TestSpeech(CancellationToken ct)
    {
        if (!await assessor.IsAvailableAsync(Me, ct))
            return Results.Problem(title: "Azure Speech 未配置",
                detail: "服务端还没有可用的密钥,请先在 AI 语音设置里保存密钥。",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        try
        {
            var ok = await assessor.PingAsync(ct, Me);
            return ok
                ? Results.Ok(new { ok = true, message = "连接正常,密钥可用。" })
                : Results.Problem(title: "连接失败", detail: "Azure 未按预期响应。",
                    statusCode: StatusCodes.Status502BadGateway);
        }
        catch (AzureAuthException ex)
        {
            // ⚠️ 不能用 401 表示"Azure 密钥/区域错误"!
            // 401 在任何"需要鉴权"的 API 里都意味着"你的**访问令牌**无效,请重新登录"。
            // 前端拦截器据此自动刷新会话;这是**领域错误**不是**会话错误**,
            // 用它冒充 401 会让用户点一次示范朗读就被弹回登录页(2026-09-16 真实踩坑),
            // 更糟的是会触发刷新令牌轮转/复用检测,把整个会话搞废。
            //
            // 语义上这是"上游 Azure 拒绝了**我们自己**的凭据" —— 服务器对下游的
            // 网关错误,所以用 502 Bad Gateway。前端据 status 给出可操作提示。
            // 真正需要重新登录的情况由框架的 [Authorize] 中间件以 401 返回,
            // 与业务代码无关,两者从此不再混淆。
            return Results.Problem(title: "密钥或区域无效", detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    // ---------- 示范朗读(Azure 神经语音合成) ----------

    /// <summary>
    /// 合成示范朗读音频。
    ///
    /// Forrest 需求第 3 条:示范朗读可选择使用同一把 Azure key。
    /// · 传 engine=browser → 前端自己用浏览器语音,不该调这里;
    /// · 传 engine=azure   → 调这里拿 MP3 播。
    ///
    /// ⚠️ 没配 key 时返回 503 并说明原因,前端据此**如实**回退到浏览器语音,
    ///    并告知用户"Azure 不可用",绝不静默冒充。
    /// </summary>
    [HttpPost("tts")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> Synthesize([FromBody] TtsBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Text))
            return Results.Problem(title: "文本为空", detail: "请先选择要朗读的素材内容。",
                statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var mp3 = await synthesizer.SynthesizeAsync(body.Text, body.Voice, body.Speed, Me, ct);
            // 返回音频流而不是 base64 —— 前端直接 new Audio(objectURL) 播,省一次解码
            return Results.File(mp3, "audio/mpeg", $"tts-{Guid.NewGuid():N}.mp3");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("未配置"))
        {
            return Results.Problem(title: "Azure 语音未配置", detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (AzureAuthException ex)
        {
            // ⚠️ 同 TestSpeech:Azure 凭据错误绝不能用 401 —— 那会让前端拦截器
            // 误以为"会话过期"并触发刷新/登出。这是上游服务拒绝我们凭据的
            // 网关类错误,用 502;真正的会话失效由 [Authorize] 返回 401。
            return Results.Problem(title: "密钥或区域无效", detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "语音合成失败", detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}

// ---------------- 请求体 ----------------

public sealed record CreateSessionBody(string Title, string Mode, string? Topic,
    string? CompanyStyle, int TargetQuestionCount = 5, string? Language = null);

public sealed record CompleteBody(string? OverallSummary, string? PriorityAction);

public sealed record AddQuestionsBody(IReadOnlyList<QuestionDraftIn> Questions);

public sealed record AnswerBody(string? AnswerText, string? AudioPath, double? DurationSeconds,
    string? TranscriptJson);

public sealed record ScoreBody(int Pronunciation, int Fluency, int SentenceIntegrity, int Structure,
    int TechnicalDepth, int Relevance, string? Comment, string? RecommendedAnswer,
    string? BetterStructure, string? FillerWordsJson, IReadOnlyList<IssueDraftIn>? Issues);

/// <summary>
/// 发音评估请求体。
/// 两种给音频的方式(二选一):
///   1. Samples —— 前端 Web Audio 解出的 Float32 PCM(推荐,服务端免 ffmpeg)
///   2. WavBase64 —— 前端已自行转好的完整 WAV
/// </summary>
public sealed record AssessPronunciationBody(
    float[]? Samples,
    int? SampleRate,
    string? WavBase64,
    string? ReferenceText,
    string? Language);

// ---- /practice 页请求体(2026-09-15 第十七轮)----

/// <summary>整树提交的根。</summary>
public sealed record SaveTreeBody(IReadOnlyList<YourInterview.Services.Assessment.Application.MaterialNodeIn>? Nodes);

/// <summary>Azure Speech 设置提交。⚠️ 只入不出 —— 保存后绝不回传 key。</summary>
public sealed record SpeechSettingsBody(string Key, string Region, string? Endpoint);

/// <summary>
/// 测试一把**尚未保存**的候选凭据(先测后存)。
/// 与 /speech/settings 的 body 同形,但语义完全不同:这里只验证,不写库。
/// </summary>
public sealed record SpeechCredentialBody(string Key, string Region);

/// <summary>
/// 评分落库请求体。
/// 字段名与前端 snake→camel 对齐;可空维度保持可空 —— Free F0 拿不到就是 null,
/// **绝不用 0 填充**(0 分和"没这项"在界面上的含义完全不同)。
/// </summary>
public sealed record RecordingScoreBody(
    double? PronScore,
    double? AccuracyScore,
    double? FluencyScore,
    double? CompletenessScore,
    double? ProsodyScore,
    string? RecognizedText,
    string? ReferenceText,
    string? AzureRegion,
    List<WordScoreIn>? Words);

/// <summary>逐词评分明细。</summary>
public sealed record WordScoreIn(string Word, double Accuracy, string ErrorType);

/// <summary>
/// 示范朗读合成请求。
/// voice 为空用默认音色(en-US-AriaNeural);speed 1.0 = 原速。
/// </summary>
public sealed record TtsBody(string Text, string? Voice, double? Speed);

