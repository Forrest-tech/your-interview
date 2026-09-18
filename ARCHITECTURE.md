# Your Interview — 代码框架总览

个人求职与面试全流程平台。四大业务类目 + 管理后台:

1. Tracker 投递跟踪
2. 实战机经(导入面试录音/转写 → 自动分析出短板)
3. 技术栈(机经里答不好的问题 + 自供问题,九块拆解 + SM-2 复习)
4. AI 实战模拟(AI 提问、我回答、六维评分)
5. 管理后台(用户/角色/权限/审计)

规模:后端 91 个 C# 文件约 16,500 行;前端 24 个 TS 约 4,800 行、
HTML 2,500 行、SCSS 3,500 行。

---

## 一、整体架构

    浏览器 (Angular 4200)
        │  /api/*  (proxy.conf.json)
        ▼
    网关 YARP (5200)  ← 唯一对外入口
        │  按路径前缀分发
        ├──► Identity (5262)     认证 / 用户 / 角色 / 权限 / 审计
        ├──► Jobs (5263)         投递跟踪
        ├──► Interviews (5264)   实战机经
        ├──► Knowledge (5265)    技术栈
        ├──► Assessment (5266)   AI 模拟
        └──► Analytics (5267)    读模型 / 统计
                 ▲
                 │ RabbitMQ 集成事件(异步、最终一致)
                 │
            Analysis.Worker  AI 分析管线(转写 → 六维诊断 → 回写)

    PostgreSQL (5433) —— 单库 yourinterview,每服务独立 schema

设计取向:Clean Architecture + DDD + CQRS/MediatR + 事件驱动。
后端严格照 eShopOnContainers 的范式组织,便于日后维护与面试讲解。

---

## 二、解决方案分层

    src/
      BuildingBlocks/       跨服务共享的技术底座(无业务)
      SharedContracts/      跨服务共享的契约(集成事件、权限常量)
      Services.Identity/    ┐
      Services.Jobs/        │ 6 个业务微服务
      Services.Interviews/  │ 每个都是
      Services.Knowledge/   │ Api / Application / Domain / Infrastructure
      Services.Assessment/  │ 四层结构
      Services.Analytics/   ┘
      Services.Gateway/     YARP 反向代理(只有 Program.cs + 中间件)
      Analysis.Worker/      后台消费者 + AI 分析管线(无 HTTP 端点)
      YourInterview.sln
      Directory.Build.props     统一 TargetFramework / LangVersion / 编译规范
      Directory.Packages.props  集中管理 NuGet 版本(不写在各 csproj 里)

---

## 三、BuildingBlocks —— 技术底座

不含业务,所有服务共用:

    Domain/
      Entity.cs              实体基类(Id + 相等性)
      AggregateRoot.cs       聚合根基类(领域事件收集、并发令牌)
      ValueObject.cs         值对象基类(结构相等)
      IDomainEvent.cs        领域事件标记
      DomainEventBase.cs     领域事件基类
    Behaviors/
      PipelineBehaviors.cs   MediatR 管道:日志 / 事务 / 性能
      ValidationBehavior.cs  FluentValidation 自动校验
      (注:校验器要反射注册 IValidator<T>,否则静默失效)
    Persistence/
      DomainEventDispatchInterceptor.cs  SaveChanges 时派发领域事件
    Results/
      Result.cs              结果对象(替代异常做业务失败)
    Security/
      CurrentUser.cs         当前用户抽象
      PermissionPolicy.cs    细粒度权限策略
    Web/
      ResultExtensions.cs    Result → HTTP 响应映射
    Hosting/
      ServiceDefaults.cs     统一服务基座(日志/可观测/MediatR/CORS/健康检查/Swagger)

关键点:每个服务的 Program.cs 都只写 `builder.AddServiceDefaults(...)`,
日志、OpenTelemetry、异常处理、CORS、健康检查、Swagger 全在基座里配一次。

---

## 四、SharedContracts —— 跨服务契约

    Events/IntegrationEvents.cs     集成事件定义(跨服务异步通信)
    IIntegrationEvent.cs            集成事件接口
    Analysis/InterviewAnalysisReport.cs  AI 分析报告契约(Worker → Interviews)
    Messaging/MessageTopology.cs    交换机/队列拓扑约定
    Messaging/MessagingExtensions.cs MassTransit 配置扩展
    Security/Permissions.cs         权限常量(前后端共用同一份语义)

---

## 五、单个服务的四层结构

以 Interviews 为例:

    Services.Interviews/
      Api/
        InterviewsController.cs      HTTP 端点,薄;只做请求 → Command/Query → 响应
      Application/
        InterviewHandlers.cs         MediatR Handler:用例编排(Command 写 / Query 读)
      Domain/
        InterviewAggregates.cs       聚合根 + 实体 + 值对象 + 领域事件
      Infrastructure/
        Persistence/
          InterviewsDbContext.cs     EF Core 上下文 + 实体映射 + Schema
          InterviewsDbSeeder.cs      启动时 MigrateAsync + 播种
          Migrations/                EF 迁移(含 ModelSnapshot)
        Services/
          InterviewsIntegrationEventPublisher.cs  领域事件 → 集成事件发布
      Program.cs                    组合根:DI 装配
      appsettings.json              连接串 / JWT / RabbitMQ / 播种开关

分层依赖方向:Api → Application → Domain ← Infrastructure。
Domain 不依赖任何外层,也不依赖 EF。

### 各服务的领域模型

    Identity     AppUser、AppRole(+ 权限、刷新令牌、审计)
    Jobs         Company、JobApplication(投递状态机)
    Interviews   InterviewEntry(一场面试)+ 问题、短板、分析报告、资产
    Knowledge    KnowledgeItem(九块结构 + SM-2 复习排期)
    Assessment   MockSession + Question + 六维评分
    Analytics    只读读模型(消费事件维护,不承载业务写入)

---

## 六、网关路由(YARP)

    路径前缀                      → 服务
    /api/auth/**                  → Identity
    /api/admin/**                 → Identity
    /api/{**catch-all}            → Identity(兜底)
    /api/jobs/**                  → Jobs
    /api/interviews/**            → Interviews
    /api/knowledge/**             → Knowledge
    /api/assessment/**            → Assessment
    /api/analytics/**             → Analytics

网关附加能力:GUID 关联 ID 注入、按用户分区限流、CORS、下游健康探测。
容器里下游地址通过环境变量覆盖成服务名(见 docker-compose.yml)。

---

## 七、事件驱动

服务之间不直接调用,通过 RabbitMQ 发集成事件,Analytics 消费后维护读模型:

    Jobs          投递状态变更         → Analytics
    Interviews    面试分析完成         → Analytics
    Knowledge     掌握度等级变化       → Analytics
    Assessment    模拟答题提交         → Analytics
    Identity      用户注册 / 审计事件  → Analytics

对应的消费者(Analytics 服务内):
JobApplicationStatusChangedConsumer、InterviewAnalysisCompletedConsumer、
KnowledgeLevelChangedConsumer、MockAnswerSubmittedConsumer、
AuditEventRecordedConsumer(均继承 AnalyticsConsumerBase,统一下消费基座)。

一致性策略:Outbox(事务内落库、后台投递)+ 幂等消费。
Analysis.Worker 订阅分析请求 → 调 Azure Speech 转写 → 六维诊断 → 回写 Interviews。

---

## 八、前端结构(Angular 17 standalone)

    web/src/app/
      core/
        api/api-client.ts          统一 HTTP 客户端(泛型封装)
        auth/auth.service.ts       JWT 存取、登录状态、refresh
        auth/auth.guard.ts         路由守卫(authGuard / permissionGuard)
        interceptors/auth.interceptor.ts  自动注入 Bearer、401 自动刷新
        models/api.models.ts       后端 DTO 的类型化映射
      layout/
        shell.component.*          侧边栏 + 顶栏 + 内容区
      features/
        auth/login               登录页
        dashboard                总览(漏斗 + 能力雷达)
        tracker                  投递跟踪(看板 + 新建/编辑)
        playbook(+detail)        实战机经列表与详情
        techstack(+dialog)       技术栈 + 复习 + 新增条目弹窗
        mock(+session)           AI 模拟列表与会话页
        analytics                六维雷达 / 漏斗趋势
        admin                    用户 / 角色 / 权限 / 审计
        profile                  个人资料与权限清单
        forbidden                无权限页
      app.routes.ts                路由表(懒加载 + 守卫)
      app.config.ts                应用级 Provider

路由设计:布局父路由只初始化一次(切模块不重建侧边栏),子模块全部懒加载。
权限控制双保险:路由守卫(前端不给进)+ 后端策略(真拦住)。

---

## 九、开发与运行

    tools/
      dev.sh               容器内开发栈管理(Linux 容器版)
      dev.docker.sh        全容器版管理(Mac 用这个)
      contract-check.sh    逐端点契约联调(5xx/404 判失败,4xx 判通过)
      e2e.sh               端到端自测
      ef.sh                EF Core 迁移封装
      db/                  embedded PostgreSQL 管理(容器内用)
      rabbit/              RabbitMQ 运行器(容器内用)

    Dockerfile.service     8 个可执行项目共用的多阶段构建
    docker-compose.yml     9 容器编排(pg + rabbit + 7 服务 + 网关)

Mac 上跑:`docker compose up -d --build`,前端 `cd web && npm start`。

---

## 十、数据库

单库 yourinterview,每服务独立 schema(不用跨库):

    schema: identity / jobs / interviews / knowledge / assessment / analytics

每个 schema 有自己的 `__ef_migrations_history`,迁移互不干扰。
服务启动时自动跑 EF 迁移建表并播种,不需要手工初始化。
连接串用 `Search Path=<schema>` 指定默认 schema。
