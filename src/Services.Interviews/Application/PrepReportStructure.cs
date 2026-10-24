using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YourInterview.Services.Interviews.Domain;

namespace YourInterview.Services.Interviews.Application;

// ============================ 票133:Prep 报告规范结构 ============================
// 规范结构常量 + 轮次分类器 + 问题分流 + 本地分节 builder + AI 提示词 + 结构归一化。
// 全部为公开静态 API,便于测试直接断言(Interviews 程序集无 InternalsVisibleTo)。
// 本文件是票133 规格(SPEC.md)的唯一实现落点;Handler 只做编排与落库。

/// <summary>轮次分类(规格 §4.2):只读 round.Stage 自由文本,五个输出互相独立。</summary>
public sealed record RoundClassification(
    bool IsCoding, bool IsDesign, bool IsScreen, bool IsBehavioral, bool IsFinal);

/// <summary>轮次分类器(规格 §4.2,单点定义;前端 fw 骨架镜像同一张表)。</summary>
public static class PrepReportClassifier
{
    public static RoundClassification Classify(string? stage)
    {
        var s = stage ?? string.Empty;
        bool Match(string pattern) => Regex.IsMatch(s, pattern, RegexOptions.IgnoreCase);
        return new RoundClassification(
            Match(@"coding|live[ -]?coding|pair[ -]?programming|online assessment|\boa\b|hackerrank|codility"),
            Match(@"system[ -]?design|architect|design"),
            Match(@"screen|phone|recruit|\bhr\b"),
            Match(@"behav|culture|fit"),
            Match(@"final|hiring|manager|onsite|panel"));
    }
}

/// <summary>分流输入:原题(领域 InterviewQuestion 的分流所需投影)。</summary>
public sealed record PrepQuestionInput(
    string Text, QuestionCategory Category, bool GotStuck,
    string? StuckReason, string? RecommendedAnswer, int Sequence);

/// <summary>分流输入:弱点(领域 InterviewWeakness 的分流所需投影)。</summary>
public sealed record PrepWeaknessInput(
    WeaknessCategory Category, string Title, string? Detail, string? Suggestion, int Severity);

/// <summary>问题分流(规格 §6):先 GotStuck、后 Category,逐题唯一落点。</summary>
public static class PrepReportRouting
{
    /// <summary>规范落点节 id;SmallTalk 返回 null(不进报告)。</summary>
    public static string? CanonicalSection(PrepQuestionInput q, RoundClassification cls)
    {
        if (q.GotStuck) return "weakness-plan";
        return q.Category switch
        {
            QuestionCategory.Behavioral => "behavioral",
            QuestionCategory.Screening => "comp-process",
            QuestionCategory.SystemDesign => cls.IsDesign ? "design-round" : "tech-qa",
            QuestionCategory.Technical or QuestionCategory.HonestyBoundary => "tech-qa",
            QuestionCategory.SmallTalk => null,
            _ => "tech-qa"
        };
    }

    /// <summary>题面归一化(规格 §6.5 匹配键):Trim + 折叠连续空白,比较时不区分大小写。</summary>
    public static string NormalizeText(string text) =>
        Regex.Replace(text.Trim(), @"\s+", " ");

