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

        // ★ 第四十九轮修复:此处原先只检查 Samples,导致 WavBase64 分支永远不可达
        //   (走 WavBase64 时 Samples 必为 null → 直接 400「音频为空」)。
        //   现在两种来源**任一有值**即可通过,与上面 DTO 注释里的「二选一」契约一致。
        var hasSamples = body.Samples is { Length: > 0 };
        var hasWavBase64 = !string.IsNullOrWhiteSpace(body.WavBase64);

        if (!hasSamples && !hasWavBase64)
            return Results.Problem(title: "音频为空",
                detail: "请先录音再提交评分。",
                statusCode: StatusCodes.Status400BadRequest);

        // 前端已解码成 Float32 PCM,这里装成 16k 单声道 WAV
        var wav = hasWavBase64
            ? Convert.FromBase64String(body.WavBase64!)
            : PcmWav.FromFloat32(body.Samples!, body.SampleRate ?? 48000);

        if (!PcmWav.IsWav(wav))
            return Results.Problem(title: "音频格式不支持",
                detail: "需要 PCM WAV,或改传 Float32 PCM 样本由服务端封装。",
                statusCode: StatusCodes.Status400BadRequest);

        try
        {
            // 传当前用户 id —— 让评估器解析"这个用户在设置页保存的 key"
            var result = await assessor.AssessAsync(wav, body.ReferenceText ?? string.Empty,
                body.Language ?? "en-US", ct, Me);

            // ★ 第三十四轮(Forrest:"给我准确的消耗了多少"):
            //   Azure 发音评估的真实计费单位是**音频时长**(按小时计价),不是 token。
            //   这个秒数从我们自己装的 WAV 头精确算出(字节率 × data 长度),
            //   不依赖 Azure 回报 —— 所以是精确值,不会因版本差异而缺失。
            //   拿不到就返回 null,不编造。
            var audioSeconds = PcmWav.DurationSeconds(wav);

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
                // 计费口径:本次送评的音频时长(秒)。精确值。
                billedSeconds = audioSeconds,
                billedBytes = wav.Length,
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
        var result = await sender.Send(new SaveMaterialTreeCommand(userId, body.Nodes ?? [], body.Force), ct);
        return result.IsSuccess ? Results.Ok(new { saved = result.Value })
                                : result.ToProblemDetails();
    }

    /// <summary>
    /// ★ 第四十三轮(Forrest):单独设置某素材的标记色 —— 点一下颜色立即落库,
    ///   不走整树 PUT(避免连带触发防误删熔断/软删对比)。
    ///   color: none/orange/red/green;none = 清除标记。
    /// </summary>
    public sealed record SetMarkBody(string? Color);

    [HttpPatch("materials/{materialId:guid}/mark")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> SetMaterialMark(Guid materialId, [FromBody] SetMarkBody body, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var result = await sender.Send(new SetMaterialMarkCommand(userId, materialId, body.Color), ct);
        return result.IsSuccess ? Results.Ok(new { saved = true })
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
    /// ★ 第三十九轮:列出**当前用户的全部录音**(不按素材过滤)。
    ///   用途:数据安全兼底 —— 即使某个素材被删/ID 变动导致前端按
    ///   materialId 查不到,用户的录音也不会"凭空消失"。
    ///   前端可用它做"孤儿录音"发现与恢复。
    /// </summary>
    [HttpGet("recordings")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> ListAllRecordings(CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var list = await sender.Send(new ListAllRecordingsQuery(userId), ct);
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
            body.AzureRegion ?? "canadacentral", body.ReferenceText ?? string.Empty,
            // ★ 第三十四轮:计费口径由前端回传(它刚从 /pronunciation/assess
            //   的响应里拿到 billedSeconds/billedBytes)——
            //   这样读库路径也能显示"当时花了多少",信息不残缺。
            body.BilledSeconds, body.BilledBytes), ct);
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
    /// 合成示范朗读音频(**带本地缓存** —— 2026-09-16 第三十一轮)。
    ///
    /// Forrest 需求第 3 条:
    ///   · 文本没改 → 直接回放上次合成的 MP3,**不再消耗 Azure TTS 额度**;
    ///   · 文本(或音色/倍速)变了 → 重新合成,并把新音频存下来;
    ///   · 需要强制重合成时传 force=true。
    ///
    /// 缓存键 = SHA-256(归一化文本 + 音色 + 倍速 + 语言),
    /// 音频落文件系统,库里只存路径 —— 与录音同一套存储约定。
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
            var r = await sender.Send(new GetOrCreateTtsCommand(
                Me, body.Text, body.Voice, body.Speed, body.Language ?? "en-US", body.Force), ct);

            if (!r.IsSuccess)
            {
                // "未配 key"是**环境尚未就绪**(503),不是上游拒绝凭据(502)
                var code = r.Error?.Code ?? string.Empty;
                if (code == "Tts.NotConfigured")
                    return Results.Problem(title: "Azure 语音未配置",
                        detail: r.Error?.Description, statusCode: StatusCodes.Status503ServiceUnavailable);
                return r.ToProblemDetails();
            }

            var audio = r.Value;
            // 返回音频流而不是 base64 —— 前端直接 new Audio(objectURL) 播,省一次解码。
            //
            // ★ 第三十四轮(Forrest:"给我准确的消耗了多少"):
            //   额度信息通过响应头下发 —— 这是**精确可考**的数字,不是估算:
            //     X-Tts-Cache       : hit | miss        来源(是否调了 Azure)
            //     X-Tts-Chars       : 本次实际计费字符数(hit 时为 0)
            //     X-Tts-Chars-Full  : 这段文本的完整字符数(即使命中也有值)
            //     X-Tts-Voice       : 实际使用的音色(未指定则服务端默认)
            //     X-Tts-Bytes       : 音频字节数(便于核对)
            //   ⚠️ 别把它写成"token" —— Azure 语音不以 token 计费,
            //      真实计费单位是合成字符数。报个假 token 数比不报更坏。
            //   ⚠️ 新增的头**必须**加进 Access-Control-Expose-Headers,
            //      否则前端跨域读不到且**不报错**(静默变 null)。
            Response.Headers["X-Tts-Cache"] = audio.FromCache ? "hit" : "miss";
            Response.Headers["X-Tts-Chars"] = audio.BilledChars.ToString();
            Response.Headers["X-Tts-Chars-Full"] = GetOrCreateTtsCommandHandler
                .BilledChars(body.Text).ToString();
            Response.Headers["X-Tts-Voice"] = audio.Voice;
            Response.Headers["X-Tts-Bytes"] = audio.AudioBytes.ToString();
            Response.Headers["Access-Control-Expose-Headers"] =
                "X-Tts-Cache, X-Tts-Chars, X-Tts-Chars-Full, X-Tts-Voice, X-Tts-Bytes";
            return Results.File(audio.Audio, audio.ContentType, $"tts-{Guid.NewGuid():N}.mp3");
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
    // ==================== LLM 设置(面试前准备包) ====================
    // 与 speech/* 严格同构:key 只入不出、保存前真测、凭据拒收映射 502。

    /// <summary>
    /// 查询 LLM 配置状态。
    /// ⚠️ **只返回** hasKey / 协议 / 模型 / 掩码,永远不下发 key 本身。
    /// </summary>
    [HttpGet("ai/settings")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> GetAiSetting(CancellationToken ct)
    {
        var status = await sender.Send(new GetAiSettingQuery(Me), ct);
        return Results.Ok(status);
    }

    /// <summary>列出内置 provider 预设与常用模型(供设置页下拉)。无密钥,可安全下发。</summary>
    // ======================= 简历正文(2026-09-18)=======================
    // 用途:简历匹配分析(简历 vs JD 关键词比对)+ 面试前准备包的输入。
    //
    // 权限沿用 MockRead/MockManage —— 简历与 AI 练习同属"面试准备"域,
    // 不另起一套权限,避免用户要在两个地方各配一次。

    /// <summary>
    /// 读当前用户的简历正文。
    /// 未设置时返回 resumeText = null(前端据此提示"先录入简历"),
    /// 而不是 404 —— 404 会被前端当成错误弹出提示条。
    /// </summary>
    [HttpGet("resume-text")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> GetResumeText(CancellationToken ct)
    {
        var r = await sender.Send(new GetResumeQuery(Me), ct);
        return r.IsSuccess
            ? Results.Ok(new { resumeText = r.Value.Content, version = r.Value.Version,
                               updatedAt = r.Value.UpdatedAt })
            : r.ToProblemDetails();
    }

    /// <summary>存/覆盖当前用户的简历正文(版本号自增)。</summary>
    [HttpPut("resume-text")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> SaveResumeText([FromBody] ResumeTextBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SaveResumeCommand(Me, body.ResumeText ?? string.Empty), ct);
        return r.IsSuccess
            ? Results.Ok(new { version = r.Value.Version, updatedAt = r.Value.UpdatedAt })
            : r.ToProblemDetails();
    }

    /// <summary>清空简历(回到未设置状态)。</summary>
    [HttpDelete("resume-text")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> DeleteResumeText(CancellationToken ct)
    {
        var r = await sender.Send(new DeleteResumeCommand(Me), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    [HttpGet("ai/providers")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> ListAiProviders(CancellationToken ct)
    {
        var list = await sender.Send(new ListAiProvidersQuery(), ct);
        return Results.Ok(list);
    }

    /// <summary>
    /// 测试一把**尚未保存**的候选凭据。不读库、不写库。
    /// 前端流程:填 key → 调本端点 → 通过才调 PUT /ai/settings 保存。
    /// 失败一律映射 502(上游拒收凭据),绝不用 401/403(前端会当会话失效而登出)。
    /// </summary>
    [HttpPost("ai/test-credential")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> TestAiCredential([FromBody] AiCredentialBody body, CancellationToken ct)
    {
        var r = await sender.Send(new TestAiCredentialCommand(body.Protocol, body.ApiKey ?? string.Empty,
            body.BaseUrl, body.Model ?? string.Empty, body.Endpoint, body.ApiVersion), ct);

        if (r.IsSuccess)
            return Results.Ok(new { ok = true, message = r.Value.Message, sampleOutput = r.Value.SampleOutput });

        var code = r.Error?.Code ?? string.Empty;
        var isValidation = code is "Ai.KeyEmpty" or "Ai.ModelEmpty" or "Ai.BaseUrlEmpty" or "Ai.EndpointEmpty";
        return Results.Problem(
            title: isValidation ? "填写不完整" : "凭据无效",
            detail: r.Error?.Description ?? "凭据验证失败。",
            statusCode: isValidation
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status502BadGateway,
            extensions: new Dictionary<string, object?> { ["code"] = code });
    }

    /// <summary>保存 LLM 配置(服务端保管)。保存前会真测一次,不通不入库。</summary>
    [HttpPut("ai/settings")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> SaveAiSetting([FromBody] AiSettingsBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SaveAiSettingCommand(Me, body.Protocol, body.ApiKey ?? string.Empty,
            body.BaseUrl, body.Model ?? string.Empty, body.Endpoint, body.ApiVersion, body.DisplayName), ct);

        if (r.IsSuccess) return Results.Ok(r.Value);

        // 凭据被上游拒收 → 502,不能落到默认分支变 500,更不能是 401/403。
        var code = r.Error?.Code ?? string.Empty;
        if (code is "Ai.CredentialRejected" or "Ai.ProbeUnexpected")
        {
            return Results.Problem(
                title: "凭据验证失败",
                detail: r.Error?.Description ?? "凭据验证失败,未保存。",
                statusCode: StatusCodes.Status502BadGateway,
                extensions: new Dictionary<string, object?> { ["code"] = code });
        }

        return r.ToProblemDetails();
    }

    /// <summary>删除 LLM 配置(回到未配置状态)。</summary>
    [HttpDelete("ai/settings")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> DeleteAiSetting(CancellationToken ct)
    {
        var r = await sender.Send(new DeleteAiSettingCommand(Me), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }


/// <summary>简历正文提交。空串由 Handler 归一为"清空"。</summary>
public sealed record ResumeTextBody(string? ResumeText);
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
public sealed record SaveTreeBody(IReadOnlyList<YourInterview.Services.Assessment.Application.MaterialNodeIn>? Nodes, bool Force = false);

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
    List<WordScoreIn>? Words,
    // ★ 第三十四轮:评分时的音频时长(秒)/字节数 —— 由前端从
    //   /pronunciation/assess 的响应回传,用于在读数路径重现"花了多少"。
    double? BilledSeconds = null,
    int? BilledBytes = null);

/// <summary>逐词评分明细。</summary>
public sealed record WordScoreIn(string Word, double Accuracy, string ErrorType);

/// <summary>
/// 示范朗读合成请求。
/// voice 为空用默认音色(en-US-AriaNeural);speed 1.0 = 原速。
/// force=true 时忽略本地缓存,强制重新合成(会消耗额度)。
/// </summary>
public sealed record TtsBody(string Text, string? Voice, double? Speed, bool Force = false,
    string? Language = null);

/// <summary>LLM 设置提交。⚠️ 只入不出 —— 保存后绝不回传 key。</summary>
public sealed record AiSettingsBody(string Protocol, string? ApiKey, string? BaseUrl, string? Model,
    string? Endpoint, string? ApiVersion, string? DisplayName);

/// <summary>
/// 测试一把尚未保存的候选凭据(先测后存)。
/// 与 AiSettingsBody 同形,但语义完全不同:这里只验证,不写库。
/// </summary>
public sealed record AiCredentialBody(string Protocol, string? ApiKey, string? BaseUrl, string? Model,
    string? Endpoint, string? ApiVersion);
