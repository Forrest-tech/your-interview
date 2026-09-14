using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Knowledge.Domain;

namespace YourInterview.Services.Knowledge.Infrastructure.Persistence;

/// <summary>
/// 技术栈知识库种子数据。
///
/// 数据来源:Forrest 自己整理的《.NET 技术知识库》(workspace/NET技术知识库.md),
/// 以及历次面试复盘中"没答好的题"。这里只灌入**已经过验证的真实内容**,
/// 不编造英文原文对话。
///
/// 为什么种子数据这么认真:这个模块的价值就在于"开箱有货"。
/// 一个空的知识库页面是没有说服力的 —— 用户打开就该看见自己积累的所有知识点。
/// </summary>
public static class KnowledgeDbSeeder
{
    public static async Task MigrateAndSeedAsync(IServiceProvider services, IConfiguration config,
        ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();

        await db.Database.MigrateAsync();

        if (!(config["Seed:CreateDemoData"] ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Knowledge 数据库就绪(未写入演示数据)");
            return;
        }

        if (await db.Items.AnyAsync())
        {
            logger.LogInformation("Knowledge 演示数据已存在,跳过");
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // ---------- 一、概念百科:来自 NET技术知识库.md 的 26 个条目 ----------
        var concepts = new (string Topic, string Title, string Question, int Diff, int Imp,
            MasteryLevel Mastery, string Explain, string[] KeyPoints, string[] Pitfalls,
            string[] Refs)[]
        {
            ("架构", "Clean Architecture(整洁架构)", "Talk me through your Clean Architecture.", 4, 5,
                MasteryLevel.Familiar,
                "依赖规则:源代码依赖只能由外向内。最内层是领域(纯业务规则,不认识 EF/SQL/HTTP),其外是应用层(用例编排 + 定义仓储接口),再外是基础设施(EF Core/消息/外部调用),最外是 API(薄翻译层)。好处:换数据库/框架只动外层;测业务规则不用起库。",
                ["依赖向内(Dependency Rule)", "接口定义在应用/领域侧,不由 Infrastructure 反向定义", "Controller 是翻译器,零业务逻辑"],
                ["把\"分了几层\"当 Clean —— 老项目也分层但允许上层直接 new 数据库对象", "Domain 直接暴露给 API,业务字段被 HTTP 层绕过规则"],
                ["Robert C. Martin《Clean Architecture》(2017)", "Ports & Adapters(Hexagonal)"]),

            ("架构", "DDD 与 Bounded Context(限界上下文)", "How did DDD help you decide where to split services?", 4, 5,
                MasteryLevel.Familiar,
                "DDD 两句话:模型要表达真实业务规则和词汇;同一个词在不同业务区含义不同,别硬做统一超级模型。Bounded Context 是最有用的实操:每个业务边界内部各自定义模型和不变量,成员用统一说法。找微服务边界的主答案就是:两个模型若有各自独立的不变量、独立生命周期、由不同团队演进,就该是不同 bounded context。",
                ["独立的不变量 → 独立的上下文", "统一语言(Ubiquitous Language)", "防腐层接住外部模型"],
                ["DDD ≠ 微服务:DDD 是建模思想,微服务是部署形态", "别把一切叫 Aggregate"],
                ["Eric Evans《Domain-Driven Design》(2003)", "Martin Fowler 关于 DDD/CQRS 的文章"]),

            ("架构", "CQRS(命令查询职责分离)", "What is CQRS and when would you use it?", 4, 4,
                MasteryLevel.Familiar,
                "把\"写\"和\"读\"拆成两条路:命令侧走领域模型保证不变量,查询侧直接查优化过的读模型(甚至不经过领域)。适合读写负载差异大、查询形态复杂的场景。代价:最终一致性、需要维护两套模型。",
                ["写走聚合保证不变量,读走投影(可能直接 SQL)", "读写负载不对称时才值得", "代价是最终一致性与复杂度"],
                ["不是为了 CQRS 而 CQRS —— 简单 CRUD 用它是自找麻烦", "读写库分离后忘了处理延迟"],
                ["Martin Fowler, CQRS(2011)", "Greg Young 的 CQRS 文档"]),

            ("架构", "MediatR / 中介者模式", "How does MediatR help you keep controllers thin?", 3, 4,
                MasteryLevel.Learning,
                "中介者模式在 .NET 的落地:Controller 只发一个 Command/Query 对象,由 MediatR 路由到对应 Handler。好处:Controller 变薄、每个用例一个 Handler 单一职责、可在管道(Pipeline Behavior)里统一插入日志/校验/性能监控/事务。",
                ["一个用例一个 Handler", "Pipeline Behavior = 切面(AOP)的优雅实现", "校验/日志/性能/异常都走管道"],
                ["用 MediatR 不等于架构变好 —— 它只是解耦手段", "Handler 里又注入一堆服务变成新的上帝类"],
                ["Jimmy Bogard, MediatR(GitHub)", "Mediator Pattern(GoF)"]),

            ("架构", "Repository + Unit of Work(仓储 + 工作单元)", "Why do you wrap EF Core in a Repository?", 3, 4,
                MasteryLevel.Familiar,
                "Repository 把持久化细节藏在接口后面(领域只认识 IOrderRepository),Unit of Work 保证一次业务操作里的多步改动能原子提交。EF Core 的 DbContext 本身就是 Unit of Work,DbSet 就是 Repository。",
                ["接口定义在应用层,实现放基础设施", "DbContext 天然是 Unit of Work", "避免领域层依赖 ORM"],
                ["无脑包一层 Repository 但接口就是 DbSet 的镜子 —— 白加复杂度", "忘了 Repository 会隐藏 IQueryable 带来的查询灵活性"],
                ["Martin Fowler, PoEAA:Repository / Unit of Work", "MS Learn: EF Core 中的 DbContext"]),

            ("EF Core", "EF Core 查询与调优", "How do you diagnose and fix a slow query in EF Core?", 4, 5,
                MasteryLevel.Learning,
                "先量再改:打开 EF 的 SQL 日志或用 Application Insights 看到真实 SQL 与耗时。常见问题:① N+1(循环里查关联)→ 用 Include/投影一次性取;② 取回整表却只用两列 → 用 Select 投影;③ 追踪开销大 → 只读查询用 AsNoTracking;④ 大结果集 → 分页(keyset 比 OFFSET 更好);⑤ 缺索引 → 按执行计划加。",
                ["先看真实 SQL 再优化,不要凭感觉", "N+1 是最常见杀手", "AsNoTracking / Select 投影 / 分页是三大常用手段"],
                ["以为 Include 越多越好 —— 会产生笛卡尔积爆炸", "在生产用 ToList() 拉全表到内存再过滤"],
                ["MS Learn: EF Core 性能", "MS Learn: 高效查询"]),

            ("数据库", "SQL Server 内部机制(索引/执行计划/事务/并发)", "How does SQL Server execute a query?", 4, 5,
                MasteryLevel.New,
                "SQL Server 解析 → 优化器生成执行计划 → 存储引擎按计划取数据。索引(B 树)决定是 Seek(定位)还是 Scan(全扫)。事务由锁保证 ACID,隔离级别决定并发行为(SQL Server 默认 Read Committed,可能读到已提交的新值即不可重复读;RCSI 用行版本避免读阻塞写)。",
                ["Seek 优于 Scan", "看执行计划找 Table Scan / Key Lookup", "隔离级别决定并发代价"],
                ["用 NOLOCK 当性能银弹 —— 会读到脏数据", "只看单次执行时间不看逻辑读次数"],
                ["MS Learn: SQL Server 执行计划", "MS Learn: 事务隔离级别"]),

            ("云原生", "Cloud-Native 与 Azure 计算服务", "Where would you host a .NET microservice on Azure?", 3, 4,
                MasteryLevel.Learning,
                "App Service 最省心(托管 PaaS,适合常规 Web API);Container Apps 适合容器化且要弹性伸缩、事件驱动的场景;AKS 适合需要精细控制 K8s 能力的团队但运维成本最高。选择依据:控制力 vs 运维成本的权衡。",
                ["App Service → 最省心", "Container Apps → 容器 + 弹性 + 少运维", "AKS → 控制力最强但最贵"],
                ["一上来就上 AKS —— 团队没有 K8s 运维能力是灾难", "忽略了冷启动与最小实例数的成本"],
                ["MS Learn: Azure 计算服务选择", "CNCF Cloud Native 定义"]),

            ("云原生", "Azure SQL Database(托管关系库)", "When would you pick Azure SQL over self-hosted SQL Server?", 3, 4,
                MasteryLevel.Learning,
                "托管数据库:微软负责补丁、备份、高可用;你负责 schema 和查询。Serverless 层可按用量自动暂停省钱,超大规模(Hyperscale)支持快速扩容。代价:部分服务器级功能不可用、要按 DTU/vCore 付费。",
                ["托管 = 少运维、高可用内置", "Serverless 可自动暂停", "代价是控制力与成本模型"],
                ["把 SQL Server 的某些服务器级特性假设它也有", "忘了连接要用托管身份而不是明文密码"],
                ["MS Learn: Azure SQL Database", "MS Learn: 购买模型 DTU vs vCore"]),

            ("云原生", "Azure Service Bus(消息队列/发布订阅)", "Why use Service Bus instead of direct HTTP calls?", 3, 4,
                MasteryLevel.Learning,
                "异步解耦 + 削峰填谷 + 可靠投递。Queue 是点对点(竞争消费者),Topic 是发布订阅(一份消息多方消费)。关键能力:死信队列(处理不了的消息不丢)、会话(有序)、重复检测(幂等)。",
                ["Queue 点对点 / Topic 发布订阅", "死信队列是可靠性核心", "异步解耦换来了最终一致性"],
                ["把消息队列当 RPC 用 —— 失去了异步的意义", "没有幂等消费导致重复处理"],
                ["MS Learn: Service Bus", "MS Learn: 消息传递模式"]),

            ("云原生", "Azure Storage(Blob/Table/Queue/File)", "How would you store interview recordings?", 2, 4,
                MasteryLevel.Familiar,
                "Blob 存非结构化(录音、图片、文档),Table 存结构化 KV,Queue 存简单消息,File 是 SMB 共享。Blob 分层(Hot/Cool/Archive)可显著省成本 —— 老录音转 Cool 层。",
                ["Blob = 对象存储,适合大文件", "分层存储能省钱", "SAS 令牌做临时授权访问"],
                ["把大文件塞进数据库", "公开 Blob 容器导致数据泄露"],
                ["MS Learn: Azure Storage", "MS Learn: Blob 访问层"]),

            ("安全", "Entra ID / OIDC / OAuth2 身份认证", "How does your API authenticate users?", 3, 4,
                MasteryLevel.Familiar,
                "OAuth2 是授权框架(拿 access token),OIDC 在其上加身份层(拿 ID token)。流程:客户端跳到 Entra ID 登录 → 拿授权码 → 换 token → 带 Bearer token 调 API → API 用公钥验签并检查 scope/role。",
                ["OAuth2 管授权,OIDC 管身份", "JWT 用公钥验签,不查库", "scope/role 决定能调什么"],
                ["把 access token 当会话用还存起来", "只验签不验过期/受众"],
                ["RFC 6749(OAuth2)", "OpenID Connect Core 1.0", "MS Learn: Microsoft identity platform"]),

            ("安全", "Key Vault + Managed Identity", "How do you keep secrets out of appsettings.json?", 3, 4,
                MasteryLevel.Familiar,
                "密钥集中放 Key Vault,应用用托管身份(Managed Identity)向 AAD 证明\"我是谁\"后直接读,代码里零密钥。轮换时只改 Key Vault,应用无感。",
                ["代码零密钥", "托管身份免去管理凭证", "轮换无感"],
                ["把连接字符串写在配置文件里提交到 Git", "给应用过大的 Key Vault 权限"],
                ["MS Learn: Key Vault", "MS Learn: 托管身份"]),

            ("可观测性", "Application Insights(可观测性/APM)", "How do you know something broke in production?", 3, 4,
                MasteryLevel.Familiar,
                "三大支柱:日志(Serilog → App Insights)、指标(请求率/错误率/依赖延迟)、分布式追踪(W3C traceparent 串起跨服务调用)。配合告警规则主动发现,而不是等用户投诉。",
                ["日志 + 指标 + 链路追踪", "traceparent 串起微服务", "告警要基于 SLO 而不是拍脑袋"],
                ["日志里泄露 PII", "只记异常不记上下文导致查不出来"],
                ["MS Learn: Application Insights", "OpenTelemetry 规范"]),

            ("容器", "容器与 Docker", "Why containerize this service?", 3, 4,
                MasteryLevel.Learning,
                "容器把应用 + 依赖打包成一致的运行单元,解决\"我机器上能跑\"的问题。多阶段构建(构建阶段有 SDK,运行阶段只有 runtime)能显著减小镜像并降低攻击面。",
                ["一致性:开发/测试/生产同镜像", "多阶段构建减小体积", "非 root 运行提升安全性"],
                ["镜像里塞了 SDK 和源码", "以 root 跑容器"],
                ["Docker 官方文档", "MS Learn: 容器化 .NET 应用"]),

            ("容器", "Kubernetes 与 Helm", "How do you deploy and upgrade this on Kubernetes?", 4, 4,
                MasteryLevel.New,
                "K8s 编排容器:Deployment 管副本与滚动升级,Service 给稳定入口,Ingress 对外暴露,ConfigMap/Secret 注入配置。liveness/readiness 探针决定健康判定与流量切换。Helm 把一堆 manifest 参数化打包,便于多环境部署。",
                ["Deployment + Service + Ingress 三件套", "探针决定流量切换时机", "Helm 参数化多环境"],
                ["没配 readiness 就开始接流量 → 升级时用户看到 502", "资源 requests/limits 不设导致节点雪崩"],
                ["k8s.io 官方文档", "helm.sh 官方文档"]),

            ("DevOps", "CI/CD 管道", "Walk me through your CI/CD pipeline.", 3, 4,
                MasteryLevel.Familiar,
                "CI:每次 push 触发构建 + 单测 + 静态扫描 + 镜像构建。CD:把镜像推到注册表 → 按环境渐进部署(先 staging 后 prod)→ 自动迁移数据库 → 冒烟测试 → 可回滚。关键原则:构建一次,各处部署同一产物。",
                ["构建一次,多环境部署同一产物", "数据库迁移要可回滚", "自动化测试是 CI 的门槛"],
                ["每个环境重新构建 → 产物不一致", "数据库迁移没有回滚脚本"],
                ["GitHub Actions 文档", "MS Learn: .NET CI/CD"]),

            ("DevOps", "生产环境可观测性运维", "How do you troubleshoot a production incident?", 4, 4,
                MasteryLevel.Learning,
                "先止血再找根因:回滚/限流/扩容让用户先恢复,然后看追踪链路定位到具体服务与依赖,再看该服务的日志与指标异常点。事后写复盘并补上防复发的测试或告警。",
                ["先止血(回滚)再定位", "用 trace id 串起跨服务调用", "事后要补防复发的机制"],
                ["在生产直接改代码热修", "不复盘导致同一个坑踩多次"],
                ["Google SRE Book", "MS Learn: 云应用最佳实践"]),

            ("C#/.NET", ".NET 8+ / C# 12 现代特性", "What .NET 8 features do you actually use?", 3, 5,
                MasteryLevel.Familiar,
                "record(不可变值语义 DTO/领域事件)、主构造器(减少样板)、可空引用类型(编译期抓空引用)、集合表达式、源生成器(编译期生成减少反射开销)、最小 API。",
                ["record 天然适合 DTO 与领域事件", "NRT 把空引用错误提前到编译期", "源生成器减少运行时反射"],
                ["滥用 record 做可变实体", "开了 NRT 却到处用 ! 压制警告"],
                ["MS Learn: C# 12 新特性", "MS Learn: .NET 8 新增功能"]),

            ("安全", "数据安全与 PII 处理", "How do you handle PII in your system?", 4, 4,
                MasteryLevel.Learning,
                "识别 PII(姓名/邮箱/身份证/录音)→ 最小化收集 → 传输加密(TLS)+ 存储加密 → 访问按最小权限 → 日志脱敏 → 有保留期限与删除机制。加拿大要留意 PIPEDA,欧盟是 GDPR。",
                ["最小化收集 + 明确保留期", "日志脱敏(录音转写最容易漏)", "权限最小化 + 审计"],
                ["把 PII 打进日志当调试信息", "没有删除机制,数据永久留存"],
                ["PIPEDA(加拿大)", "GDPR(EU)", "OWASP Top 10: 敏感数据暴露"]),

            ("安全", "面向安全的 Code Review", "How do you review code for security?", 3, 4,
                MasteryLevel.Learning,
                "固定清单式检查:输入校验与参数化查询(防注入)、鉴权与越权(每个端点都要问\"这个用户能访问这条数据吗\")、密钥是否硬编码、依赖是否有已知漏洞、错误信息是否泄露内部细节、AI 生成的代码是否引入了幻觉库或过宽权限。",
                ["每个端点都问\"会越权吗\"", "参数化查询防注入", "AI 生成的代码要额外审依赖与权限"],
                ["只审风格不审安全", "信任 AI 生成的代码直接合入"],
                ["OWASP Top 10", "OWASP ASVS"]),

            ("AI", "AI 开发工具的日常使用", "How do you use AI in your daily work?", 2, 4,
                MasteryLevel.Familiar,
                "真实口径:主力用 Claude Code 做重构、写测试、读陌生代码库;用 Copilot 类工具做行内补全。关键是\"我审它的产出\"——尤其是权限、依赖、边界条件这些 AI 容易想当然的地方。",
                ["AI 提速模板化工作", "人负责审业务正确性与安全边界", "不把决策责任交给 AI"],
                ["说\"代码是 AI 写的\"来回避理解", "不审 AI 产出直接合入"],
                ["GitHub Copilot 文档", "Anthropic Claude Code 文档"]),

            ("软技能", "Mentoring 与工程标准", "How have you mentored junior developers?", 3, 4,
                MasteryLevel.Learning,
                "三层:① 上手期给明确的第一个任务 + 结对;② 成长期做 code review 时讲\"为什么\"而不只改结果;③ 独立期给 ownership 并当后盾。工程标准靠机制而非口号:统一模板、CI 卡口、review checklist。",
                ["讲\"为什么\"而不只改结果", "标准靠机制(CI/模板)落地", "逐步放权给 ownership"],
                ["只派活不解释", "标准只写在文档里没人执行"],
                ["Google Engineering Practices", "《The Manager's Path》"]),

            ("软技能", "Full Technical Ownership", "Tell me about a time you owned something end to end.", 3, 5,
                MasteryLevel.Familiar,
                "从需求澄清 → 设计 → 实现 → 测试 → 部署 → 监控 → 后续迭代全程负责,而不是\"我只写我的那一块\"。判断标准:production 出事第一个想到的是你,而不是\"这不是我的模块\"。",
                ["端到端负责,包括生产","需求澄清也是自己的活","有主人翁心态而非任务心态"],
                ["只做被分配的 ticket", "上线后不管运行状况"],
                ["《The Pragmatic Programmer》", "Amazon Ownership 领导力准则"]),

            ("前端", "现代 Angular 与 RxJS", "What's the difference between switchMap, mergeMap, concatMap and exhaustMap?", 5, 5,
                MasteryLevel.New,
                "四个高阶映射操作符的区别只在\"上游来新值时,已在飞的下游请求怎么办\":switchMap 取消旧的(适合搜索/路由参数)、mergeMap 并发全部(适合互不依赖的写操作)、concatMap 排队保序(适合必须按顺序的写操作)、exhaustMap 忽略新值直到当前完成(适合防重复提交/登录按钮)。另外内存泄漏防治:takeUntil(ngOnDestroy)/takeUntilDestroyed/async pipe 自动退订。",
                ["switchMap 取消 / mergeMap 并发 / concatMap 排队 / exhaustMap 忽略", "搜索用 switchMap,防重复提交用 exhaustMap", "内存泄漏靠 takeUntil/async pipe 自动退订"],
                ["用 mergeMap 做表单提交导致乱序", "订阅了不取消导致内存泄漏", "把 RxJS 操作符和模板语法混为一谈"],
                ["RxJS 官方文档", "Angular 官方文档: RxJS 与信号"]),

            ("C#/.NET", "多线程与 C# 并发原语", "How do you write thread-safe code in C#?", 4, 5,
                MasteryLevel.Learning,
                "区分两个概念:并发(concurrency)= 同时处理多件事;并行(parallelism)= 同时做多件事。工具分层:Task/async-await 做 IO 密集;Thread 做 CPU 密集长任务;同步用 lock/Monitor/SemaphoreSlim(后者可异步等待);跨线程共享数据用 ConcurrentDictionary/Interlocked/Channel。",
                ["async-await 用于 IO 密集,不是\"开线程\"", "SemaphoreSlim 支持异步等待,lock 不支持", "共享状态才需要锁,先想能不能不共享"],
                ["在 async 方法里用 lock 包 await", "把多线程和消息队列混为一谈 —— 前者是进程内并发,后者是跨进程解耦"],
                ["MS Learn: 托管线程处理", "MS Learn: 异步编程模式"]),

            ("API", "RESTful API 设计", "How do you design a REST API?", 3, 5,
                MasteryLevel.Familiar,
                "资源用名词复数(/orders),动作用 HTTP 方法(GET 查 / POST 建 / PUT 全量改 / PATCH 局部改 / DELETE 删),状态码表达结果(201 创建、204 无内容、400 参数错、401 未认证、403 无权、404 不存在、409 冲突、422 语义错)。分页用 page/pageSize 或游标,过滤用 query string。",
                ["资源是名词,动作是方法", "状态码要准确表达语义", "分页/过滤/排序是标配"],
                ["用 GET 做写操作", "所有错误都返回 200 带 error 字段"],
                ["RFC 9110(HTTP 语义)", "Microsoft REST API 指南"]),

            ("API", "API 版本控制与向后兼容", "How do you version an API without breaking clients?", 3, 4,
                MasteryLevel.Learning,
                "三种方式:URL 路径(/v1/orders)、请求头、媒体类型。最实用是 URL 路径。兼容原则:只加不删、新字段可选、不改变已有字段语义、弃用要先公告再给过渡期。",
                ["只加不删是兼容的核心", "弃用要有公告期", "URL 路径版本最直观"],
                ["直接改字段类型导致老客户端解析失败", "没有版本直接大改"],
                ["MS Learn: API 版本控制", "Stripe API 版本策略"]),

            ("API", "一致错误模型与 ProblemDetails", "How do you return errors from your API?", 3, 4,
                MasteryLevel.Familiar,
                "用 RFC 9457(原 7807)ProblemDetails 统一格式:type(错误类型 URI)、title、status、detail、instance,再自定义扩展字段(如 errorCode、traceId)。好处:客户端一套解析逻辑、traceId 可直接关联到后端日志。",
                ["ProblemDetails 是标准格式", "traceId 让用户报错可直接定位", "全站一致比格式完美更重要"],
                ["每个接口错误格式都不一样", "把内部异常堆栈返回给客户端"],
                ["RFC 9457", "MS Learn: ASP.NET Core 中的错误处理"]),

            ("架构", "外部数据/API 集成", "How do you integrate with a third-party API?", 3, 4,
                MasteryLevel.Learning,
                "动手前先问清:认证方式?限流配额?分页/增量拉取机制?数据量和更新频率?失败重试与幂等?字段映射与单位?错误码含义?对账机制?工程上:用 Polly 做重试+熔断,把外部 DTO 映射成自己的模型(防腐层),拉取用游标增量而非全量。",
                ["先问清八件事再动手", "Polly 做重试与熔断", "防腐层隔离外部模型"],
                ["直接拿外部 DTO 当自己的领域模型", "全量拉取导致限流"],
                ["Polly 官方文档", "Martin Fowler: Circuit Breaker"])
        };

        foreach (var c in concepts)
        {
            var item = new KnowledgeItem(c.Title, c.Topic, c.Question,
                KnowledgeSource.Personal, difficulty: c.Diff, importance: c.Imp);

            item.UpdateDetails(c.Title, c.Topic, null, c.Question, c.Diff, c.Imp,
                JsonSerializer.Serialize(new[] { c.Topic }),
                c.Explain, null, null,
                JsonSerializer.Serialize(c.KeyPoints),
                JsonSerializer.Serialize(c.Pitfalls),
                null);

            foreach (var r in c.Refs) item.AddReference(r);
            item.PromoteMastery(c.Mastery);

            db.Items.Add(item);
        }

        logger.LogInformation("Knowledge 概念百科写入:{Count} 条", concepts.Length);

        // ---------- 二、来自实战机经的知识点:历次面试中没答好的题 ----------
        var fromInterviews = new (string Company, DateOnly Date, string Topic, string Title,
            string Question, int Diff, string[] KeyPoints)[]
        {
            ("Gateway Services", new DateOnly(2026, 8, 19), "架构",
                "单体应用拆微服务的方法论", "If you were to break this monolith into microservices, what steps would you take?",
                5, ["map monolith → 找 bounded context(DDD)", "strangler-fig + API 网关渐进迁移", "数据解耦:shared db → per-service db,配合 outbox/saga", "权衡:不是所有都拆 —— 网络延迟/分布式事务/运维成本都是代价"]),

            ("Gateway Services", new DateOnly(2026, 8, 19), "数据库",
                "大数据集下的查询优化手段", "How would you optimize queries against a very large table?",
                5, ["先量:看执行计划与逻辑读", "索引:覆盖索引消除 Key Lookup", "分页:keyset 分页优于 OFFSET(深分页)", "读写分离/分区表/物化视图", "应用层:IAsyncEnumerable 流式处理避免全量入内存"]),

            ("CIBC", new DateOnly(2026, 9, 1), "前端",
                "RxJS 高阶映射操作符的区别", "What's the difference between switchMap, mergeMap, concatMap and exhaustMap?",
                5, ["区别只在\"上游来新值时已在下游的请求怎么办\"", "switchMap 取消旧值(搜索)", "mergeMap 并发全部(互不依赖)", "concatMap 排队保序(顺序写)", "exhaustMap 忽略新值(防重复提交)"]),

            ("CIBC", new DateOnly(2026, 9, 1), "前端",
                "Angular 内存泄漏防治", "How do you prevent memory leaks in Angular?",
                4, ["takeUntil(ngOnDestroy) 手动退订", "takeUntilDestroyed(Angular 16+)", "async pipe 自动退订(首选)", "避免在订阅里再订阅"]),

            ("CIBC", new DateOnly(2026, 9, 1), "C#/.NET",
                "多线程 vs 消息队列的区别", "How do you handle concurrency in a .NET service?",
                4, ["多线程是进程内并发(Thread/Task/lock/SemaphoreSlim/并发集合)", "消息队列是跨进程解耦(Service Bus/RabbitMQ)", "两者解决的不是同一个问题,别混答", "共享状态才需要锁 —— 先问能不能不共享"]),

            ("CIBC", new DateOnly(2026, 9, 1), "API",
                "银行 API 安全实践", "How do you secure a banking API?",
                4, ["IP 白名单 + 双向 TLS", "API 网关(APIM)做认证/限流/审计", "OAuth2 client credentials + 最小 scope", "字段级加密与脱敏日志"]),

            ("Pack-Smart (Vision Systems)", new DateOnly(2026, 9, 3), "C#/.NET",
                "C++ 桌面程序 Web 化迁移到 .NET Core", "How would you migrate a C++ desktop tracking app to a web-based .NET Core solution?",
                5, ["先切边界:算法核 vs UI vs 硬件 IO", "算法核用 P/Invoke 或 C++/CLI 包成 .NET 库,先保功能不变", "UI 换 Angular/Blazor,后端 SignalR 推实时跟踪数据", "硬件层保留 native,通过 gRPC/共享内存桥接", "分阶段:先 web 可看,再 web 可控"]),

            ("Pack-Smart (Vision Systems)", new DateOnly(2026, 9, 3), "C#/.NET",
                "C# 性能优化的着手点", "Where do you look first when optimizing C# code?",
                4, ["先测量(profiler/benchmark)再改", "减少分配:用 Span/ArrayPool/StringBuilder", "避免不必要的装箱与 LINQ 链", "异步 IO 而非阻塞线程", "缓存热点计算"]),

            ("Rentsync", new DateOnly(2026, 9, 2), "云原生",
                "容器化与编排的实际落地", "How do you run this in production?", 4,
                ["多阶段构建 + 非 root 运行", "健康探针(liveness/readiness)决定流量切换", "配置走 ConfigMap/Secret,不烘进镜像", "Helm 参数化多环境"]),

            ("Geotab", new DateOnly(2026, 9, 13), "软技能",
                "英语表达拖累技术表达的结构化改进", "Can you walk me through a complex technical decision?",
                4, ["结论先行,再 First/Second/Third", "每点后跟 trade-off", "禁用 I forget / I'm confused,改 Let me think for a second", "术语发音单独练(see-sharp / thread / algorithm)"])
        };

        foreach (var f in fromInterviews)
        {
            var title = f.Title;
            if (await db.Items.AnyAsync(x => x.Title == title)) continue;

            var item = new KnowledgeItem(title, f.Topic, f.Question,
                KnowledgeSource.FromInterview, difficulty: f.Diff, importance: 5);

            item.UpdateDetails(title, f.Topic, null, f.Question, f.Diff, 5,
                JsonSerializer.Serialize(new[] { f.Topic, f.Company }),
                null, null, null,
                JsonSerializer.Serialize(f.KeyPoints), null, null);

            item.LinkToInterview(Guid.Empty, f.Company, f.Date);
            // 这些都是"答不好"的题 → 掌握度从 New 起步,交给 SM-2 排复习
            item.PromoteMastery(MasteryLevel.New);

            db.Items.Add(item);
        }

        logger.LogInformation("Knowledge 实战来源知识点写入完成");

        await db.SaveChangesAsync();

        var total = await db.Items.CountAsync();
        logger.LogInformation("Knowledge 演示数据已写入:{Total} 个知识点", total);
    }
}