    public static bool SameText(string a, string b) =>
        string.Equals(NormalizeText(a), NormalizeText(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>按题面匹配分流输入(归一化匹配键)。同一题面命中多条记录时确定性取舍:
    /// 卡题优先(§6.5 第 1 步卡题优先原则),其次 Sequence 最小 —— 规格 O-3 备注。</summary>
    public static PrepQuestionInput? MatchInput(string text, IReadOnlyList<PrepQuestionInput> inputs) =>
        inputs.Where(q => SameText(q.Text, text))
            .OrderByDescending(q => q.GotStuck)
            .ThenBy(q => q.Sequence)
            .FirstOrDefault();

    /// <summary>本地路由(规格 §9.1 取数口径):卡题 ≤10、非卡合计 ≤30(均 Sequence 升序),再按节上限截断。</summary>
    public static RoutedPrepQuestions RouteQuestions(
        IReadOnlyList<PrepQuestionInput> questions, RoundClassification cls)
    {
        var stuck = questions.Where(q => q.GotStuck).OrderBy(q => q.Sequence).Take(10).ToList();
        var rest = questions.Where(q => !q.GotStuck).OrderBy(q => q.Sequence).Take(30).ToList();
        var tech = new List<PrepQuestionInput>();
        var beh = new List<PrepQuestionInput>();
        var comp = new List<PrepQuestionInput>();
        var design = new List<PrepQuestionInput>();
        foreach (var q in rest)
        {
            switch (CanonicalSection(q, cls))
            {
                case "tech-qa": tech.Add(q); break;
                case "behavioral": beh.Add(q); break;
                case "comp-process": comp.Add(q); break;
                case "design-round": design.Add(q); break;
            }
        }
        return new RoutedPrepQuestions(
            tech.Take(20).ToList(), beh.Take(10).ToList(), comp.Take(5).ToList(),
            design.Take(10).ToList(), stuck);
    }
}

/// <summary>本地路由结果(按节分的原题;weakness-plan 的卡题见 Stuck)。</summary>
public sealed record RoutedPrepQuestions(
    IReadOnlyList<PrepQuestionInput> TechQa,
    IReadOnlyList<PrepQuestionInput> Behavioral,
    IReadOnlyList<PrepQuestionInput> CompProcess,
    IReadOnlyList<PrepQuestionInput> DesignRound,
    IReadOnlyList<PrepQuestionInput> Stuck);

/// <summary>规范结构(规格 §4.1/§4.3):13 节位顺序钉死,条件节按分类取舍。</summary>
public static class PrepReportSections
{
    public static readonly string[] CanonicalOrder =
    [
        "round-brief", "greeting", "intro-listen", "self-intro", "deep-dive", "tech-qa",
        "coding-round", "design-round", "behavioral", "weakness-plan", "ask-back",
        "comp-process", "closing"
    ];

    /// <summary>该分类下的期望节位序列(条件节在/不在、design 卡不占节位)。</summary>
    public static List<string> ExpectedSectionIds(RoundClassification cls)
    {
        var ids = new List<string>();
        foreach (var id in CanonicalOrder)
        {
            if (id == "coding-round" && !cls.IsCoding) continue;
            if (id == "design-round" && !cls.IsDesign) continue;
            ids.Add(id);
        }
        return ids;
    }

    public static string TitleFor(string id, PrepReportLocalTexts t) =>
        t.Titles[Array.IndexOf(CanonicalOrder, id)];

    // ---------------- JSON 小构件 ----------------

    public static JsonObject PointsBlock(string? title, IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var it in items.Where(i => !string.IsNullOrWhiteSpace(i))) arr.Add(it);
        var o = new JsonObject { ["type"] = "points", ["items"] = arr };
        if (title is not null) o["title"] = title;
        return o;
    }

    public static JsonObject ListBlock(string? title, IEnumerable<string> items)
    {
        var o = PointsBlock(title, items);
        o["type"] = "list";
        return o;
    }

    public static JsonObject ScriptBlock(string? title, string text)
    {
        var o = new JsonObject { ["type"] = "script", ["text"] = text };
        if (title is not null) o["title"] = title;
        return o;
    }

    public static JsonArray Tags(params string[] tags)
    {
        var arr = new JsonArray();
        foreach (var t in tags) arr.Add(t);
        return arr;
    }

    public static string? GetString(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static string SectionId(JsonObject s) => GetString(s, "id") ?? string.Empty;

    public static JsonArray EnsureBlocks(JsonObject section)
    {
        if (section["blocks"] is not JsonArray arr)
        {
            arr = new JsonArray();
            section["blocks"] = arr;
        }
        return arr;
    }

    public static JsonArray EnsureQuestions(JsonObject section)
    {
        if (section["questions"] is not JsonArray arr)
        {
            arr = new JsonArray();
            section["questions"] = arr;
        }
        return arr;
    }

    public static List<JsonNode?> BlocksOf(JsonObject section) =>
        section["blocks"] is JsonArray arr ? arr.ToList() : [];

    public static List<JsonNode?> QuestionsOf(JsonObject section) =>
        section["questions"] is JsonArray arr ? arr.ToList() : [];

    public static string? QuestionText(JsonNode node) =>
        node is JsonObject o ? GetString(o, "question") : null;

    public static void RemoveQuestion(JsonObject section, JsonNode node)
    {
        if (section["questions"] is JsonArray arr) arr.Remove(node);
    }

    public static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];
}

// ============================ 本地文案(规格 §9.2,三语齐全) ============================

/// <summary>本地兜底文案:讲解随语言、话术行恒英文(规格 §7)。</summary>
public sealed record PrepReportLocalTexts(
    string[] Titles,
    string NoCompanyBackground, string NoJd, string NoFetched, string NoResume,
    string NoWeaknessOrStuck, string StoryBankEmpty, string StackGapNeedsAi,
    string EmptyAreaMarker,
    string[] RoundBriefPrejudge,
    string JdGuide, string CompanyExcerptTitle, string JdExcerptTitle,
    string FactCompany, string FactRole, string FactRound, string FactInterviewers,
    string FactFormat, string FactDate,
    string[] Greeting, string OnsiteLine,
    string[] IntroListen,
    string[] SelfIntroTitles, string[] SelfIntroScaffolds, string[] SelfIntroGuide,
    string[] DeepDiveGuide, string[] DeepDiveTree, string[] DeepDiveNumbers,
    string[] TechQaGuide,
    string DesignCardTitle, string[] DesignCard,
    string[] CodingGuide,
    string[] DesignGuide, string DesignOpening,
    string[] BehavioralGuide, string[] BehavioralCoverage,
    string ConceptAreaTitle, string StackGapTitle, string SelfStatementTitle, string OtherWeaknessTitle,
    string[] StackGapFramework, string[] SelfStatementGuide,
    string[] AskBackGuide, string[] AskBackLayerTitles, string[][] AskBackLayerQuestions,
    string[] CompGuideSalary, string[] CompGuideStatus, string CompGuideProcess, string CompGuideScreenNote,
    string[] Closing,
    string PastTag, string GeneralTag, string FetchedTitle,
    string[] DefaultQuestions, string[] DefaultPoints)
{
    // 自介槽位脚手架为话术行,三语共用同一英文正文;{company}/{role} 由 builder 以真实字段实填。
    private static readonly string[] Scaffolds =
    [
        "Hi, I'm [your name]. I'm currently [current role & team], where I focus on [core area]. Recently, I [headline result with a number]. I'm excited about {company} because [why this company — one concrete change].",
        "Hi, I'm [your name]. I'm currently [current role & team]. In my current role I [core project hook] — the hardest part was [challenge], and the result was [headline result with a number]. Before that, I [past experience in one line]. I'm looking at {role} at {company} because [why this company — one concrete change], and that's where I want to go next.",
        "Hi, I'm [your name]. I'm currently [current role & team], where I work on [core area]. Let me start with the present: right now I'm [current project in one sentence]. Looking back, my most relevant work is [core project hook]: the background was [context], the hardest part was [challenge], what I did was [two concrete actions], and the result was [headline result with a number]. Before that, I [past experience in two lines]. Looking forward, I'm interested in {role} at {company} because [why this company — one concrete change], and I want to bring [your strength] to [team/problem]."
    ];

    private static readonly string[][] AskBackQuestions =
    [
        [
            "What are the most important deliverables in the first 90 days?",
            "What is the team's biggest technical challenge right now?",
            "How is success measured for this role?"
        ],
        [
            "How are technical decisions made on this team?",
            "What is the most interesting technical problem the team worked on recently?"
        ],
        [
            "Where does this team sit in the company's 1–2 year plan?",
            "What would you like this role to have achieved a year from now?"
        ],
        [
            "What are the next steps and the timeline?",
            "What should I prepare for the next round?"
        ]
    ];

    private static readonly string[] DefaultQs =
    [
        "How does a hash map work internally, and how do you handle collisions?",
        "When would you choose SQL over NoSQL, and when the reverse?",
        "How would you investigate a slow endpoint in production?",
        "How does async/await actually work, and what can go wrong with it?",
        "How does a middleware pipeline work, and where would you put cross-cutting concerns?",
        "What is the difference between WHERE and HAVING, and when does it matter?"
    ];

    public static PrepReportLocalTexts For(string lang) => lang switch
    {
        "zh" => new PrepReportLocalTexts(
            ["本轮速览与公司/岗位调研", "开场寒暄", "听对方介绍", "自我介绍", "项目深挖", "技术问答", "编程轮打法", "系统设计轮打法", "行为题 · 故事银行", "弱点与历史题预案", "反问环节", "薪资与流程", "收尾与注意事项"],
            "本轮无公司背景输入", "本轮无 JD 输入", "面经未取到", "未提供简历，本节为通用框架",
            "暂无已识别弱点与卡壳原题", "故事银行需 AI 生成或按清单自行填写，系统不代编故事", "逐项比对需 AI 生成",
            "暂无内容",
            ["筛选轮侧重：先问清岗位职责、团队结构与流程时间线", "设计轮侧重：先澄清需求与规模估算，再画数据模型与 API", "编程轮侧重：先听全问题再答，边写边说、收尾自验", "行为轮侧重：STAR 结构，Result 带数字", "终面侧重：动机与契合——为什么是我们、为什么是现在", "按对方介绍确认本轮考察重点，再调整答题深度"],
            "逐词对照简历找证据，找不到的词会落到弱点预案", "公司背景", "JD 摘录",
            "公司：", "岗位：", "第{0}轮 · {1}", "面试官：", "形式：", "日期：",
            ["提前 5 分钟进入会议", "开场一句：Hi, thanks for taking the time to speak with me today.", "名字确认一句：Before we start — could you let me know how to pronounce your name correctly?", "没人进会时：等 15 分钟 → 给 recruiter 发邮件留时间戳 → 当天等改期。邮件开口：Hi — I joined the call at [time] and haven't seen anyone join yet. Could we reschedule?"],
            "线下：提前到场、带水与纸质简历，先到前台签到",
            ["记下团队规模、在招岗位的职责边界、当前项目关键词", "回应一句：That sounds great — the part about X is exactly what I've been doing.", "三栏笔记：团队现状 / 当前挑战 / 他们要这个人解决什么——回应与反问都从后两栏取词"],
            ["30 秒版", "60 秒版", "120 秒版"], Scaffolds,
            ["三档结构恒为 Present–Past–Future：Present 一句封顶、Past 落与 JD 对位的经历、Future 落目标公司具体变化", "钩子纪律：只埋两个钩子（主项目 + 与 JD 最对位的一项）", "按方括号槽位填真实经历，不要编造数字与公司名"],
            ["四段式：背景 → 难点 → 方案 → 结果，数字先行", "每项目先备齐规模、提速或降本、影响范围三个数字再进门，缺数字的项目不做主打", "每个技术选型准备一句取舍：为什么选它、放弃了什么"],
            ["架构层追问 → 第一句开口：The system is structured as …", "数据层追问 → 第一句开口：The data flows as …", "故障层追问 → 第一句开口：When X failed, we …", "取舍层追问 → 第一句开口：We chose X over Y because …"],
            ["规模：用户量 / 数据量 / QPS", "提速或降本：百分比与绝对值", "影响范围：团队、业务线、客户"],
            ["概念题四拍：定义一句 → 机制一句 → 取舍一句 → 项目一句", "不会时开口：先明说边界，再 bridge 到最近的可迁移经验。英文原句：I haven't worked with X directly, but the closest experience I have is …"],
            "设计讨论卡",
            ["① 题目 + 需求澄清：一句话一个概念，问题要能决定设计方向", "② Functional（只取 TOP 3）/ Non-functional（3–5 条、必须量化，数字没给就问）+ Core Entities + API", "③ High-level design 按每个 functional 逐个讲", "④ NFR 逐条落到图上组件", "⑤ Bonus deep dives：带数字、模式与 trade-off，画进终图", "轮内被问设计题时按此五步答"],
            ["① 先听全问题再答，不抢答", "② 一句话一个概念地复述确认 + 澄清。英文开口：Let me restate the problem to make sure I got it right …", "③ 先讲 approach 再动手", "④ 边写边说、边界（空输入 / 大小写 / 标点）先行说出口", "⑤ 收尾自验：小例子跑一遍 + 一句复杂度 + 输出格式逐字对题面", "纪律：输出格式逐字对题面，一个字符都不要自作主张"],
            ["① 题目 + 需求澄清：一句话一个概念，不给选择题，问题要能决定设计方向", "② Functional：\"Users should be able to…\"，只取 TOP 3；Non-functional：\"The system should be able to…\"，3–5 条，必须量化，数字没给就问、不许编；+ Core Entities + API", "③ High-level design 按每个 functional 逐个讲", "④ NFR 逐条落到图上组件", "⑤ Bonus deep dives：带数字、模式与 trade-off，画进终图", "话术纪律：边画边说，Narrative > diagram", "话术纪律：收尾不展开新话题"],
            "Before I start designing, let me clarify a few things that will decide the direction …",
            ["STAR+L：Situation 两句封顶、Action 占一半且为具体复数动作、Result 带数字、末句 Learnings", "全文目标 ≤2 分钟口述量（约 ≤300 英文词）", "只许用简历 / 面经中真实出现的经历，提取不到的考点明示暂无故事"],
            ["ownership（主导）", "conflict（冲突）", "failure（失败）", "influence（影响力）", "ambiguity（模糊）"],
            "概念弱点卡区", "栈差距区", "Weakness 自述题区", "其他已识别弱点区",
            ["明说边界 + bridge 到最近可迁移经验 + 补课状态", "英文开口框架：I haven't worked with X in production, but the closest experience I have is … and I'm currently …"],
            ["选真实、在改、有证据的点：点名问题 → 已采取的动作 → 可验证的改善", "禁用假答案句式：不说「我最大的弱点是太追求完美」这类套话"],
            ["每轮备 3 问：一问交付、一问挑战、一问流程", "至少一问回收对方介绍里出现的真实关键词", "按对象分层：Manager / Tech Lead / Director / Recruiter·HR"],
            ["给 Manager", "给 Tech Lead", "给 Director", "给 Recruiter·HR"], AskBackQuestions,
            ["先问区间再表态。英文原句：Could you share the range budgeted for this role?", "只报目标区间、一句封顶、不报历史低点、不用单个数字自我锚定。目标区间句架：My target is [range].", "JD 写有 band 时只引用 JD 原文 band，不许出现其他金额数字"],
            ["工作授权句架：My work authorization is [type], valid until [date].", "是否需要 sponsorship 句架：I [do / do not] require sponsorship.", "到岗时间句架：I can start within [timeframe]."],
            "每轮结束前确认 next steps 与时间线（与反问环节 recruiter 层互引，不重复备题）",
            "筛选轮提示：薪资探询最常发生在筛选轮，先问区间再表态",
            ["结束前重申兴趣：Based on what I've heard, this role is exactly what I'm looking for.", "当天：提前测试设备与网络，水杯与简历放手边", "临场异常：不会的题先请复述争取时间。英文原句：Could you rephrase that? I want to make sure I understand the question correctly. 然后明说边界 + bridge", "面后跟进（只给骨架、不代发）：当天 thank-you——短、相关、点名一个本轮具体话题、不堆履历；check-in 按对方给的时间窗 +2 天、一封为限"],
            "原题", "通用", "抓取到的面经来源",
            DefaultQs,
            ["桶 + 哈希函数 + 冲突链/开放寻址，扩容时 rehash", "结构化与事务强 → SQL；海量半结构化与横向扩展 → NoSQL", "先看指标与日志定位层，再复现、加观测、逐段收窄", "状态机 + 同步上下文；死锁与 ConfigureAwait 是常见坑", "洋葱模型：请求进出各经过一次，认证/日志/异常放外层", "WHERE 过滤行、HAVING 过滤分组，聚合后筛选只能用 HAVING"]),
        "fr" => new PrepReportLocalTexts(
            ["Synthèse du tour et recherche sur l'entreprise", "Accueil", "Écouter leur présentation", "Présentation", "Projets en détail", "Questions techniques", "Stratégie pour l'épreuve de code", "Stratégie pour la conception système", "Comportement · banque d'histoires", "Plan faiblesses et questions passées", "Vos questions", "Rémunération et processus", "Conclusion et rappels"],
            "Aucun contexte d'entreprise fourni pour ce tour", "Aucune offre fournie pour ce tour", "Aucun retour récupéré", "Aucun CV fourni — cette section reste un cadre générique",
            "Aucune faiblesse ni question bloquante identifiée pour l'instant", "La banque d'histoires doit être générée par l'IA ou complétée par vous depuis la liste — le système n'invente jamais d'histoires", "La comparaison détaillée nécessite la génération par l'IA",
            "rien pour l'instant",
            ["Accent présélection : clarifiez d'abord le périmètre, l'équipe, le processus et le calendrier", "Accent conception : clarifiez besoins et échelle, puis modèle de données et API", "Accent code : écoutez tout le problème, pensez à voix haute, vérifiez à la fin", "Accent comportement : STAR, avec des chiffres dans le Résultat", "Accent final : motivation et adéquation — pourquoi nous, pourquoi maintenant", "Confirmez l'accent de ce tour, puis ajustez votre profondeur en conséquence"],
            "Confrontez chaque mot-clé à votre CV pour trouver des preuves ; les mots sans preuve alimentent le plan faiblesses", "Contexte de l'entreprise", "Extrait de l'offre",
            "Entreprise : ", "Poste : ", "Tour {0} · {1}", "Intervenants : ", "Format : ", "Date : ",
            ["Rejoignez la réunion 5 minutes à l'avance", "Ouverture : Hi, thanks for taking the time to speak with me today.", "Confirmez les noms : Before we start — could you let me know how to pronounce your name correctly?", "Si personne ne rejoint : attendez 15 minutes → envoyez un courriel au recruteur pour laisser une trace → attendez un nouveau créneau le jour même. Ouverture : Hi — I joined the call at [time] and haven't seen anyone join yet. Could we reschedule?"],
            "Sur place : arrivez tôt, apportez de l'eau et un CV imprimé, passez d'abord à l'accueil",
            ["Notez la taille de l'équipe, les responsabilités du poste et les mots-clés du projet actuel", "Répondez : That sounds great — the part about X is exactly what I've been doing.", "Notes en trois colonnes : équipe actuelle / défis actuels / ce qu'ils attendent de ce poste — puisez réponses et questions dans les deux dernières colonnes"],
            ["Version 30 secondes", "Version 60 secondes", "Version 120 secondes"], Scaffolds,
            ["Les trois versions suivent Présent–Passé–Futur : une phrase pour le présent, un passé aligné sur l'offre, un futur lié à un changement concret chez l'entreprise cible", "Discipline des accroches : exactement deux (le projet clé + l'élément le plus aligné sur l'offre)", "Remplissez les [crochets] avec votre expérience réelle — n'inventez jamais de chiffres ni de noms d'entreprise"],
            ["Forme en quatre temps : contexte → défi → approche → résultats, les chiffres d'abord", "Pour chaque projet, ayez échelle, gain de vitesse ou d'économie et impact prêts avant d'entrer — un projet sans chiffres n'est pas votre histoire principale", "Pour chaque choix technique, préparez un compromis : pourquoi ce choix, ce qui a été abandonné"],
            ["Couche architecture → ouverture : The system is structured as …", "Couche données → ouverture : The data flows as …", "Couche défaillance → ouverture : When X failed, we …", "Couche compromis → ouverture : We chose X over Y because …"],
            ["Échelle : utilisateurs / volume de données / QPS", "Gain de vitesse ou d'économie : pourcentage et valeur absolue", "Impact : équipe, ligne métier, clients"],
            ["Questions de concept en quatre temps : une phrase de définition → une phrase de mécanisme → une phrase de compromis → une phrase de votre projet", "Si vous ne savez pas : énoncez d'abord la limite, puis reliez à l'expérience transférable la plus proche. Ouverture : I haven't worked with X directly, but the closest experience I have is …"],
            "Fiche de discussion conception",
            ["1. Problème + questions de clarification : un concept par phrase ; chaque question doit orienter la conception", "2. Fonctionnel (TOP 3) / Non fonctionnel (3–5 points, OBLIGATOIREMENT quantifiés — chiffre manquant : demandez, n'inventez jamais) + entités clés + API", "3. Conception générale, une fonctionnalité à la fois", "4. Chaque exigence non fonctionnelle projetée sur un composant du schéma", "5. Approfondissements bonus : avec chiffres, modèles et compromis, dessinés dans le schéma final", "Si une question de conception surgit, répondez avec ces cinq étapes"],
            ["1. Écoutez tout le problème avant de répondre — ne vous précipitez pas", "2. Reformulez et confirmez, un concept par phrase, puis clarifiez. Ouverture : Let me restate the problem to make sure I got it right …", "3. Exposez l'approche avant d'écrire du code", "4. Pensez à voix haute ; annoncez les cas limites (entrée vide / casse / ponctuation) avant de les rencontrer", "5. Concluez en vérifiant : petit exemple, complexité, et format de sortie conforme à l'énoncé caractère pour caractère", "Discipline : le format de sortie doit correspondre exactement à l'énoncé — aucune improvisation"],
            ["1. Problème + clarifications : un concept par phrase, pas de QCM ; chaque question doit orienter la conception", "2. Fonctionnel : « Users should be able to… », TOP 3 ; Non fonctionnel : « The system should be able to… », 3–5 points, OBLIGATOIREMENT quantifiés — chiffre manquant : demandez, n'inventez jamais ; + entités clés + API", "3. Conception générale, une fonctionnalité à la fois", "4. Chaque exigence non fonctionnelle projetée sur un composant du schéma", "5. Approfondissements bonus : avec chiffres, modèles et compromis, dessinés dans le schéma final", "Discipline d'exposé : commentez pendant que vous dessinez — le récit prime sur le schéma", "Discipline d'exposé : n'ouvrez pas de nouveau sujet en conclusion"],
            "Before I start designing, let me clarify a few things that will decide the direction …",
            ["STAR+L : Situation limitée à deux phrases, Action la moitié du récit avec des actions concrètes au pluriel, Résultat chiffré, phrase de leçon en conclusion", "Cible ≤ 2 minutes à l'oral (environ ≤ 300 mots anglais)", "N'utilisez que l'expérience figurant réellement dans le CV ou les retours ; une zone sans histoire l'indique explicitement"],
            ["ownership (initiative)", "conflict (conflit)", "failure (échec)", "influence", "ambiguity (ambiguïté)"],
            "Fiches d'écart conceptuel", "Écart de pile technique", "Parler de ses faiblesses", "Autres faiblesses identifiées",
            ["Énoncez la limite, reliez à l'expérience transférable la plus proche, indiquez votre progression", "Ouverture : I haven't worked with X in production, but the closest experience I have is … and I'm currently …"],
            ["Choisissez un point réel, en cours de correction et prouvé : nommez le problème → l'action prise → l'amélioration vérifiable", "Pas de fausses réponses : jamais de formules comme « mon plus grand défaut est le perfectionnisme »"],
            ["Préparez 3 questions par tour : livrables, défis, processus", "Au moins une question doit reprendre un mot-clé réel de leur présentation", "Par audience : Manager / Tech Lead / Director / Recruteur·RH"],
            ["Pour le Manager", "Pour le Tech Lead", "Pour le Director", "Pour le Recruteur·RH"], AskBackQuestions,
            ["Demandez d'abord la fourchette. Ouverture : Could you share the range budgeted for this role?", "N'annoncez qu'une fourchette cible, en une phrase, jamais un ancien salaire bas ni un chiffre unique qui vous ancre. Cadre : My target is [range].", "Si l'offre indique une fourchette, ne citez que son texte d'origine — aucun autre montant"],
            ["Cadre autorisation de travail : My work authorization is [type], valid until [date].", "Cadre parrainage : I [do / do not] require sponsorship.", "Cadre disponibilité : I can start within [timeframe]."],
            "Avant la fin de chaque tour, confirmez les prochaines étapes et le calendrier (en écho à la couche recruteur de Vos questions — ne le préparez pas deux fois)",
            "Note présélection : l'exploration salariale a le plus souvent lieu en présélection — demandez la fourchette avant tout",
            ["Réaffirmez votre intérêt : Based on what I've heard, this role is exactly what I'm looking for.", "Le jour même : testez l'équipement et le réseau à l'avance ; eau et CV à portée de main", "En cas de blocage : demandez une reformulation pour gagner du temps. Ouverture : Could you rephrase that? I want to make sure I understand the question correctly. Puis énoncez la limite et reliez", "Suivi post-entretien (canevas seulement, jamais envoyé pour vous) : remerciement le jour même — court, pertinent, citant un sujet précis du tour, sans déballer le CV ; relance à leur échéance + 2 jours, un seul courriel"],
            "Question passée", "Général", "Sources de retours récupérées",
            DefaultQs,
            ["Compartiments + fonction de hachage + chaînage/adressage ouvert ; rehachage au redimensionnement", "Structure forte et transactions → SQL ; données semi-structurées massives et extension horizontale → NoSQL", "Partez des métriques et journaux pour localiser la couche, puis reproduisez, ajoutez de l'observabilité, resserrez", "Machine à états + contexte de synchronisation ; interblocages et ConfigureAwait sont les pièges classiques", "Modèle oignon : chaque requête traverse deux fois ; auth/journalisation/exceptions à l'extérieur", "WHERE filtre les lignes, HAVING filtre les groupes — après agrégation, seul HAVING convient"]),
        _ => new PrepReportLocalTexts(
            ["Round brief & company research", "Warm-up", "Hear their introduction", "Self-introduction", "Project deep-dive", "Technical Q&A", "Coding round playbook", "System design playbook", "Behavioral · story bank", "Weakness & past-question game plan", "Questions to ask", "Compensation & process", "Wrap-up & reminders"],
            "No company background provided for this round", "No JD provided for this round", "No fetched experiences available", "No resume provided — this section is a generic framework",
            "No identified weaknesses or stuck questions yet", "The story bank needs AI generation or your own entries from the checklist — the system never invents stories", "Item-by-item matching needs AI generation",
            "nothing yet",
            ["Screening focus: clarify role scope, team structure, process and timeline first", "Design focus: clarify requirements and scale estimates first, then sketch the data model and APIs", "Coding focus: hear the whole problem first, think aloud while coding, verify at the end", "Behavioral focus: STAR structure, numbers in the Result", "Final focus: motivation and fit — why us, why now", "Confirm what this round focuses on, then match your depth to it"],
            "Match each keyword against your resume for evidence; unmatched keywords land in the weakness plan", "Company background", "JD excerpt",
            "Company: ", "Role: ", "Round {0} · {1}", "Interviewers: ", "Format: ", "Date: ",
            ["Join the meeting 5 minutes early", "Open with: Hi, thanks for taking the time to speak with me today.", "Confirm names: Before we start — could you let me know how to pronounce your name correctly?", "If no one joins: wait 15 minutes → email the recruiter to leave a timestamp → wait for a reschedule the same day. Open with: Hi — I joined the call at [time] and haven't seen anyone join yet. Could we reschedule?"],
            "Onsite: arrive early, bring water and a printed resume, check in at reception first",
            ["Note the team size, the role's responsibilities, and current project keywords", "Respond with: That sounds great — the part about X is exactly what I've been doing.", "Three-column notes: team today / current challenges / what they need this person to solve — draw responses and questions from the last two columns"],
            ["30-second version", "60-second version", "120-second version"], Scaffolds,
            ["All three versions follow Present–Past–Future: one line for the Present, Past matched to JD keywords, Future tied to a concrete change at the target company", "Hook discipline: plant exactly two hooks (the core project + the item most aligned with the JD)", "Fill the [bracketed slots] with real experience — never invent numbers or company names"],
            ["Four-part form: background → challenge → approach → results, numbers first", "For each project, have scale, speedup or cost saved, and impact ready before you walk in — a project without numbers is not your lead story", "For each tech choice, prepare one trade-off line: why it won, what you gave up"],
            ["Architecture layer → open with: The system is structured as …", "Data layer → open with: The data flows as …", "Failure layer → open with: When X failed, we …", "Trade-off layer → open with: We chose X over Y because …"],
            ["Scale: users / data volume / QPS", "Speedup or cost saved: percentage and absolute value", "Impact: team, business line, customers"],
            ["Concept questions in four beats: one line of definition → one line of mechanism → one line of trade-off → one line from your project", "When you don't know: state the boundary first, then bridge to the closest transferable experience. Open with: I haven't worked with X directly, but the closest experience I have is …"],
            "Design discussion card",
            ["1. Problem + clarifying questions: one concept per sentence; each question must decide a design direction", "2. Functional (TOP 3 only) / Non-functional (3–5 items, MUST be quantified — if a number is missing, ask, never invent) + Core Entities + API", "3. High-level design, one functional at a time", "4. Each NFR mapped onto a diagram component", "5. Bonus deep dives: with numbers, patterns and trade-offs, drawn into the final diagram", "If a design question comes up mid-round, answer with these five steps"],
            ["1. Hear the whole problem before answering — don't jump in", "2. Restate and confirm, one concept per sentence, then clarify. Open with: Let me restate the problem to make sure I got it right …", "3. Talk through the approach before writing code", "4. Think aloud; state edge cases (empty input / casing / punctuation) before you hit them", "5. Close by verifying: run a small example, state complexity, and match the output format to the problem statement character for character", "Discipline: the output format must match the problem statement exactly — no improvising"],
            ["1. Problem + clarifying questions: one concept per sentence, no multiple-choice questions; each question must decide a design direction", "2. Functional: \"Users should be able to…\", TOP 3 only; Non-functional: \"The system should be able to…\", 3–5 items, MUST be quantified — if a number is missing, ask, never invent; + Core Entities + API", "3. High-level design, one functional at a time", "4. Each NFR mapped onto a diagram component", "5. Bonus deep dives: with numbers, patterns and trade-offs, drawn into the final diagram", "Delivery discipline: narrate while you draw — Narrative > diagram", "Delivery discipline: don't open new topics in the close"],
            "Before I start designing, let me clarify a few things that will decide the direction …",
            ["STAR+L: Situation capped at two sentences, Action half the story with concrete plural actions, Result with numbers, closing Learnings line", "Target ≤ 2 minutes spoken (about ≤ 300 English words)", "Only use experience that actually appears in the resume or past experiences; a coverage area with no story says so explicitly"],
            ["ownership", "conflict", "failure", "influence", "ambiguity"],
            "Concept gap cards", "Stack gap", "Talking about weaknesses", "Other identified weaknesses",
            ["State the boundary, bridge to the closest transferable experience, and name your study status", "Open with: I haven't worked with X in production, but the closest experience I have is … and I'm currently …"],
            ["Pick a point that is real, being fixed, and evidenced: name the problem → the action taken → the verifiable improvement", "No fake answers: never use lines like “my biggest weakness is perfectionism”"],
            ["Prepare 3 questions per round: one on deliverables, one on challenges, one on process", "At least one question must reuse a real keyword from their introduction", "Layered by audience: Manager / Tech Lead / Director / Recruiter·HR"],
            ["For the Manager", "For the Tech Lead", "For the Director", "For the Recruiter·HR"], AskBackQuestions,
            ["Ask the range before stating anything. Open with: Could you share the range budgeted for this role?", "State only a target range, one line, never a past low point, never a single number that anchors you. Frame: My target is [range].", "If the JD states a band, quote only that band's original text — no other money figures anywhere"],
            ["Work authorization frame: My work authorization is [type], valid until [date].", "Sponsorship frame: I [do / do not] require sponsorship.", "Start date frame: I can start within [timeframe]."],
            "Before each round ends, confirm next steps and the timeline (cross-referenced with the recruiter layer in Questions to ask — don't prepare it twice)",
            "Screening-round note: salary probing most often happens in screening rounds — ask the range before stating anything",
            ["Restate your interest: Based on what I've heard, this role is exactly what I'm looking for.", "On the day: test equipment and network early; water and resume within reach", "If you're stuck live: ask for a rephrase to buy time. Open with: Could you rephrase that? I want to make sure I understand the question correctly. Then state the boundary and bridge", "Post-interview follow-up (skeleton only, never sent for you): same-day thank-you — short, relevant, naming one specific topic from this round, no resume dump; check-in at their window + 2 days, one email max"],
            "Past question", "General", "Fetched experience sources",
            DefaultQs,
            ["Buckets + hash function + chaining/open addressing; rehash on resize", "Strong structure and transactions → SQL; massive semi-structured data and horizontal scale → NoSQL", "Start from metrics and logs to locate the layer, then reproduce, add observability, narrow down", "State machine + synchronization context; deadlocks and ConfigureAwait are the classic traps", "Onion model: each request passes through twice; auth/logging/exceptions on the outside", "WHERE filters rows, HAVING filters groups — post-aggregation filtering can only use HAVING"])
    };
}

// ============================ 生成输入与分节 builder(规格 §5/§9) ============================

/// <summary>生成输入(Handler 由领域对象映射而来;测试可直接构造,不碰数据库)。</summary>
public sealed record PrepReportSourceData(
    string CompanyName, string Role, string? CompanyProfile,
    int RoundOrder, string Stage, string? Interviewers, string? Format, DateOnly? ScheduledDate,
    string? RequestJd, string? EntryJd, string? EntryJdSummary, string? ResumeText, string? Experiences,
    bool QuestionBankEnabled, bool CompanyBackgroundEnabled,
    IReadOnlyList<(string Title, string Url)> FetchedItems,
    IReadOnlyList<PrepQuestionInput> Questions,
    IReadOnlyList<PrepWeaknessInput> Weaknesses)
{
    public string? Jd => !string.IsNullOrWhiteSpace(RequestJd) ? RequestJd
        : !string.IsNullOrWhiteSpace(EntryJd) ? EntryJd : EntryJdSummary;
}

/// <summary>本地分节 builder(规格 §9):本地路与归一化脚手架共用同一组代码,不许另写简化版。</summary>
public static class PrepReportBuilders
{
    public static JsonObject FactBlock(PrepReportSourceData d, PrepReportLocalTexts t)
    {
        var items = new List<string>
        {
            t.FactCompany + d.CompanyName,
            t.FactRole + d.Role,
            string.Format(t.FactRound, d.RoundOrder, d.Stage)
        };
        if (!string.IsNullOrWhiteSpace(d.Interviewers)) items.Add(t.FactInterviewers + d.Interviewers);
        if (!string.IsNullOrWhiteSpace(d.Format)) items.Add(t.FactFormat + d.Format);
        if (d.ScheduledDate is not null) items.Add(t.FactDate + d.ScheduledDate.Value.ToString("yyyy-MM-dd"));
        return PrepReportSections.PointsBlock(null, items);
    }

