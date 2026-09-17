-- ============================================================================
--  import-interviews.sql
--  把 5 场真实面试(结构化数据)导入 interviews schema。
--
--  来源:原 InterviewsDbSeeder.cs 里的硬编码种子(已从代码移除)。原文归档在
--        workspace/memory/归档-种子数据原文-2026-09-17/InterviewsDbSeeder.cs
--
--  为什么用 SQL 而不是种子:Forrest 要求"重要业务数据只能来自数据库"。
--  这 5 场是真实发生过的面试(含面试官姓名/评分/复盘),属个人资产,应落在
--  库里、可在界面编辑,而不是固化在 C# 源码中随代码发布。
--
--  用法(Mac):
--    "/Library/PostgreSQL/13/bin/psql" -U postgres -d yourinterview \
--      -v ON_ERROR_STOP=1 -f tools/import-interviews.sql
--
--  幂等:固定 GUID + ON CONFLICT DO NOTHING,可重复执行。
--  ⚠️ 运行前:确认库里没有同公司的重复条目;若要重来,先删本脚本插入的 5 个 GUID。
-- ============================================================================

BEGIN;
SET client_encoding = 'UTF8';

-- ============================================================================
-- 1. Gateway Services  2026-08-19  现场技术面(Lori / David / Vinitha)
-- ============================================================================
INSERT INTO interviews.entries (
    "Id","CompanyId","CompanyName","JobApplicationId","Role","CompanyProfile","JdText","JdSummary",
    "RoundNo","InterviewDate","InterviewFormat","Interviewers","Result","Location","Notes","Status",
    "CreatedAt","IsDeleted")
VALUES (
    '11111111-1111-1111-1111-000000000001','a1111111-0000-0000-0000-000000000001',
    'Gateway Services',NULL,'Software Engineer',
    '北美宠物殡葬服务商。技术团队在 Guelph(230 Hanlon Creek Blvd),岗位 3 天现场 + 2 天远程。面试官:Lori Lalonde(Director of Engineering,新上任 2 周,30 年从业,最终决策者)、David Castelino(Sr. Software Engineer,技术把关人)、Vinitha Kotha(Lead Software Engineer,远程接入)。',
    NULL,
    '现场技术面:Lori + David 到场,Vinitha 远程接入。考纲由 David 邮件预告:1 道 mini coding + 1 道 system design + C#/Angular/critical thinking。',
    1,'2026-08-19','Onsite','Lori Lalonde, David Castelino, Vinitha Kotha','待定',
    'Guelph 230 Hanlon Creek Blvd(现场)',
    '面试后 8-20 发感谢信。David 承诺 end of this week(8-21)或 early next week(8-24~25)出结果。',
    'Draft','2026-08-19T12:00:00+00:00',false)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO interviews.weaknesses
    ("Id","InterviewEntryId","Category","Title","Detail","Evidence","Severity","Suggestion","OccurrenceCount","SourceType","CreatedAt")
