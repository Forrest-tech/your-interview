using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Assessment.Domain;

namespace YourInterview.Services.Assessment.Infrastructure.Persistence;

/// <summary>
/// 模拟练习种子数据。
///
/// ⚠️ 纪律:这里**不伪造 Forrest 的回答原文**。
/// 模拟练习的"我的回答"必须是真人真答;编造的答案会让整个评分体系失去意义
/// (用户看到的第一条练习就是假数据,他会立刻不信任这个工具)。
///
/// 因此种子的定位是:
///   1. 建一个"已完成的历史练习"作为基线,展示六维评分与明细问题的呈现效果;
///   2. 回答内容取自**真实发生过的短板**(Gateway/CIBC/Geotab 复盘结论),
///      而不是凭空编一场面试 —— 这样即使文本是摘录,结论也是真的;
///   3. 另建一个"进行中"的练习,让新用户打开就能看见两种状态。
/// </summary>
public static class AssessmentDbSeeder
{
    /// <summary>
    /// 种子数据归属的用户。
    ///
    /// 这里**查真实的 Identity 用户表**而不是写死一个 GUID ——
    /// 写死的 ID 与 Identity 服务实际生成的 ID 不一致(Identity 用 Guid.NewGuid),
    /// 结果就是"种子数据进了库但按 UserId 过滤时一条都查不到"。
    /// 这是真实踩过的坑,所以宁可多一次跨 schema 查询。
    /// </summary>
    private static async Task<Guid?> ResolveSeedUserIdAsync(AssessmentDbContext db,
        IConfiguration config, ILogger logger)
    {
        var email = config["Seed:OwnerEmail"] ?? "admin@your-interview.local";

        try
        {
            // 用 EF 托管的连接(不要 await using —— 它属于 DbContext,
            // 提前 Dispose 会让后续 SaveChanges 失败,这是踩过的坑)。
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT ""Id"" FROM identity.users WHERE ""Email"" = @e LIMIT 1";
            var p = cmd.CreateParameter();
            p.ParameterName = "@e";
            p.Value = email;
            cmd.Parameters.Add(p);

            var result = await cmd.ExecuteScalarAsync();
            if (result is Guid g) return g;
            if (result is string str && Guid.TryParse(str, out var parsed)) return parsed;

            logger.LogWarning("找不到种子数据归属用户 {Email}", email);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "查询 Identity 用户失败 —— 跳过模拟练习种子");
            return null;
        }
    }

    public static async Task MigrateAndSeedAsync(IServiceProvider services, IConfiguration config,
        ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AssessmentDbContext>();

        await db.Database.MigrateAsync();

        if (!(config["Seed:CreateDemoData"] ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Assessment 数据库就绪(未写入演示数据)");
            return;
        }

        if (await db.Sessions.AnyAsync())
        {
            logger.LogInformation("Assessment 演示数据已存在,跳过");
            return;
        }

        var seedUserId = await ResolveSeedUserIdAsync(db, config, logger);
        if (seedUserId is null) return;
        var SeedUserId = seedUserId.Value;

        // =====================================================================
        // 练习一:已完成 —— 系统设计题(还原 Gateway 那次崩掉的那道题)
        // =====================================================================
        var design = new MockSession(SeedUserId,
            "系统设计强化 · 单体拆微服务", SessionMode.TopicDrill, "系统设计", null, 3, "en");

        var q1 = design.AddQuestion(
            "If you were to break this monolith into microservices, what steps would you take?",
            QuestionType.SystemDesign,
            """["先明确边界识别方法(DDD bounded context)","讲渐进迁移策略(strangler-fig)","讲数据解耦路径(shared db → per-service db)","必须给出 trade-off:不是所有都该拆","提到分布式事务/最终一致性的代价"]""",
            5);

        // 这里的回答文本是**复盘里记录的真实回答特征**(项目管理视角),不是逐字稿 —— 已在文档中标注。
        design.AnswerQuestion(q1.Id,
            "[复盘记录:当时用项目管理视角作答 —— 说派人去学、做 PoC、先做个 demo 给团队看," +
            "而没有给出架构层面的方案。此处保留问题本身与评分,回答原文见实战机经 Gateway 条目。]",
            durationSeconds: 165);

        design.ScoreQuestion(q1.Id,
            new DimensionScores(pronunciation: 90, fluency: 62, sentenceIntegrity: 60,
                structure: 35, technicalDepth: 38, relevance: 40),
            [
                new DimensionIssue("Structure", "没有架构方案骨架,讲了项目管理流程",
                    "回答落在\"派人学 / PoC / demo\"这类过程性描述上,没有进入技术方案层。",
                    "面试官连问 4-5 遍\"what steps\",说明他一直在等技术方案。",
                    "改用固定骨架:\"I'd start by mapping bounded contexts. First... Second... Third... The main trade-off is...\"",
                    5),
                new DimensionIssue("Relevance", "把 system design 题听成了项目经历题",
                    "题目问的是\"你会怎么做\",不是\"你过去怎么做的\"。两者答法完全不同。",
                    "听到 how would you / what steps / if you were to 就该切技术方案模式。",
                    "答题前先复述确认:\"So you're asking how I'd approach it, right?\" 再开始。",
                    5),
                new DimensionIssue("TechnicalDepth", "缺少数据解耦与迁移策略",
                    "没有提 strangler-fig、API 网关、shared db → per-service db、outbox/saga。",
                    "这些都是该题的得分点,一个都没讲到。",
                    "四个必讲点:边界识别(DDD)→ 渐进迁移(strangler-fig + 网关)→ 数据解耦(outbox/saga)→ 权衡(不是全拆)。",
                    5)
            ],
            comment: "问题本身听懂了,但答的不是这个维度。这类题考的是架构判断力,不是执行流程。" +
                     "最大的失分不是\"不知道\",而是\"知道的东西没按面试官要的框架组织出来\"。",
            recommendedAnswer:
                "The short answer is: I'd do it incrementally, not as a big-bang rewrite. " +
                "Three things I'd focus on. First, identify boundaries using DDD bounded contexts — " +
                "I look for parts of the model that have their own invariants and lifecycle, because those can be split safely. " +
                "Second, migrate gradually with a strangler-fig pattern behind an API gateway — " +
                "the monolith keeps serving traffic while I peel off one capability at a time, so there's never a flag day. " +
                "Third, decouple the data. A shared database is the real blocker — " +
                "each service gets its own schema, and I use an outbox pattern plus sagas for cross-service consistency. " +
                "The main trade-off is that this buys independent deployability at the cost of network latency, " +
                "distributed transactions, and much heavier ops. So I wouldn't split everything — " +
                "only the parts where independent scaling or deployment actually pays for that cost.",
            betterStructure:
                "结论先行 → \"incrementally, not big-bang\"\n" +
                "First: 边界识别(DDD bounded context)\n" +
                "Second: 渐进迁移(strangler-fig + API gateway)\n" +
                "Third: 数据解耦(outbox + saga)\n" +
                "Trade-off: 换来独立部署,代价是延迟/分布式事务/运维复杂度 → 所以不是全拆",
            fillerWordsJson: """{"um":7,"uh":4,"like":6}""");

        var q2 = design.AddQuestion(
            "Follow-up: how would you handle a transaction that spans two of those services?",
            QuestionType.SystemDesign,
            """["指出不能用分布式事务(2PC)做大范围强一致","讲 saga(编排 vs 编舞)","讲 outbox 保证消息与本地事务原子","讲幂等消费与补偿"]""",
            5);

        design.AnswerQuestion(q2.Id,
            "[复盘记录:这一追问没有答上来 —— 当时没有讲出 saga / outbox,回答偏向了\"用消息队列\"这个泛泛的方向。]",
            durationSeconds: 95);

        design.ScoreQuestion(q2.Id,
            new DimensionScores(88, 58, 55, 40, 30, 45),
            [
                new DimensionIssue("TechnicalDepth", "没有答出 saga / outbox",
                    "只说\"发消息\"就停下了,没有讲跨服务一致性到底怎么保证。",
                    "追问的本质是\"没有分布式事务怎么办\",答案必须是 saga + outbox。",
                    "背下来:Saga(编排/编舞)做业务补偿,Outbox 保证\"本地事务 + 发消息\"原子,消费端必须幂等。",
                    5),
                new DimensionIssue("Structure", "追问后直接进入零散细节,没有先给结论",
                    "面对追问容易越答越窄。应该先用一句话给出方向,再展开。",
                    "追问时先答:\"Not with a distributed transaction — I'd use a saga.\" 再解释。",
                    "任何追问都先给一句方向性结论,再进细节。",
                    4)
            ],
            comment: "这是真正的深水区:跨服务一致性。答不出 saga/outbox 是知识缺口,不是表达问题 —— " +
                     "这类缺口要靠技术栈模块的主动复习补,临时想不出来。",
            recommendedAnswer:
                "Not with a distributed transaction — two-phase commit across services is an anti-pattern " +
                "at this scale because it couples availability and usually doesn't survive a service being down. " +
                "I'd use a saga. Each service commits locally and publishes an event, " +
                "and there's a compensating action if a later step fails. " +
                "To make \"commit locally plus publish\" atomic, I use the outbox pattern — " +
                "the event is written to an outbox table in the same local transaction, " +
                "then a background publisher forwards it to the bus. " +
                "The receiving side has to be idempotent, because delivery is at-least-once. " +
                "The trade-off is that you get eventual consistency, not immediate consistency — " +
                "so the business has to tolerate a brief window where the two services disagree.",
            betterStructure:
                "先否定:\"Not with a distributed transaction.\"\n" +
                "给方案:Saga(本地事务 + 补偿)\n" +
                "保原子:Outbox(事件与本地事务同库同事务)\n" +
                "防重复:幂等消费(at-least-once)\n" +
                "Trade-off:最终一致,业务要能容忍短暂不一致",
            fillerWordsJson: """{"um":9,"uh":6}""");

        design.Complete(
            overallSummary:
                "本次练习暴露的是同一个根因:听懂了题目但答错了维度,以及在追问下缺少可展开的知识点。" +
                "两道题的发音都在 88-90 分(不是问题),但结构分 35-40、技术深度 30-38 —— " +
                "这与 Geotab 那场真实面试的结论完全一致:瓶颈在\"组织答案\"和\"技术纵深\",不在语音。",
            priorityAction:
                "只做一件事:把\"单体拆微服务 + 跨服务一致性\"这一组做成能脱口的固定骨架," +
                "练到不需要现场组织语言。每题都必须包含 First/Second/Third 与一句明确的 trade-off。");

        db.Sessions.Add(design);

        // =====================================================================
        // 练习二:进行中 —— 术语发音专项(源自 Geotab 报告的真实发现)
        // =====================================================================
        var pronoun = new MockSession(SeedUserId,
            "术语发音专项 · 技术词汇", SessionMode.TopicDrill, "发音", null, 5, "en");

        var p1 = pronoun.AddQuestion(
            "Please read this aloud and then explain it: \"We run a .NET stack with a monolithic " +
            "web tier, and I'm on-call for the vision pipeline.\"",
            QuestionType.Technical,
            """[".NET stack 读作 dot-net,不是 the stick","monolithic 重音在 -lith-","on-call 连读","vision 的 v 要咬唇","DealTrax 读作 deal-tracks"]""",
            2);

        pronoun.AnswerQuestion(p1.Id,
            "[语音回答 —— 音频与转写待补,由 Azure Speech 管线分析后回写]",
            audioPath: "面试录音评估/归档/术语跟读-练习.mp3",
            durationSeconds: 42);

        db.Sessions.Add(pronoun);

        await db.SaveChangesAsync();

        logger.LogInformation("Assessment 演示数据已写入:{Count} 个练习会话",
            await db.Sessions.CountAsync());
    }
}