    public static JsonObject DesignCardBlock(PrepReportLocalTexts t) =>
        PrepReportSections.PointsBlock(t.DesignCardTitle, t.DesignCard);

    public static string PrejudgeSentence(PrepReportSourceData d, RoundClassification cls, PrepReportLocalTexts t)
    {
        // 规格未钉多分类并存时的优先级;实现取舍:编程 > 设计 > 行为 > 筛选 > 终面 > 默认(内容决定性最强的在先)。
        if (cls.IsCoding) return t.RoundBriefPrejudge[2];
        if (cls.IsDesign) return t.RoundBriefPrejudge[1];
        if (cls.IsBehavioral) return t.RoundBriefPrejudge[3];
        if (cls.IsScreen) return t.RoundBriefPrejudge[0];
        if (cls.IsFinal) return t.RoundBriefPrejudge[4];
        return t.RoundBriefPrejudge[5];
    }

    private static bool IsConceptWeakness(PrepWeaknessInput w) =>
        w.Category is WeaknessCategory.TechnicalDepth or WeaknessCategory.TechnicalStructure;

    private static JsonObject WeaknessPointsBlock(PrepWeaknessInput w)
    {
        var items = new List<string>();
        if (!string.IsNullOrWhiteSpace(w.Detail)) items.Add(w.Detail!);
        else if (!string.IsNullOrWhiteSpace(w.Suggestion)) items.Add(w.Suggestion!);
        return PrepReportSections.PointsBlock(w.Title, items);
    }