VALUES
 ('b1000001-0000-0000-0000-000000000001','11111111-1111-1111-1111-000000000001','Logic',
  'system design 题被听成"讲项目经历"',
  'David 问"如果要拆微服务你会怎么做",这是那道 system design 题,但被当成项目回忆来答,用项目管理视角(派人学 / PoC / demo)回应技术设计追问。面试官连问 4-5 遍后放弃。',
  'David 邮件已预告考纲含 system design;现场连问 4-5 遍',5,
  '听到 how would you design / what steps / if you were to 立即切换技术方案模式:结论先行 + First/Second/Third + trade-off,绝不讲成项目回忆。',1,'Manual','2026-08-23T00:00:00+00:00'),
 ('b1000002-0000-0000-0000-000000000002','11111111-1111-1111-1111-000000000001','TechnicalDepth',
  '单体拆微服务方法论空白',
  '找不出 bounded context、说不出 strangler-fig 渐进迁移、说不出数据解耦路径(shared db → per-service db / outbox / saga)。',
  'David 连续追问 4-5 遍放弃',5,
  '必背四步:map monolith → DDD bounded context → strangler-fig + API gateway → 数据解耦(shared db→per-service db,outbox/saga)→ 权衡(不是所有都拆,网络延迟/分布式事务/运维成本都是代价)。',1,'Manual','2026-08-23T00:00:00+00:00'),
 ('b1000003-0000-0000-0000-000000000003','11111111-1111-1111-1111-000000000001','TechnicalDepth',
  '数据库大数据集优化讲不出细节',
  '被追问大数据集优化时给不出具体手段(分区 / 覆盖索引 / 读写分离 / keyset 分页 / 物化视图 / IAsyncEnumerable),与简历声称的优化幅度落差大,伤可信度。',
  '两段追问:①大数据优化 ②微服务内部结构+业务逻辑放哪+薄 Controller',4,
  '按"先量再改"讲:看执行计划与逻辑读 → 覆盖索引消除 Key Lookup → keyset 分页 → 读写分离/分区/物化视图 → 应用层流式处理。',1,'Manual','2026-08-23T00:00:00+00:00'),
 ('b1000004-0000-0000-0000-000000000004','11111111-1111-1111-1111-000000000001','Expression',
  '口头禅 "so easy" 说了十几次',
  '说简单却答不出细节,反差极大。同时当面说 "coding is so easy by AI / one week work done by AI",David 当场回 "I''m getting in depth"。',
  '转写逐字可查',5,
  '全部删除 so easy;改用具体收益表述。面试绝不吹 AI 写代码,哪怕被当面问也只讲"我审它的产出"。',1,'Manual','2026-08-23T00:00:00+00:00'),
 ('b1000005-0000-0000-0000-000000000005','11111111-1111-1111-1111-000000000001','TechnicalDepth',
  'RxJS / Angular 现代深度薄弱',
  'switchMap/mergeMap/concatMap/exhaustMap 区别答不出;pipe 概念混淆;内存泄漏防治(takeUntil/takeUntilDestroyed/async pipe)没答到。',
  'Rentsync 与 CIBC 均再考此题,连续踩坑',5,
  '四个高阶映射操作符的区别只在"上游来新值时已在飞的请求怎么办"。',1,'Manual','2026-08-23T00:00:00+00:00'),
 ('b1000006-0000-0000-0000-000000000006','11111111-1111-1111-1111-000000000001','TechnicalDepth',
  'coding 手感不足',
  '回文题双指针方向对但磕绊,off-by-one(S.length 忘减 1),return true 的位置想不清。',
  '现场 mini coding',4,
  '刷 LeetCode 双指针 / 字符串 / 哈希 30-50 题找手感。',1,'Manual','2026-08-23T00:00:00+00:00'),
 ('b1000007-0000-0000-0000-000000000007','11111111-1111-1111-1111-000000000001','Expression',
  '术语发音错误',
  'palindrome 完全没听懂;RxJS 说成 RSSS/art。',
  '现场',4,'建术语发音表专练。',1,'Manual','2026-08-23T00:00:00+00:00'),
 ('b1000008-0000-0000-0000-000000000008','11111111-1111-1111-1111-000000000001','Logic',
  '每题前垫 "this is a good/challenging question" 拖延',
  '回答无结构骨架,不上 First/Second/Third。',
  '转写',3,
  '改直接说 "Let me think for a second",然后结论先行 + 分点 + trade-off。',1,'Manual','2026-08-23T00:00:00+00:00')
ON CONFLICT ("Id") DO NOTHING;

-- ============================================================================
-- 2. CIBC(Kumaran 外包) 2026-09-01  电话技术面 —— 未通过
-- ============================================================================
INSERT INTO interviews.entries (
    "Id","CompanyId","CompanyName","JobApplicationId","Role","CompanyProfile","JdText","JdSummary",
    "RoundNo","InterviewDate","InterviewFormat","Interviewers","Result","Location","Notes","Status",
    "CreatedAt","IsDeleted")