    private static string? MatchingWeaknessSuggestion(
        PrepQuestionInput q, IReadOnlyList<PrepWeaknessInput> weaknesses)
    {
        foreach (var w in weaknesses)
        {
            if (string.IsNullOrWhiteSpace(w.Title)) continue;
            if (q.Text.Contains(w.Title, StringComparison.OrdinalIgnoreCase)
                || w.Title.Contains(q.Text, StringComparison.OrdinalIgnoreCase))
                return w.Suggestion;
        }
        return null;
    }

    public static JsonObject RoutedQuestionItem(
        PrepQuestionInput q, string pastTag, IReadOnlyList<string> weaknessTitles)
    {
        var weak = weaknessTitles.Any(t => !string.IsNullOrWhiteSpace(t)
            && q.Text.Contains(t, StringComparison.OrdinalIgnoreCase));
        return new JsonObject
        {
            ["question"] = q.Text,
            ["category"] = q.Category.ToString(),
            ["sourceTags"] = PrepReportSections.Tags(pastTag),
            ["weaknessRelated"] = weak,
            ["stdSpoken"] = JsonValue.Create((string?)null),
            ["stdTechnical"] = q.RecommendedAnswer is null
                ? JsonValue.Create((string?)null) : JsonValue.Create(q.RecommendedAnswer),
            ["knowledgeId"] = JsonValue.Create((string?)null)
        };
    }

    public static JsonObject PlanCardItem(
        PrepQuestionInput q, string pastTag, IReadOnlyList<PrepWeaknessInput> weaknesses)
    {
        var o = new JsonObject
        {
            ["question"] = q.Text,
            ["category"] = q.Category.ToString(),
            ["sourceTags"] = PrepReportSections.Tags(pastTag),
            ["weaknessRelated"] = true,
            ["stdSpoken"] = JsonValue.Create((string?)null),
            ["stdTechnical"] = q.RecommendedAnswer is null
                ? JsonValue.Create((string?)null) : JsonValue.Create(q.RecommendedAnswer),
            ["knowledgeId"] = JsonValue.Create((string?)null)
        };
        var note = !string.IsNullOrWhiteSpace(q.StuckReason) ? q.StuckReason
            : MatchingWeaknessSuggestion(q, weaknesses);
        if (!string.IsNullOrWhiteSpace(note)) o["note"] = note;
        return o;
    }

    /// <summary>归一化 §6.5 第 1 步补卡用的本地形态卡(note 只取 StuckReason,规格原文)。</summary>
    public static JsonObject LocalPlanCardItem(PrepQuestionInput q, string pastTag)
    {
        var o = new JsonObject
        {
            ["question"] = q.Text,
            ["category"] = q.Category.ToString(),
            ["sourceTags"] = PrepReportSections.Tags(pastTag),
            ["weaknessRelated"] = true,
            ["stdSpoken"] = JsonValue.Create((string?)null),
            ["stdTechnical"] = q.RecommendedAnswer is null
                ? JsonValue.Create((string?)null) : JsonValue.Create(q.RecommendedAnswer),
            ["knowledgeId"] = JsonValue.Create((string?)null)
        };
        if (!string.IsNullOrWhiteSpace(q.StuckReason)) o["note"] = q.StuckReason;
        return o;
    }

    private static JsonObject Section(string id, string title,
        IEnumerable<JsonObject> blocks, IEnumerable<JsonObject> questions)
    {
        var b = new JsonArray();
        foreach (var x in blocks) b.Add(x);
        var q = new JsonArray();
        foreach (var x in questions) q.Add(x);
        return new JsonObject { ["id"] = id, ["title"] = title, ["blocks"] = b, ["questions"] = q };
    }