VALUES (
    '11111111-1111-1111-1111-000000000002','a1111111-0000-0000-0000-000000000002',
    'CIBC',NULL,'Senior Developer(外包承接)',
    'CIBC 项目通过 Kumaran 的招聘方承接。验收标准是纯 C# + SQL + Web API + Angular + 单元测试 + API 安全 + CI/CD。',
    NULL,
    '面试官当场划考纲七件套:C#、MS SQL、Web API、Angular、unit testing、API 安全/APIM、CI/CD。JD 上吹的 Graph API / Teams / LogicApps 本轮根本没问。',
    1,'2026-09-01','Phone','Kumaran(招聘经理)','未通过',NULL,
    '同类外包/银行岗的验收标准就是那七件套。',
    'Draft','2026-09-01T12:00:00+00:00',false)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO interviews.weaknesses
    ("Id","InterviewEntryId","Category","Title","Detail","Evidence","Severity","Suggestion","OccurrenceCount","SourceType","CreatedAt")
VALUES
 ('b2000001-0000-0000-0000-000000000001','11111111-1111-1111-1111-000000000002','Logic',
  'C# 年限答成 10/3/20 混乱',
  '被问 C# 用多久,绕来绕去给出互相矛盾的数字,直接动摇可信度。',
  '逐字转写',4,
  '固定口径:C# / .NET 累计 X 年(从 ASM 时期算起),其中生产级服务端 X 年。数字提前定死,不再临场算。',1,'Manual','2026-09-01T18:00:00+00:00'),
 ('b2000002-0000-0000-0000-000000000002','11111111-1111-1111-1111-000000000002','TechnicalDepth',
  '被问 MS SQL 时亲手否认自己最强项',
  '答"只用过 postgres / I haven''t used SQL Server",当场否掉简历里最硬的项。',
  '逐字转写',5,
  '铁律:主力技术永不承认不会。改成确认 + 立刻给例子:"Yes, SQL Server 是我主力之一。" → 举例。',1,'Manual','2026-09-01T18:00:00+00:00'),
 ('b2000003-0000-0000-0000-000000000003','11111111-1111-1111-1111-000000000002','TechnicalDepth',
  'Angular 内存泄漏卡在函数名',
  'takeUntil / async pipe 说对了方向,但卡在 "I forget the function name" —— 该说 ngOnDestroy / takeUntilDestroyed。',
  '逐字转写',4,
  '专有名词贴桌边速记卡,面试前 2 天做突发提问模拟。',1,'Manual','2026-09-01T18:00:00+00:00'),
 ('b2000004-0000-0000-0000-000000000004','11111111-1111-1111-1111-000000000002','TechnicalDepth',
  'RxJS 四兄弟遇 mergeMap 自我投降',
  'switchMap 说对了,遇 mergeMap 直接说 "I''m confused"。',
  '逐字转写',5,
  '四兄弟一次成组背:取消/并发/排队/忽略 —— switchMap/mergeMap/concatMap/exhaustMap。',1,'Manual','2026-09-01T18:00:00+00:00'),
 ('b2000005-0000-0000-0000-000000000005','11111111-1111-1111-1111-000000000002','TechnicalDepth',
  '多线程答成消息队列',
  '被问并发/多线程,答的是消息队列 / Redis,而不是 lock / SemaphoreSlim / 并发集合。',
  '逐字转写',4,
  '先分清:多线程是进程内并发(Thread/Task/lock/SemaphoreSlim/并发集合),消息队列是跨进程解耦。两者解决的不是同一个问题。',1,'Manual','2026-09-01T18:00:00+00:00'),
 ('b2000006-0000-0000-0000-000000000006','11111111-1111-1111-1111-000000000002','Expression',
  '禁用词系统性出现',
  '全程 I forget / I''m confused / I haven''t used 三种投降式表达反复出现。',
  '逐字转写',4,
  '三个词全部禁用,改 "Let me think for a second" + 复述问题。',1,'Manual','2026-09-01T18:00:00+00:00')
ON CONFLICT ("Id") DO NOTHING;