    public static JsonObject BuildSection(
        PrepReportSourceData d, PrepReportLocalTexts t, RoundClassification cls,
        RoutedPrepQuestions routed, string id)
    {
        var title = PrepReportSections.TitleFor(id, t);
        var weaknessTitles = d.Weaknesses.Select(w => w.Title).ToList();
        JsonObject Routed(PrepQuestionInput q) => RoutedQuestionItem(q, t.PastTag, weaknessTitles);
        switch (id)
        {
            case "round-brief":
            {
                var blocks = new List<JsonObject> { FactBlock(d, t) };
                blocks.Add(d.CompanyBackgroundEnabled && !string.IsNullOrWhiteSpace(d.CompanyProfile)
                    ? PrepReportSections.PointsBlock(t.CompanyExcerptTitle,
                        [PrepReportSections.Trunc(d.CompanyProfile!, 600)])
                    : PrepReportSections.PointsBlock(null, [t.NoCompanyBackground]));
                blocks.Add(!string.IsNullOrWhiteSpace(d.Jd)
                    ? PrepReportSections.PointsBlock(t.JdExcerptTitle, [PrepReportSections.Trunc(d.Jd!, 600)])
                    : PrepReportSections.PointsBlock(null, [t.NoJd]));
                if (!string.IsNullOrWhiteSpace(d.Jd))
                    blocks.Add(PrepReportSections.PointsBlock(null, [t.JdGuide]));
                blocks.Add(PrepReportSections.PointsBlock(null, [PrejudgeSentence(d, cls, t)]));
                blocks.Add(d.FetchedItems.Count > 0
                    ? PrepReportSections.ListBlock(t.FetchedTitle,
                        d.FetchedItems.Select(f => $"{f.Title} — {f.Url}"))
                    : PrepReportSections.PointsBlock(null, [t.NoFetched]));
                return Section(id, title, blocks, []);
            }
            case "greeting":
            {
                var items = t.Greeting.ToList();
                if (string.Equals(d.Format, "Onsite", StringComparison.OrdinalIgnoreCase))
                    items.Add(t.OnsiteLine);
                return Section(id, title, [PrepReportSections.PointsBlock(null, items)], []);
            }
            case "intro-listen":
                return Section(id, title, [PrepReportSections.PointsBlock(null, t.IntroListen)], []);
            case "self-intro":
            {
                var blocks = new List<JsonObject> { PrepReportSections.PointsBlock(null, t.SelfIntroGuide) };
                for (var i = 0; i < 3; i++)
                {
                    var text = t.SelfIntroScaffolds[i]
                        .Replace("{company}", d.CompanyName)
                        .Replace("{role}", d.Role);
                    blocks.Add(PrepReportSections.ScriptBlock(t.SelfIntroTitles[i], text));
                }
                return Section(id, title, blocks, []);
            }
            case "deep-dive":
            {
                var blocks = new List<JsonObject>();
                if (string.IsNullOrWhiteSpace(d.ResumeText))
                    blocks.Add(PrepReportSections.PointsBlock(null, [t.NoResume]));
                blocks.Add(PrepReportSections.PointsBlock(null, t.DeepDiveGuide));
                blocks.Add(PrepReportSections.ListBlock(null, t.DeepDiveTree));
                blocks.Add(PrepReportSections.ListBlock(null, t.DeepDiveNumbers));
                return Section(id, title, blocks, []);
            }
            case "tech-qa":
            {
                var blocks = new List<JsonObject> { PrepReportSections.PointsBlock(null, t.TechQaGuide) };
                if (!cls.IsDesign) blocks.Add(DesignCardBlock(t));
                var questions = routed.TechQa.Select(Routed).ToList();
                if (questions.Count == 0)
                {
                    for (var i = 0; i < t.DefaultQuestions.Length; i++)
                    {
                        questions.Add(new JsonObject
                        {
                            ["question"] = t.DefaultQuestions[i],
                            ["category"] = "Technical",
                            ["sourceTags"] = PrepReportSections.Tags(t.GeneralTag),
                            ["weaknessRelated"] = false,
                            ["stdSpoken"] = JsonValue.Create((string?)null),
                            ["stdTechnical"] = t.DefaultPoints[i],
                            ["knowledgeId"] = JsonValue.Create((string?)null)
                        });
                    }
                }
                return Section(id, title, blocks, questions);
            }
            case "coding-round":
                return Section(id, title, [PrepReportSections.PointsBlock(null, t.CodingGuide)], []);
            case "design-round":
            {
                var blocks = new List<JsonObject>
                {
                    PrepReportSections.PointsBlock(null, t.DesignGuide.Concat([t.DesignOpening]))
                };
                return Section(id, title, blocks, routed.DesignRound.Select(Routed));
            }
            case "behavioral":
            {
                var blocks = new List<JsonObject>();
                if (string.IsNullOrWhiteSpace(d.ResumeText))
                    blocks.Add(PrepReportSections.PointsBlock(null, [t.NoResume]));
                blocks.Add(PrepReportSections.PointsBlock(null, t.BehavioralGuide));
                blocks.Add(PrepReportSections.ListBlock(null, t.BehavioralCoverage));
                blocks.Add(PrepReportSections.PointsBlock(null, [t.StoryBankEmpty]));
                return Section(id, title, blocks, routed.Behavioral.Select(Routed));
            }
            case "weakness-plan":
            {
                var blocks = new List<JsonObject>();
                var hasData = d.Weaknesses.Count > 0 || routed.Stuck.Count > 0;
                if (!hasData)
                {
                    blocks.Add(PrepReportSections.PointsBlock(null, [t.NoWeaknessOrStuck]));
                    blocks.Add(PrepReportSections.PointsBlock(t.SelfStatementTitle, t.SelfStatementGuide));
                    blocks.Add(PrepReportSections.PointsBlock(null,
                    [
                        $"{t.ConceptAreaTitle} — {t.EmptyAreaMarker}",
                        $"{t.StackGapTitle} — {t.EmptyAreaMarker}",
                        $"{t.OtherWeaknessTitle} — {t.EmptyAreaMarker}"
                    ]));
                }
                else
                {
                    foreach (var w in d.Weaknesses.Where(IsConceptWeakness))
                        blocks.Add(WeaknessPointsBlock(w));
                    blocks.Add(PrepReportSections.PointsBlock(t.StackGapTitle, t.StackGapFramework));
                    if (!string.IsNullOrWhiteSpace(d.Jd))
                        blocks.Add(PrepReportSections.PointsBlock(t.JdExcerptTitle,
                            [PrepReportSections.Trunc(d.Jd!, 600)]));
                    blocks.Add(PrepReportSections.PointsBlock(t.SelfStatementTitle, t.SelfStatementGuide));
                    foreach (var w in d.Weaknesses.Where(w => !IsConceptWeakness(w)))
                        blocks.Add(WeaknessPointsBlock(w));
                }
                var cards = routed.Stuck.Select(q => PlanCardItem(q, t.PastTag, d.Weaknesses)).ToList();
                return Section(id, title, blocks, cards);
            }
            case "ask-back":
            {
                var blocks = new List<JsonObject> { PrepReportSections.PointsBlock(null, t.AskBackGuide) };
                for (var i = 0; i < 4; i++)
                    blocks.Add(PrepReportSections.ListBlock(t.AskBackLayerTitles[i], t.AskBackLayerQuestions[i]));
                return Section(id, title, blocks, []);
            }
            case "comp-process":
            {
                var blocks = new List<JsonObject>
                {
                    PrepReportSections.PointsBlock(null, t.CompGuideSalary),
                    PrepReportSections.PointsBlock(null, t.CompGuideStatus),
                    PrepReportSections.PointsBlock(null, [t.CompGuideProcess])
                };
                if (cls.IsScreen) blocks.Add(PrepReportSections.PointsBlock(null, [t.CompGuideScreenNote]));
                return Section(id, title, blocks, routed.CompProcess.Select(Routed));
            }
            case "closing":
                return Section(id, title, [PrepReportSections.PointsBlock(null, t.Closing)], []);
            default:
                throw new ArgumentOutOfRangeException(nameof(id), id, "未知规范节位");
        }
    }

    public static JsonObject BuildLocalReport(
        PrepReportSourceData d, string lang, PrepReportLocalTexts t,
        RoundClassification cls, RoutedPrepQuestions routed, JsonObject sources)
    {
        var sections = new JsonArray();
        foreach (var id in PrepReportSections.ExpectedSectionIds(cls))
            sections.Add(BuildSection(d, t, cls, routed, id));
        return new JsonObject
        {
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("o"),
            ["generatedBy"] = "local-aggregation",
            ["sources"] = sources,
            ["sections"] = sections,
            ["structureVersion"] = 2
        };
    }
}

// ============================ AI 提示词(规格 §8) ============================

/// <summary>AI 路提示词:系统提示词 SP-1…SP-7 与用户提示词分组供给(规格 §8.1/§8.2)。</summary>
public static class PrepReportPrompts
{
    public static string LanguageName(string lang) => lang switch
    {
        "zh" => "Simplified Chinese (简体中文)",
        "fr" => "French (Français)",
        _ => "English"
    };

    public static string SystemPrompt(string lang) =>
        "You are an interview coach for North American tech companies. Output exactly one JSON object, no other text.\n" +
        $"Report language: explanations, titles and technical notes in {LanguageName(lang)}. " +
        "SPOKEN LINES STAY IN ENGLISH: every script block text, every stdSpoken, and every verbatim line the candidate will say aloud MUST be in English regardless of report language. Interview questions are quoted in English. " +
        "Before finishing, check: every script text and every stdSpoken is in English; every question is in English; explanations are in " + LanguageName(lang) + ".\n" +
        "Structure: {\"sections\":[{\"id\":\"round-brief|greeting|intro-listen|self-intro|deep-dive|tech-qa|coding-round|design-round|behavioral|weakness-plan|ask-back|comp-process|closing\",\"title\":\"section title\",\"blocks\":[{\"type\":\"script|points|list\",\"title\":\"optional\",\"text\":\"body for script\",\"items\":[\"bullets\"]}],\"questions\":[{\"question\":\"...\",\"category\":\"...\",\"sourceTags\":[\"...\"],\"weaknessRelated\":true/false,\"stdSpoken\":\"spoken standard answer\",\"stdTechnical\":\"technical standard answer\",\"note\":\"game-plan diagnosis (weakness-plan cards only)\"}]}]}. " +
        "Output sections in this exact order: round-brief, greeting, intro-listen, self-intro, deep-dive, tech-qa, coding-round (only if the user prompt says coding=true), design-round (only if the user prompt says design=true), behavioral, weakness-plan, ask-back, comp-process, closing. " +
        "The user prompt states the round classification (coding/design/screen/behavioral/final) computed by the server — use it as given, do not re-infer the round type from the stage text. When design=false, do NOT output a design-round section; instead put one design discussion card block inside tech-qa. When coding=false, do NOT output a coding-round section.\n" +
        "Routing: past questions are grouped in the user prompt — stuck questions go ONLY to weakness-plan as game-plan cards and must not appear in tech-qa or any other section; behavioral questions go to behavioral; screening questions go to comp-process; system-design questions go to design-round (or tech-qa when design=false); technical and honesty-boundary questions go to tech-qa. The same question must not appear in two sections.\n" +
        "Per-section content: round-brief — three things about the company (what it sells, what is changing, why this role exists), a JD keyword → resume evidence map with an explicit line for keywords with no evidence, and a prediction of this round's likely follow-up directions. " +
        "self-intro — three script blocks, 30 / 60 / 120 seconds (60–90 / 120–160 / 240–300 English words), each Present–Past–Future with exactly two hooks (the core project + the item most aligned with the JD); if no resume was provided, output ONLY slot scaffolds (complete English sentence frames with [bracketed slots]) and NEVER write any concrete experience, past employer name, or metric. " +
        "deep-dive — a four-layer follow-up tree (architecture / data / failure / trade-off) per lead project plus a numbers card (scale, speedup or cost saved, impact). " +
        "tech-qa — concept questions answered in four beats (one line of definition → one line of mechanism → one line of trade-off → one line from the project); when you don't know, say the boundary first and bridge to the closest transferable experience. " +
        "coding-round — five beats: hear the whole problem first; restate and confirm one concept per sentence; approach before code; think aloud with edge cases stated up front; close by verifying with a small example, complexity, and output format matched to the problem statement character for character. " +
        "design-round — five steps, exactly: (1) problem + clarifying questions, one concept per sentence, no multiple-choice questions, each question must decide a design direction; (2) Functional (\"Users should be able to…\", TOP 3 only) / Non-functional (\"The system should be able to…\", 3–5 items, MUST be quantified — if a number is missing, ask, never invent) + Core Entities + API; (3) high-level design, one functional at a time; (4) each NFR mapped onto a diagram component; (5) bonus deep dives with numbers, patterns and trade-offs drawn into the final diagram. Narrate while you draw; do not open new topics in the close. " +
        "behavioral — a story × coverage map for ownership / conflict / failure / influence / ambiguity, then one script block per story in STAR+L (Situation capped at two sentences, Action half the story with concrete plural actions, Result with numbers, closing Learnings); use ONLY experience that actually appears in the resume or past experiences — a coverage area with no story must say so explicitly, never invent a story. " +
        "weakness-plan — concept-gap cards for TechnicalDepth/TechnicalStructure weaknesses, a stack-gap section (state the boundary, bridge, study status), a weakness self-talk section (real, being fixed, evidenced; no fake answers), and a section for all other identified weaknesses. " +
        "ask-back — questions layered by audience: Manager, Tech Lead, Director, Recruiter·HR, each question in English. " +
        "comp-process — salary discipline: ask the range before stating anything; state only a target range in one line, never a past low point, never a single anchoring number; except a band quoted verbatim from the JD, no money figures anywhere. " +
        "closing — post-interview follow-up as a skeleton only (same-day thank-you: short, relevant, naming one specific topic; check-in at their window + 2 days, one email max); the system never sends anything.\n" +
        "Grounding: technical questions may only be grounded in JD keywords, the fetched interview experiences given in the prompt, the candidate's past questions, and identified weaknesses (weaknessRelated=true). Never invent interview experiences or attribute statements to a source that did not say them. Standard answers: first-person spoken version + bullet-style technical version, North American interview style.\n" +
        "Empty states: if a section has no input, write its explicit empty-state line or default content — never omit a section, never leave a section with empty blocks and empty questions.";

    public static string UserPrompt(PrepReportSourceData d, string lang, RoundClassification cls)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Prep target: {d.CompanyName} — {d.Role} (Round {d.RoundOrder} / {d.Stage})");
        sb.AppendLine($"Output language: {LanguageName(lang)}");
        sb.AppendLine($"Round classification: coding={cls.IsCoding.ToString().ToLowerInvariant()}, design={cls.IsDesign.ToString().ToLowerInvariant()}, screen={cls.IsScreen.ToString().ToLowerInvariant()}, behavioral={cls.IsBehavioral.ToString().ToLowerInvariant()}, final={cls.IsFinal.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(d.Interviewers)) sb.AppendLine($"Interviewers: {d.Interviewers}");
        if (!string.IsNullOrWhiteSpace(d.Format)) sb.AppendLine($"Format: {d.Format}");
        if (!string.IsNullOrWhiteSpace(d.Jd)) sb.AppendLine($"## JD\n{PrepReportSections.Trunc(d.Jd!, 6000)}");
        if (d.CompanyBackgroundEnabled && !string.IsNullOrWhiteSpace(d.CompanyProfile))
            sb.AppendLine($"## Company background\n{PrepReportSections.Trunc(d.CompanyProfile, 3000)}");
        if (!string.IsNullOrWhiteSpace(d.ResumeText)) sb.AppendLine($"## Submitted resume\n{PrepReportSections.Trunc(d.ResumeText, 6000)}");
        if (!string.IsNullOrWhiteSpace(d.Experiences)) sb.AppendLine($"## Interview experiences pasted by the user\n{PrepReportSections.Trunc(d.Experiences, 5000)}");
        foreach (var (heading, items) in new[]
        {
            ("## Fetched web interview experiences (search results; ground questions only in these; also ground for round-brief)", d.FetchedItems)
        })
        {
            if (items.Count > 0)
            {
                sb.AppendLine(heading);
                foreach (var f in items.Take(5)) sb.AppendLine($"- {f.Title} — {f.Url}");
            }
        }
        if (d.QuestionBankEnabled && d.Questions.Count > 0)
        {
            var stuck = d.Questions.Where(q => q.GotStuck).OrderBy(q => q.Sequence).Take(10).ToList();
            var rest = d.Questions.Where(q => !q.GotStuck).OrderBy(q => q.Sequence).Take(30).ToList();
            if (stuck.Count > 0)
            {
                sb.AppendLine("## Past questions you got stuck on (route ONLY to weakness-plan as game-plan cards)");
                foreach (var q in stuck)
                    sb.AppendLine($"- {PrepReportSections.Trunc(q.Text, 250)} (stuck reason: {q.StuckReason ?? ""}; recommended answer: {PrepReportSections.Trunc(q.RecommendedAnswer ?? "", 150)})");
            }
            var beh = rest.Where(q => q.Category == QuestionCategory.Behavioral).ToList();
            if (beh.Count > 0)
            {
                sb.AppendLine("## Past behavioral questions (route to behavioral)");
                foreach (var q in beh) sb.AppendLine($"- {PrepReportSections.Trunc(q.Text, 250)}");
            }
            var scr = rest.Where(q => q.Category == QuestionCategory.Screening).ToList();
            if (scr.Count > 0)
            {
                sb.AppendLine("## Past screening questions (route to comp-process)");
                foreach (var q in scr) sb.AppendLine($"- {PrepReportSections.Trunc(q.Text, 250)}");
            }
            var other = rest.Where(q => q.Category is QuestionCategory.Technical or QuestionCategory.SystemDesign or QuestionCategory.HonestyBoundary).ToList();
            if (other.Count > 0)
            {
                sb.AppendLine("## Other past questions (route to tech-qa, or design-round for system-design questions when this is a design round)");
                foreach (var q in other) sb.AppendLine($"- [{q.Category}] {PrepReportSections.Trunc(q.Text, 250)}");
            }
        }
        if (d.Weaknesses.Count > 0)
        {
            sb.AppendLine("## Identified technical weaknesses");
            foreach (var w in d.Weaknesses.OrderByDescending(w => w.Severity).Take(15))
                sb.AppendLine($"- {w.Title} [{w.Category} · severity {w.Severity}]: {PrepReportSections.Trunc(w.Suggestion ?? w.Detail ?? "", 150)}");
        }
        return sb.ToString();
    }
}

// ============================ 结构归一化(规格 §4.4 + §6.5 + 评分微瑕 N-1 打捞) ============================

/// <summary>
/// 服务端结构归一化:AI 路落库前把模型输出修成规范结构(规格 §4.4 N-1…N-8)。
/// 执行顺序(钉死):打捞 → N-1 去重/排序 → N-2/N-3 缺节脚手架 → 打捞题追加 →
/// 设计卡补齐 → N-5 空块/空标题 → N-7 事实块置顶 → §6.5 四步 → 默认题 → 根字段。
/// </summary>
public static class PrepReportNormalizer
{
    private static JsonArray SectionsOf(JsonObject root) =>
        root["sections"] as JsonArray ?? new JsonArray();

    private static JsonObject? FindSection(JsonArray sections, string id)
    {
        foreach (var n in sections)
            if (n is JsonObject o && o["id"]?.GetValue<string>() == id) return o;
        return null;
    }

    private static JsonArray BlocksOf(JsonObject section)
    {
        if (section["blocks"] is not JsonArray a) { a = new JsonArray(); section["blocks"] = a; }
        return a;
    }

    private static JsonArray QuestionsOf(JsonObject section)
    {
        if (section["questions"] is not JsonArray a) { a = new JsonArray(); section["questions"] = a; }
        return a;
    }

    private static string? QText(JsonNode? n) =>
        n is JsonObject o ? o["question"]?.GetValue<string>() : null;

    private static bool IsDiscarded(string? id, RoundClassification cls, HashSet<string> seen)
    {
        if (id is null) return true;
        if (!PrepReportSections.CanonicalOrder.Contains(id)) return true;
        if (id == "coding-round" && !cls.IsCoding) return true;
        if (id == "design-round" && !cls.IsDesign) return true;
        if (!seen.Add(id)) return true; // 同 id 重复节,后者丢弃
        return false;
    }