-- ============================================================================
-- 3. Rentsync  2026-09-02  intro call(Viktor)
-- ============================================================================
INSERT INTO interviews.entries (
    "Id","CompanyId","CompanyName","JobApplicationId","Role","CompanyProfile","JdText","JdSummary",
    "RoundNo","InterviewDate","InterviewFormat","Interviewers","Result","Location","Notes","Status",
    "CreatedAt","IsDeleted")
VALUES (
    '11111111-1111-1111-1111-000000000003','a1111111-0000-0000-0000-000000000003',
    'Rentsync',NULL,'Senior Software Engineer',
    '加拿大租赁营销 SaaS。本轮为 intro call(面试官 Viktor)。',
    NULL,'intro call,主要确认背景与匹配度,尚未进入技术深挖。',
    1,'2026-09-02','Video','Viktor','待定',NULL,
    'intro 表现尚可;技术轮候考。此轮暴露出术语发音问题(switch up / surgery / dual tracks)。',
    'Draft','2026-09-02T12:00:00+00:00',false)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO interviews.weaknesses
    ("Id","InterviewEntryId","Category","Title","Detail","Evidence","Severity","Suggestion","OccurrenceCount","SourceType","CreatedAt")
VALUES
 ('b3000001-0000-0000-0000-000000000001','11111111-1111-1111-1111-000000000003','Expression',
  '术语发音系统性错误',
  'C# 说成 C-shop / switch up、thread 说成 surgery/surrender/reader、DealTrax 说成 dual tracks、vision inspection 说成 wishing、.NET full-stack 说成 Donet for steak、Pack-Smart 说成 passport/picky。',
  '2026-09-03 PackSmart 与 Rentsync intro 均记录到同一现象',5,
  '术语必须照正确发音:C#=see-sharp、WPF=W-P-F、MVVM、thread、.NET Core、vision、algorithm、library、OpenCV、multithreading。技术轮前必背。',1,'Manual','2026-09-02T18:00:00+00:00')
ON CONFLICT ("Id") DO NOTHING;

-- ============================================================================
-- 4. Pack-Smart (Vision Systems)  2026-09-03  intro 电话(Michael Ly)
-- ============================================================================
INSERT INTO interviews.entries (
    "Id","CompanyId","CompanyName","JobApplicationId","Role","CompanyProfile","JdText","JdSummary",
    "RoundNo","InterviewDate","InterviewFormat","Interviewers","Result","Location","Notes","Status",
    "CreatedAt","IsDeleted")
VALUES (
    '11111111-1111-1111-1111-000000000004','a1111111-0000-0000-0000-000000000004',
    'Pack-Smart (Vision Systems)',NULL,'C# / .NET Developer',
    '包装自动化视觉检测设备商。Vision 团队纯 C#,GUI + backend;库已建好,部分项目中途,主要工作其实是 optimization;同时在把 C++ 桌面 tracking 软件 web 化迁移到 .NET Core(conveyor 400ft/min 跟踪 → 客户用 Chrome/Edge 登录)。Michael 明确:"要 C# developer 不是 vision designer"。vision fully in-house + OpenCV,后端 C#,硬件可外采。',
    NULL,
    'intro 电话(TA Michael Ly)。流程:视频轮考定义/概念类;现场 30 分钟 C# assignment —— 用他们的笔记本、断网、Visual Studio 手写一个 algorithm,然后 meet the team。',
    1,'2026-09-03','Phone','Michael Ly (TA)','已推进(约 70%),技术轮候考',NULL,
    '强匹配:C#/.NET 主力 + C++→.NET 迁移 + web 化,不是要写 OpenCV 算法。',
    'Draft','2026-09-03T12:00:00+00:00',false)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO interviews.weaknesses
    ("Id","InterviewEntryId","Category","Title","Detail","Evidence","Severity","Suggestion","OccurrenceCount","SourceType","CreatedAt")