    public static JsonObject Normalize(
        JsonObject modelRoot, PrepReportSourceData d, string lang,
        PrepReportLocalTexts t, RoundClassification cls)
    {
        var filled = new List<string>();
        var inputSections = SectionsOf(modelRoot);
        var kept = new List<JsonObject>();
        var seen = new HashSet<string>();
        // ---- 打捞(评分微瑕 N-1):丢弃之前,先把被丢弃节内的有据题按 §6.2 移入规范落点 ----
        var staged = new List<(PrepQuestionInput Input, JsonNode Node)>();
        foreach (var n in inputSections)
        {
            if (n is not JsonObject section) continue;
            var id = section["id"]?.GetValue<string>();
            if (!IsDiscarded(id, cls, seen))
            {
                kept.Add(section);
                continue;
            }
            if (d.QuestionBankEnabled)
            {
                foreach (var qn in QuestionsOf(section).ToList())
                {
                    var text = QText(qn);
                    var input = text is null ? null : PrepReportRouting.MatchInput(text, d.Questions);
                    if (input is null) continue; // 无据题随节丢弃
                    if (input.Category == QuestionCategory.SmallTalk && !input.GotStuck) continue; // §6.5 第 3 条
                    if (qn is not null) staged.Add((input, qn));
                }
            }
        }
        // ---- N-1 排序 + N-2/N-3 缺节脚手架(原数组原地重组,避免 JsonNode 二次挂接抛错) ----
        var keptById = kept.ToDictionary(s => s["id"]!.GetValue<string>());
        var routedEmpty = new RoutedPrepQuestions([], [], [], [], []);
        var ordered = new List<JsonObject>();
        foreach (var id in PrepReportSections.ExpectedSectionIds(cls))
        {
            if (keptById.TryGetValue(id, out var existing)) ordered.Add(existing);
            else
            {
                var routed = d.QuestionBankEnabled
                    ? PrepReportRouting.RouteQuestions(d.Questions, cls)
                    : routedEmpty;
                ordered.Add(PrepReportBuilders.BuildSection(d, t, cls, routed, id));
                filled.Add(id);
            }
        }
        inputSections.Clear();
        foreach (var s in ordered) inputSections.Add(s);
        var sections = inputSections;
        // ---- 打捞题追加(目标节此时必存在;若目标节已有同题,§6.5 去重会去掉后追加的副本) ----
        foreach (var (input, node) in staged)
        {
            var target = PrepReportRouting.CanonicalSection(input, cls);
            if (target is null) continue;
            var targetSection = FindSection(sections, target);
            if (targetSection is null) continue;
            var qs = QuestionsOf(targetSection);
            // 节点必须先脱离原数组(已随丢弃节整体丢弃,原父级不再被引用,故可直接挂接;
            // 但若同一节点在输入数组里被复用过,先做一次防御性脱离)。
            node.Parent?.AsArray().Remove(node);
            qs.Add(node);
        }
        // ---- 设计卡补齐(¬IsDesign 时 tech-qa 内必须有设计讨论卡) ----
        if (!cls.IsDesign)
        {
            var techQa = FindSection(sections, "tech-qa");
            if (techQa is not null)
            {
                var blocks = BlocksOf(techQa);
                var hasCard = blocks.OfType<JsonObject>().Any(b =>
                    b["title"]?.GetValue<string>() == t.DesignCardTitle);
                if (!hasCard) blocks.Insert(1, PrepReportBuilders.DesignCardBlock(t));
            }
        }
        // ---- N-5 空块/空标题 ----
        foreach (var section in sections.OfType<JsonObject>())
        {
            var id = section["id"]!.GetValue<string>();
            var changed = false;
            if (string.IsNullOrWhiteSpace(section["title"]?.GetValue<string>()))
            {
                section["title"] = PrepReportSections.TitleFor(id, t);
                changed = true;
            }
            if (BlocksOf(section).Count == 0)
            {
                var routed = d.QuestionBankEnabled
                    ? PrepReportRouting.RouteQuestions(d.Questions, cls)
                    : new RoutedPrepQuestions([], [], [], [], []);
                var scaffold = PrepReportBuilders.BuildSection(d, t, cls, routed, id);
                section["blocks"] = scaffold["blocks"]!.DeepClone();
                changed = true;
            }
            if (changed && !filled.Contains(id)) filled.Add(id);
        }
        // ---- N-7 事实块置顶:模型自写的事实块(同时含公司名与角色名的 points 块)去掉,服务端事实块放第一位 ----
        var roundBrief = FindSection(sections, "round-brief");
        if (roundBrief is not null)
        {
            var blocks = BlocksOf(roundBrief);
            foreach (var b in blocks.OfType<JsonObject>().ToList())
            {
                if (b["type"]?.GetValue<string>() != "points") continue;
                var items = b["items"] as JsonArray;
                if (items is null) continue;
                var joined = string.Join(" ", items.Select(i => i?.GetValue<string>() ?? ""));
                if (joined.Contains(d.CompanyName, StringComparison.Ordinal)
                    && joined.Contains(d.Role, StringComparison.Ordinal))
                    blocks.Remove(b);
            }
            blocks.Insert(0, PrepReportBuilders.FactBlock(d, t));
        }
        // ---- §6.5 四步判定 ----
        if (d.QuestionBankEnabled)
            ApplyRoutingValidation(sections, d, t, cls);
        else
        {
            foreach (var section in sections.OfType<JsonObject>())
                QuestionsOf(section).Clear();
        }
        // ---- tech-qa 默认题:校验后仍为空则补默认列表(§6.1 默认列表规则) ----
        var techQaFinal = FindSection(sections, "tech-qa");
        if (techQaFinal is not null && QuestionsOf(techQaFinal).Count == 0)
        {
            var routed = new RoutedPrepQuestions([], [], [], [], []);
            var scaffold = PrepReportBuilders.BuildSection(d, t, cls, routed, "tech-qa");
            QuestionsOf(techQaFinal).Clear();
            foreach (var q in (scaffold["questions"] as JsonArray)!)
                QuestionsOf(techQaFinal).Add(q!.DeepClone());
        }
        // ---- 根字段 ----
        modelRoot["sections"] = sections;
        modelRoot["structureVersion"] = 2;
        if (filled.Count > 0)
        {
            var fa = new JsonArray();
            foreach (var id in PrepReportSections.ExpectedSectionIds(cls).Where(filled.Contains))
                fa.Add(id);
            modelRoot["filledSections"] = fa;
        }
        else
        {
            modelRoot.Remove("filledSections");
        }
        return modelRoot;
    }

    private static void ApplyRoutingValidation(
        JsonArray sections, PrepReportSourceData d, PrepReportLocalTexts t, RoundClassification cls)
    {
        var weaknessTitles = d.Weaknesses.Select(w => w.Title).ToList();
        List<(JsonObject Section, JsonNode Node)> CopiesOf(string text)
        {
            var list = new List<(JsonObject, JsonNode)>();
            foreach (var s in sections.OfType<JsonObject>())
                foreach (var q in QuestionsOf(s).ToList())
                {
                    if (q is null) continue;
                    var qt = QText(q);
                    if (qt is not null && PrepReportRouting.SameText(qt, text))
                        list.Add((s, q));
                }
            return list;
        }
        void RemoveNode(JsonObject section, JsonNode node) => QuestionsOf(section).Remove(node);

        // 第 1 步:卡题只在 weakness-plan;缺失的卡题以本地形态补回。
        foreach (var input in d.Questions.Where(q => q.GotStuck).OrderBy(q => q.Sequence))
        {
            var copies = CopiesOf(input.Text);
            var plan = FindSection(sections, "weakness-plan")!;
            var inPlan = copies.Where(c => c.Section == plan).Select(c => c.Node).ToList();
            if (inPlan.Count > 0)
            {
                foreach (var (s, node) in copies)
                    if (node != inPlan[0]) RemoveNode(s, node);
            }
            else
            {
                foreach (var (s, node) in copies) RemoveNode(s, node);
                QuestionsOf(plan).Add(PrepReportBuilders.LocalPlanCardItem(input, t.PastTag));
            }
        }
        // 第 2 步:非卡题移入规范落点(保持扫描顺序);缺失的以本地形态补回。
        var moved = new List<(string Target, JsonNode Node, PrepQuestionInput Input)>();
        foreach (var s in sections.OfType<JsonObject>())
        {
            var sid = s["id"]!.GetValue<string>();
            foreach (var q in QuestionsOf(s).ToList())
            {
                var text = QText(q);
                var input = text is null ? null : PrepReportRouting.MatchInput(text, d.Questions);
                if (input is null || input.GotStuck) continue;
                var target = PrepReportRouting.CanonicalSection(input, cls);
                if (target is null) continue; // SmallTalk 无规范落点,第 3 步统一移除
                if (sid != target && q is not null)
                {
                    RemoveNode(s, q);
                    moved.Add((target, q, input));
                }
            }
        }
        foreach (var (target, node, _) in moved)
            QuestionsOf(FindSection(sections, target)!).Add(node);
        foreach (var input in d.Questions.Where(q => !q.GotStuck).OrderBy(q => q.Sequence))
        {
            if (input.Category == QuestionCategory.SmallTalk) continue; // 第 3 步统一移除
            var target = PrepReportRouting.CanonicalSection(input, cls);
            if (target is null) continue;
            if (CopiesOf(input.Text).All(c => c.Section != FindSection(sections, target)))
                QuestionsOf(FindSection(sections, target)!)
                    .Add(PrepReportBuilders.RoutedQuestionItem(input, t.PastTag, weaknessTitles));
        }
        // 第 3 步:非卡 SmallTalk 全报告移除。
        foreach (var s in sections.OfType<JsonObject>())
            foreach (var q in QuestionsOf(s).ToList())
            {
                if (q is null) continue;
                var text = QText(q);
                var input = text is null ? null : PrepReportRouting.MatchInput(text, d.Questions);
                if (input is not null && !input.GotStuck && input.Category == QuestionCategory.SmallTalk)
                    RemoveNode(s, q);
            }
        // 第 4 步:去重——规范节内保留首份;无据题保留规范顺序最早一份。
        foreach (var input in d.Questions.OrderBy(q => q.Sequence))
        {
            var target = PrepReportRouting.CanonicalSection(input, cls);
            if (target is null) continue; // 非卡 SmallTalk 已在第 3 步移除
            var copies = CopiesOf(input.Text);
            var keep = copies.FirstOrDefault(c => c.Section == FindSection(sections, target)).Node
                ?? copies.FirstOrDefault().Node;
            if (keep is null) continue;
            foreach (var (s, node) in copies)
                if (node != keep) RemoveNode(s, node);
        }
        var seenUngrounded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sections.OfType<JsonObject>())
            foreach (var q in QuestionsOf(s).ToList())
            {
                var text = QText(q);
                if (text is null || q is null) continue;
                if (PrepReportRouting.MatchInput(text, d.Questions) is not null) continue;
                var key = PrepReportRouting.NormalizeText(text);
                if (!seenUngrounded.Add(key)) RemoveNode(s, q);
            }
    }
}