VALUES
 ('b4000001-0000-0000-0000-000000000001','11111111-1111-1111-1111-000000000004','Mindset',
  '薪资自曝低于预算下限',
  'TA 问期望时答 "90,000 fine for me now",低于 JD 预算(100-130)下限,也和速记卡"锚 120-130"自相矛盾。',
  '逐字转写',5,
  'TA / 猎头轮报价一律锚 JD 区间中段或"based on 100-130, expect 115-130";绝不主动低于下限。vacation 说 no problem OK,base 别自压。',1,'Manual','2026-09-03T18:00:00+00:00'),
 ('b4000002-0000-0000-0000-000000000002','11111111-1111-1111-1111-000000000004','TechnicalDepth',
  '断网手写 C# 算法是既有硬伤',
  '现场 30 分钟、断网、无提示、用他们的机器写 algorithm —— 与 Gateway/CIBC 的 coding 手感问题同源。',
  '面试流程已确认',5,
  '技术轮前专项练:不看提示手写双指针/字符串/哈希/排序,并用 Visual Studio 熟悉快捷键。',1,'Manual','2026-09-03T18:00:00+00:00')
ON CONFLICT ("Id") DO NOTHING;

-- ============================================================================
-- 5. Geotab  2026-09-11  第一轮(Lena Kang)· 唯一有真实六维评分的场次
-- ============================================================================
INSERT INTO interviews.entries (
    "Id","CompanyId","CompanyName","JobApplicationId","Role","CompanyProfile","JdText","JdSummary",
    "RoundNo","InterviewDate","InterviewFormat","Interviewers","Result","Location","Notes","Status",
    "OverallScore","PronunciationScore","FluencyScore","StructureScore","TechnicalDepthScore","RelevanceScore",
    "AnalysisSummary","TranscribedAt","AnalyzedAt","CreatedAt","IsDeleted")
VALUES (
    '11111111-1111-1111-1111-000000000005','a1111111-0000-0000-0000-000000000005',
    'Geotab',NULL,'Senior Software Developer',
    '车队远程信息处理(telematics)平台,加拿大大型技术公司(多伦多/橡树维尔)。',
    NULL,
    '第一轮,面试官 Lena Kang。已录制完整音频并跑通 Azure Speech 评估管线。',
    1,'2026-09-11','Video','Lena Kang','待定',NULL,
    '已产出《Geotab 第一轮面试录音评估报告》(原文 + PDF),含逐字转写、说话人分离、发音评分、语速/填充词/自我重复等自研指标与四周提升方案。',
    'Analyzed',
    62,93,55,45,60,70,
    '发音不是主要问题(92.8/100 接近母语者水平);瓶颈在表达层:① 句子破碎(平均句长 7.5 词)② 结构骨架缺失 ③ 专业术语系统性读错。最大增量 = 结构骨架,而非语音能力。',
    '2026-09-13T12:00:00+00:00','2026-09-13T12:00:00+00:00','2026-09-11T12:00:00+00:00',false)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO interviews.weaknesses
    ("Id","InterviewEntryId","Category","Title","Detail","Evidence","Severity","Suggestion","OccurrenceCount","SourceType","CreatedAt")
VALUES
 ('b5000001-0000-0000-0000-000000000001','11111111-1111-1111-1111-000000000005','Expression',
  '句子破碎(平均句长仅 7.5 词)',
  '语速 158.3 wpm 正常,但平均句长只有 7.5 词、过短句 75 条。表达被切割成碎片,信息密度低,听感上像在挤单词而不是在讲完整的技术判断。这是最大增量项。',
  'Azure Speech 转写 + 自研指标(2026-09-13 报告)',5,
  '练"结论句 + 两个支撑句"的三句话单元,把短句合并成带因果/让步关系的复合句。',1,'Manual','2026-09-13T12:00:00+00:00'),
 ('b5000002-0000-0000-0000-000000000002','11111111-1111-1111-1111-000000000005','Structure',
  '结构骨架缺失',
  '回答缺少 First / Second / Third 的显式骨架,听众抓不到层次。',
  '转写分析',5,
  '万能骨架:"The short answer is X. Three things I''d focus on. First... Second... Third... The main trade-off is..."',1,'Manual','2026-09-13T12:00:00+00:00'),
 ('b5000003-0000-0000-0000-000000000003','11111111-1111-1111-1111-000000000005','Pronunciation',
  '术语发音错误(第 N 次重演)',
  '.NET stack 读成 "the stick"、monolithic 读成 "Mandalay''s"、on-call 读成 "uncle"、DealTrax 读成 "dual tracks"。',
  'Azure Pronunciation Assessment:均分 92.8,但专业术语是主要扣分点;低分词(<60)占 4.4%',5,
  '发音本身接近母语者水平(92.8/100),说明不是语音能力问题,而是专业词汇没练过。术语表专项。',1,'Manual','2026-09-13T12:00:00+00:00'),
 ('b5000004-0000-0000-0000-000000000004','11111111-1111-1111-1111-000000000005','Expression',
  '填充词与自我重复',
  '填充词 20 次、自我重复 5 次,在压力下用重复换思考时间。',
  '自研指标',3,
  '用"Let me think for a second"静默两秒替代填充词;宁可停顿不要 um/uh。',1,'Manual','2026-09-13T12:00:00+00:00'),
 ('b5000005-0000-0000-0000-000000000005','11111111-1111-1111-1111-000000000005','TechnicalDepth',
  '技术缺口:Go / Python 与 Web 架构',
  '面试中暴露出 Go/Python 与 Web 架构方面的知识缺口。',
  '报告结论',3,
  '按岗位需要补:Go/Python 基础语法 + Web 架构模式(SSR/CSR/BFF/边缘)。',1,'Manual','2026-09-13T12:00:00+00:00')
ON CONFLICT ("Id") DO NOTHING;

-- Geotab 的那道原始问题(唯一有逐题记录的场次)
INSERT INTO interviews.questions
    ("Id","InterviewEntryId","Sequence","QuestionText","MyAnswerText","Category","Difficulty",
     "Assessment","GotStuck","StuckReason","RecommendedAnswer","AskedAtSeconds",
     "WeaknessTagsJson","FollowUpQuestionsJson","MissedPointsJson")
VALUES (
    'c5000001-0000-0000-0000-000000000001','11111111-1111-1111-1111-000000000005',1,
    'Can you walk us through the architecture of the system you currently work on?',
    NULL,'Technical',4,NULL,false,NULL,NULL,NULL,NULL,NULL,NULL)
ON CONFLICT ("Id") DO NOTHING;

-- Geotab 的录音材料(分析已完成的证据)
INSERT INTO interviews.assets
    ("Id","InterviewEntryId","Kind","FileName","ContentType","SizeBytes","StoragePath","BlobUrl",
     "DurationSeconds","SourceLanguage","UploadedAt","TranscriptText","TranscriptSegmentsJson")
VALUES (
    'd5000001-0000-0000-0000-000000000001','11111111-1111-1111-1111-000000000005',
    'Audio','Geotab-Round1-LenaKang.m4a','audio/mp4',0,
    '面试录音评估/归档/Geotab-第一轮-LenaKang.m4a',NULL,1475,'en-US',
    '2026-09-11T12:00:00+00:00',
    '[转写全文见 面试录音评估/归档/ 下的 Geotab 转写数据 —— 此处留空以免塞入整篇对话]',
    NULL)
ON CONFLICT ("Id") DO NOTHING;

COMMIT;

-- ============================================================================
-- 验证:应有 5 条 entries、15 条 weaknesses、1 条 questions、1 条 assets
-- ============================================================================
-- SELECT (SELECT count(*) FROM interviews.entries)    AS entries,
--        (SELECT count(*) FROM interviews.weaknesses) AS weaknesses,
--        (SELECT count(*) FROM interviews.questions)  AS questions,
--        (SELECT count(*) FROM interviews.assets)     AS assets;
-- SELECT "CompanyName","Role","InterviewDate","Result","OverallScore","Status"
--   FROM interviews.entries ORDER BY "InterviewDate";
