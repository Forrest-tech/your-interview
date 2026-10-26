
using System.Linq;
using YourInterview.Services.Jobs.Application;
using YourInterview.SharedContracts.Ai;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票 154：JD 小节自适应模型（动态小节 + 别名投影）。
/// 夹具说明（真实数据，零编造）：
/// - IntuitFixture：票 152 同款夹具的 JD 部分原文（2026-10-25 自原帖 curl；
///   原帖 https://jobs.intuit.com/job/toronto/senior-software-developer-full-stack/27595/102162337168，
///   2026-10-19 发布）。尾部博客列表省略 —— 它位于模块切除边界之后，不影响小节提取。
/// - PerpayFixture / InceedFixture：2026-10-25 自原帖 curl 原始 HTML，按 JobPageFetcher
///   提纯逻辑（取 main/article、块级标签换行、去标签、RemoveNavLines）机械提取的真实文本；
///   Perpay 一行开头的零宽字符已剔除（无语义、无断言触及该行）。
/// - KodiakExcerpt：同法提取的真实片段（仅保留标题判定相关的上下文行）。
/// - A-154-1 文本中的“薪资 null”已过期：票 152 实证纠正为原帖确有补偿段，
///   本票断言以 152 实证为准（"$132k - $178k"）。
/// </summary>
public sealed class Ticket154AdaptiveSectionsTests
{
    private const string IntuitFixture = """
Senior Software Developer, Full-Stack at Intuit
Skip to main content
Senior Software Developer, Full-Stack
Category Software Engineering Location Toronto, Canada Job ID 24685
Apply Now
Company Overview
Intuit is the global financial technology platform that powers prosperity for the people and communities we serve. With tens of millions of customers worldwide using products such as TurboTax, Credit Karma, QuickBooks, and Mailchimp, we believe that everyone should have the opportunity to prosper. We never stop working to find new, innovative ways to make that possible.
Job Overview
Come join the Intuit Toronto site as a Senior Software Developer. Be a leader in our Data, Growth and Experiences team that is building an AI powered robust Partner Platform that unifies sales motions across Mid-Market and Mass Market for Quick Books, MailChimp, Accountants, and PTG with AI powered automation and intelligence to deliver growth at scale.
The ideal candidate is an experienced full stack Software Developer who is proficient with JavaScript/TypeScript, Java, and familiar with AI powered development tools. This Senior Software Developer should be full-stack and prepared to work at all levels of the application. They will be directly responsible for the design, development, testing, maintenance, and documentation of high-volume customer experiences.
Intuit has embraced a hybrid way of working. This is a hybrid role with the expectation of working from our Toronto office 3 days per week.
Responsibilities
Development of customer-facing applications from design through implementation
Act in a technical leadership capacity: Mentoring junior engineers, new team members, and applying technical expertise to challenging programming and design problems
Roughly 70-85% hands-on coding
Resolve defects/bugs during QA testing, pre-production, production, and post-release patches
Work cross-functionally with various Intuit teams: product management, QA/QE, various product lines, or business units to drive forward results
Contribute to the design and architecture of the project
Experience with Agile Development, SCRUM, or Extreme Programming methodologies and successful delivery of front-end applications using both Java and JavaScript/React
Qualifications
Experience developing web applications using Java and JavaScript
Strong knowledge of Java and Spring Boot
Hands of experience with React and JavaScript
BS/MS in computer science or relevant field or equivalent work experience.
Experience with the entire Software Development Life Cycle (SDLC).
Demonstrated ability to explain complex technical issues to both technical and non-technical audiences.
Strong understanding of the Software design/architecture process.
Experience with unit testing & Test-Driven Development (TDD).
Passion for growing and applying technical skills in service to customers
Experience with Agile Development, SCRUM, and/or Extreme Programming methodologies
May be on-call for urgent issues
Preferred Qualifications:
Experience developing, maintaining, and scaling large, consumer-facing web applications
Deep understanding of the challenges involved in building highly scalable, highly available systems
A continuous learner who stays current with open-source tools and emerging technology — including AI — through hands-on prototyping and research
Hands-on experience with a range of modern web frameworks
Intuit provides a competitive compensation package with a strong pay for performance rewards approach. This position may be eligible for a cash bonus, equity rewards and benefits, in accordance with our applicable plans and programs (see more about our compensation and benefits at Intuit®: Careers | Benefits). Pay offered is based on factors such as job-related knowledge, skills, experience, and work location. To drive ongoing fair pay for employees, Intuit conducts regular comparisons across categories of ethnicity and gender.
The expected base pay range for this position is:
Toronto $132,000 - $178,500
Apply Now
Career Path
Mobility plays an important role for our engineering community. In addition to vertical growth, our teams offer lateral growth opportunities. Whether it’s working on a different tech stack or product, Intuit will help you get to the next step in your career.
Software Engineer 1 & 2
Sr. Software Engineer
Staff Software Engineer
Sr. Staff Software Engineer
Principal Software Engineer
Distinguished Software Engineer
Intuit supports you, not just as you are now, but as the person that you want to grow to be.
Alex Pollock Senior Software Engineer
Related Content
Video
Global Engineering Days 2023
GED is a weeklong code-a-thon that brings together Intuit technologists from all over the world to innovate and collaborate on projects they’re passionate about.
Blog
""";

    // ================= A-154-1：Intuit 整帖 =================

    [Fact]
    public void Intuit_Sections_TitlesInOriginalOrder()
    {
        var d = JdPasteParser.Parse(IntuitFixture, PasteKind.Text);
        var titles = d.Sections!.Select(s => s.Title).ToArray();
        // 定稿 A-154-1 列三节；实测按 R-154-2 冒号规则另有 "Preferred Qualifications:"
        // 一节（原帖真实子标题，冒号结尾）—— 四节齐全，顺序与原文一致。
        // 票 204：KnownSectionHeaders 增加 "company overview"，原帖 Company Overview
        // 一节首次成为小节，排在首位（五节齐全，顺序与原文一致）。
        Assert.Equal(
            new[] { "Company Overview", "Job Overview", "Responsibilities", "Qualifications", "Preferred Qualifications" },
            titles);
        Assert.All(d.Sections, s => Assert.Equal("auto", s.Origin));
        Assert.All(d.Sections, s => Assert.False(s.Edited));
    }

    [Fact]
    public void Intuit_BenefitsNull_RequirementsIsQualificationsBody()
    {
        var d = JdPasteParser.Parse(IntuitFixture, PasteKind.Text);
        // 原帖无福利小节：benefits 为 null，详情页不再凭空出现 Additional Information。
        Assert.Null(d.Benefits);
        // Qualifications 不再以 "Requirements" 之名出现：列表里是原标题，投影取首个族内节。
        Assert.DoesNotContain(d.Sections!, s => s.Title == "Requirements");
        Assert.NotNull(d.Requirements);
        Assert.StartsWith("Experience developing web applications using Java and JavaScript", d.Requirements);
        Assert.Contains("May be on-call for urgent issues", d.Requirements);
    }

    [Fact]
    public void Intuit_NoBlogResidue_AndSummaryStillExcerpt_AndSalaryReal()
    {
        var d = JdPasteParser.Parse(IntuitFixture, PasteKind.Text);
        Assert.DoesNotContain(d.Sections!, s => s.Body.Contains("Global Engineering Days"));
        Assert.DoesNotContain(d.Sections!, s => s.Body.Contains("Data Mesh"));
        // 摘要仍为 152 原文摘录（非中文改写），Job Overview 全文以小节呈现、不因摘要截断。
        Assert.NotNull(d.JdSummary);
        Assert.StartsWith("Come join the Intuit Toronto site", d.JdSummary);
        Assert.True(d.JdSummary.Length <= 500);
        // 票 204：Sections[0] 已是 Company Overview 一节（见上），Job Overview 全文
        // 改按标题寻址断言，意图不变（Job Overview 全文以小节呈现、不因摘要截断）。
        Assert.Contains("Come join the Intuit Toronto site", d.Sections!.Single(s => s.Title == "Job Overview").Body);
        // 薪资：定稿 "薪资 null" 已被 152 实证推翻 —— 原帖确有补偿段，规则解出真实区间。
        Assert.Equal("$132k - $178k", d.Salary);
    }

    [Fact]
    public void Intuit_HandsOf_IsSourceTypo_NotParseLoss()
    {
        // A-154-7 定性：2026-10-25 实抓原帖 HTML 字节证实，源 HTML 的 Qualifications
        // <li> 本身即为 "Hands of experience"（原帖笔误；同期 Preferred Qualifications
        // 一节的 "Hands-on experience" 拼写正确）。解析链逐字搬运，不修、不猜。
        var d = JdPasteParser.Parse(IntuitFixture, PasteKind.Text);
        var q = d.Sections!.Single(s => s.Title == "Qualifications");
        Assert.Contains("Hands of experience with React and JavaScript", q.Body);
        var pref = d.Sections.Single(s => s.Title == "Preferred Qualifications");
        Assert.Contains("Hands-on experience with a range of modern web frameworks", pref.Body);
    }

    // ================= A-154-2：Greenhouse Perpay =================

    private const string PerpayFixture = """
Software Engineer, New Grad
Philadelphia, Pennsylvania, United States
About Us:
Perpay is a certified B Corp and Philadelphia’s most impactful growth-stage startup. We are driven by a mission to significantly improve the financial stability of everyday Americans. For the past decade, we have established strong product-market fit and a profitable, efficient operating model across a suite of products, positioning Perpay as the premier financial partner for consumers with subprime credit.
With over 500,000 customers who have utilized more than $1 billion in spending power, we are at a pivotal moment. We are scaling our operations, building new offerings, and deepening our impact. We are looking for teammates eager to join us on this journey.
Our venture partners include First Round Capital and L Catterton.
Products we’ve built to make an impact:
Perpay Marketplace: Combines interest-free payments and modern e-commerce to reduce cost of ownership and promote healthy repayment behavior.
Perpay+: Leverages Marketplace repayment history to help members monitor and build credit with all 3 credit bureaus.
Perpay Credit Card: Expands access to the flexibility and benefits of a World Mastercard by removing common barriers like high security deposits and low approval odds.
Our team thrives on in-person collaboration, operating from our unique center-city Philadelphia office. This comfortable "home away from home" space offers river views and fosters rapid product development, strong relationships, and career growth. The energy from achieving big wins is palpable here. While we primarily work in the office, we offer sensible flexibility for personal needs, such as sick children or urgent errands, and coordinate official remote weeks around major holidays. If you are passionate about a meaningful mission, collaboration, equity, and generous perks, Perpay is the best place to be in Philadelphia right now.
About the Role:
We are looking for motivated graduates (or equivalent experience) to join our Software Engineering team in 2027! You’ll contribute to our codebase, test & release features into production, and collaborate with other teams like product, design, data science, analytics, and operations. The ideal candidate has software development internship experience and is enthusiastic about learning and growing! We're particularly interested in working with candidates excited about joining a mission-driven startup. On our team, you’ll sharpen your skills, learn new tools alongside other new graduates, and learn from experienced engineers. Plus, our team organizes events and social gatherings outside of work hours. We believe having a fun and engaging work environment is essential to fostering a positive company culture and creating a strong foundation for career growth.
Much of our team is fullstack, so we look for team members excited to learn new technologies and work across all stacks, even if it’s not something you’ve done before! Our tech stack: Javascript, React, React Native, Redux, Python, Django, Kubernetes, AWS, Docker, Terraform, and more.
Our greatest strength is our people, and we’d love for you to be one of them!
Responsibilities:
Develop, test, and release features for our customer-facing and internal platforms alongside the engineering team
Write clean, efficient, and maintainable code
Collaborate cross-functionally to develop efficient and user-friendly solutions to support the complex needs of our business
Actively participate in design reviews, code reviews, and discussions to improve our software architecture and implement efficient processes within our engineering team
What You’ll Bring:
Currently pursuing a Bachelor’s degree or higher or a recent graduate studying Computer Science, Mathematics, or a related technical field (or equivalent experience)
1 to 2 previous Software Engineering internships with a focus on fullstack software development
Experience with at least one backend application framework such as Django or Flask
Experience with frontend technologies such as React, Angular, or Vue.js
Knowledge of RESTful API design and integration
Experience with Python or another dynamically typed language
A strong focus on writing and deploying clear, readable, testable code
Ability to pick up new technologies quickly, with software engineering fundamentals being valued more than familiarity with specific tools or languages
Eagerness to learn, grow, and seek feedback from team members
Strong communication skills
Even if you don't check all the boxes, please apply if interested. You might be exactly what we're looking for!
What We’ll Bring to Full-time Team Members:
Meaningful compensation and equity
Premium medical benefits (fully paid base plan, and very reasonable premium for the buy-up option)
5% employer 401k match
Unlimited PTO policy
Remote weeks around major holidays + extra holiday on weekends like MDW and LDW
High quality catered lunch by local chefs, 4 days / week
Gym subsidy
Paid cell phone + plan
Student loan repayment match program
Family savings match program
Relocation assistance
Generous team member discounts
This is not a remote opportunity; it is 100% onsite (#LI-Onsite) (#LI-TH1) (#LI-AK1)
Perpay is proud to be an equal opportunity employer. We value diversity in all its forms and are committed to creating an inclusive environment. We do not discriminate on the basis of race, religion, color, national origin, gender identity, sexual orientation, sex (including pregnancy), marital status, political affiliation, age, veteran status, disability status or other non-merit factor. Please contact us at careers@perpay.com to request accommodation.
Apply for this job
*
indicates a required field
""";

    [Fact]
    public void Perpay_Sections_AndProjections()
    {
        // 原帖 https://job-boards.greenhouse.io/perpay/jobs/4034578007（2026-10-25 抓取）。
        // 定稿 A-154-2 列三节（此前全空的三段）；实测另有 About Us / About the Role /
        // Products 子标题三节（皆冒号结尾，R-154-2 规则下同样成节）—— 六节齐全。
        var d = JdPasteParser.Parse(PerpayFixture, PasteKind.Text);
        Assert.Equal(
            new[]
            {
                "About Us", "Products we’ve built to make an impact", "About the Role",
                "Responsibilities", "What You’ll Bring", "What We’ll Bring to Full-time Team Members"
            },
            d.Sections!.Select(s => s.Title).ToArray());
        // 投影：requirements = 首个 requirements 族节（What You'll Bring）；
        // benefits = What We'll Bring 节（福利族别名，弯引号归一化后匹配）。
        Assert.NotNull(d.Requirements);
        Assert.StartsWith("Currently pursuing a Bachelor’s degree", d.Requirements);
        Assert.NotNull(d.Benefits);
        Assert.StartsWith("Meaningful compensation and equity", d.Benefits);
    }

    // ================= A-154-5：反误判 =================

    private const string KodiakExcerpt = """
Develop software for sensors. Evaluation and production (Camera, Radar, Lidar, IMU)
Develop software for network devices, time synchronization experience a plus
What you’ll bring:
2+ years of software development experience, ideally within the automotive, aerospace or a related industry
Programming experience in C++
Knowledge of embedded programming platforms
Understanding of networking fundamentals
Experience with implementation and on-vehicle debugging of low level hardware buses such as I2C, CAN, SPI, and UART
Experience with high speed hardware communication such as PCIe and Automotive Ethernet
Hands on. You are just as comfortable getting your hands dirty analyzing a subsystem in the shop as when using a keyboard.
Great communicator. You have experience writing clear, concise, and detailed documentation
Passion. You are passionate about autonomous vehicles and their potential impact on humanity
BS or MS, or equivalent experience in Computer Science, Electrical Engineering, or Computer Engineering
What we offer:
Competitive compensation package including equity and biannual bonuses
Excellent Medical, Dental, and Vision plans through Kaiser Permanente, Anthem, and Guardian (including a medical plan with infertility benefits)
Flexible PTO and generous parental leave policies
Our office is centrally located in Mountain View, CA
Office perks: dog-friendly, free catered lunch, a fully stocked kitchen, and free EV charging
""";

    [Fact]
    public void Kodiak_BoldBulletPhrases_NeverOpenSections()
    {
        // 原帖 https://job-boards.greenhouse.io/kodiak/jobs/4005123009（2026-10-25 抓取）。
        // 抓取路径下 bullet 内加粗短语剥标签后成 "Hands on. You are …" 行：
        // 句号后有文字 + 超 8 词，两条护栏同时挡住，不得开节；三行皆为上一节正文。
        var d = JdPasteParser.Parse(KodiakExcerpt, PasteKind.Text);
        Assert.Equal(
            new[] { "What you’ll bring", "What we offer" },
            d.Sections!.Select(s => s.Title).ToArray());
        var bring = d.Sections[0];
        Assert.Contains("Hands on. You are just as comfortable", bring.Body);
        Assert.Contains("Great communicator. You have experience", bring.Body);
        Assert.Contains("Passion. You are passionate", bring.Body);
    }

    [Fact]
    public void Kodiak_PasteForm_BulletBoldPhrase_NeverOpensSection()
    {
        // 定稿 A-154-5 引用的粘贴形态："- **Hands on.** You are …"。
        // bullet 前缀 + 同行句意正文，两条护栏同时挡住。
        const string text = """
            What you’ll bring:
            - **Hands on.** You are just as comfortable getting your hands dirty analyzing a subsystem in the shop as when using a keyboard.
            - **Great communicator.** You have experience writing clear documentation.
            What we offer:
            Competitive compensation package.
            """;
        var d = JdPasteParser.Parse(text, PasteKind.Text);
        Assert.Equal(
            new[] { "What you’ll bring", "What we offer" },
            d.Sections!.Select(s => s.Title).ToArray());
    }

    [Fact]
    public void IsolatedBlogLine_NeverOpensSection_AndMetadataLinesIgnored()
    {
        // 孤立 Blog 行不开节（无 5 行内二次确认，不触发模块切除，也不自成小节）；
        // POSTED: / SALARY: 等元数据行不开节（内联仅认已知表头部）。
        const string text = """
            Senior Developer
            Acme Corp
            POSTED: October 22, 2026
            SALARY: $150,000 - $160,000
            About the Role:
            We build great software for our customers every day.
            Blog
            We write about engineering culture on our blog sometimes.
            Qualifications:
            Experience building software with Java and Spring Boot.
            """;
        var d = JdPasteParser.Parse(text, PasteKind.Text);
        Assert.Equal(
            new[] { "About the Role", "Qualifications" },
            d.Sections!.Select(s => s.Title).ToArray());
        Assert.DoesNotContain(d.Sections, s => s.Title == "Blog");
        Assert.Contains("We write about engineering culture", d.Sections[0].Body);
    }

    // ================= A-154-3：Inceed =================

    private const string InceedFixture = """
Software Engineer- Platform Houston Texas
Your Privacy Choices
Trust matters. That's why we keep our use of cookies and tracking technologies
limited to what helps our website perform its best and improve your experience.
We never sell your personal information, and any optional cookies are only
enabled with your consent.
To learn more about how we think, please visit our
Privacy Policy
Skip to content
Software Engineer- Platform
apply now
Back To Search Results
LOCATION: Houston, Texas
FLEXIBILITY: Remote
SALARY: $150,000 - $160,000
SALARY: $150,000 - $160,000
POSTED: October 22, 2026
JOB TYPE: Direct Hire
JOB ID: 33954
I want more jobs Like this in My inbox.
Job details
To Apply for this Job Click Here
Position: Software Engineer- Platform
Compensation: $150,000 – $160,000 annually, depending on experience
Location: Full Remote
Inceed has partnered with a great company to help find a skilled Software Engineer- Platform to join their team!
This is a strong opportunity for a platform-focused engineer who enjoys building reliable internal tooling that helps development teams move faster. In this role, you’ll help shape the systems behind source control, CI/CD, infrastructure automation, secrets management, observability, and incident response. The team is looking for someone who brings deep technical skill, a collaborative mindset, and a developer-first approach to platform engineering.
Key Responsibilities & Duties:
Own and improve internal developer platform tooling
Build and maintain CI/CD workflows in GitHub Actions
Develop reusable Terraform modules and templates
Support cloud infrastructure across AWS services
Improve containerization and deployment workflows
Partner with teams on observability and alerting
Enhance secrets and configuration management practices
Support incident response tooling and operational readiness
Drive engineering standards and best practices
Mentor teammates through thoughtful technical collaboration
Required Qualifications & Experience:
5+ years of professional software engineering experience
Strong background in platform, infrastructure, or systems engineering
Hands-on AWS experience
Experience with load balancing
Experience with Lambda and API Gateway
Experience with S3 and EC2
Strong GitHub Actions experience
Strong Terraform experience
Experience with containerization tools
Ability to code like a software engineer
Nice to Have Skills & Experience:
Experience with Grafana or similar observability tools
Experience with Datadog or similar monitoring platforms
Experience with LaunchDarkly or feature flag tools
Experience with incident response platforms
Experience with secrets management tools
Experience with dev container workflows
Experience supporting internal AI tools or marketplaces
Experience in a highly collaborative engineering environment
Perks & Benefits:
This opportunity includes a comprehensive and competitive benefits package—details will be shared during later stages of the hiring process.
Other Information:
100% remote position
Direct hire opportunity
Interview process includes two rounds
First interview with the hiring manager and technical lead
Second interview with executive leadership
Background and drug screening required
Client cannot provide sponsorship at this time
Target start date is ASAP
Compensation is around $160,000 annually
If you are interested in learning more about the Software Engineer- Platform opportunity, please submit your resume for consideration. Our client unable to provide sponsorship at this time.
We are Inceed, a staffing direct placement firm who believes in the possibility of something better. Our mission is simple: We’re here to help every person, whether client, candidate, or employee, find and secure what’s better for them.
Inceed is an equal opportunity employer. Inceed prohibits discrimination and harassment of any type and affords equal employment opportunities to employees and applicants without regard to race, color, religion, sex, sexual orientation, gender identity, or expression, pregnancy, age, national origin, disability status, genetic information, protected veteran status, or any other characteristic protected by law.
#LI-Remote
#IND
#LI-WS1
To Apply for this Job Click Here
I want more jobs Like this in My inbox.
Share This Job
Share This Job
Apply Now
" * " indicates required fields
About Us
Inceed is a staffing and recruiting team full of people-loving people. Specializing in the IT and Finance & Accounting fields, we thrill at the opportunity to help you discover the next step in your career or, as we call it, your “something better”. And with the broad reach of our multiple branches, we’re able to do so nationwide.
It feels weird tooting our own horn, but we are super proud to be:
(same list awards and recognitions)
Once we synch up with you to discuss a job posting, we value connection above all else. After all, how can we know if it’s a culture fit, a lifestyle fit, a passion fit, if we don’t truly know the person behind the resume?
""";

    [Fact]
    public void Inceed_Sections_AndProjections()
    {
        // 原帖 https://jobs.inceed.com/job/33954-software-engineer-platform-houston-texas/
        // （2026-10-25 抓取）。定稿 A-154-3 列 JD 五节；实测另有页脚 "About Us" 一节
        // （已知表裸行，R-154-2 规则 1 下成节 —— 该站整页无 main/article，抓取回落整页，
        // 页脚公司介绍块随正文流入；五节本身齐全、顺序与投影皆如定稿）。
        var d = JdPasteParser.Parse(InceedFixture, PasteKind.Text);
        Assert.Equal(
            new[]
            {
                "Key Responsibilities & Duties", "Required Qualifications & Experience",
                "Nice to Have Skills & Experience", "Perks & Benefits", "Other Information", "About Us"
            },
            d.Sections!.Select(s => s.Title).ToArray());
        // 投影：requirements = 文档顺序首个族内节（Required…，& 后缀剥离后匹配）；
        // benefits = Perks & Benefits；Other Information 不投影、不丢失。
        Assert.NotNull(d.Requirements);
        Assert.StartsWith("5+ years of professional software engineering experience", d.Requirements);
        Assert.NotNull(d.Benefits);
        Assert.StartsWith("This opportunity includes a comprehensive", d.Benefits);
        var other = d.Sections.Single(s => s.Title == "Other Information");
        Assert.Contains("100% remote position", other.Body);
        Assert.Contains("Interview process includes two rounds", other.Body);
    }

    // ================= 投影保真（与今日 ExtractSection 语义逐字一致） =================

    [Fact]
    public void Projection_FirstFamilyWins_ShortBodyNull_AndAmpersandMatchKeepsTitle()
    {
        // 文档顺序首个族内节胜出（Requirements 在 Qualifications 之后出现也取 Qualifications）；
        // 首个族内节正文 <10 字时返回 null（旧逻辑首命中不足即 null，不顺延）；
        // & 后缀只用于匹配，原标题一字不动。
        var sections = new[]
        {
            new JdSection("Qualifications", "qualifications", "short", "auto", false),
            new JdSection("Requirements", "requirements", "Build software with Java and Spring Boot.", "auto", false),
        };
        Assert.Null(JdSectionProjection.ProjectRequirements(sections));

        var sections2 = new[]
        {
            new JdSection("Qualifications", "qualifications",
                "Experience building software with Java and Spring Boot.", "auto", false),
            new JdSection("Requirements", "requirements",
                "Build software with Java and Spring Boot.", "auto", false),
        };
        Assert.StartsWith("Experience building software", JdSectionProjection.ProjectRequirements(sections2));

        var sections3 = new[]
        {
            new JdSection("Perks & Benefits", "perks & benefits",
                "We offer health insurance and paid leave for everyone.", "auto", false),
        };
        Assert.StartsWith("We offer health insurance", JdSectionProjection.ProjectBenefits(sections3));
        Assert.Equal("perks & benefits", sections3[0].Key);
    }

    [Fact]
    public void Projection_TruncatesAt8000_WithEllipsis()
    {
        var body = new string('x', 8001);
        var sections = new[] { new JdSection("Requirements", "requirements", body, "auto", false) };
        var projected = JdSectionProjection.ProjectRequirements(sections);
        Assert.NotNull(projected);
        Assert.Equal(8001, projected.Length);
        Assert.EndsWith("…", projected);
    }

    // ================= R-154-8：AI 物化 =================

    private sealed class FakeAi(string json) : IAiGatewayClient
    {
        public Task<AiCompletionResult> CompleteAsync(string purpose, string prompt, string? systemPrompt,
            double? temperature, int? maxTokens, CancellationToken ct)
            => Task.FromResult(new AiCompletionResult(json, "fake", null, null, null));

        public Task<PrepSourceSettingsDto> GetPrepSourceSettingsAsync(CancellationToken ct)
            => throw new NotImplementedException();

        public Task<PrepSourceSearchDto> SearchPrepSourcesAsync(string source, string company,
            string? role, string? stage, CancellationToken ct)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task Enrich_MaterializesAiBenefits_AsSection_AndDiscardsRuleDuplicate()
    {
        // 规则已产出 Qualifications 节（requirements 有值），AI 同字段值直接丢弃、不追加重复节；
        // 规则无 benefits 节，AI 补出的 benefits 值（子串校验通过）物化为 "Benefits" 节。
        const string text = """
            Senior Developer
            Acme Corp
            Qualifications:
            Build software with Java.
            We offer health insurance.
            """;
        var draft = JdPasteParser.Parse(text, PasteKind.Text);
        Assert.NotNull(draft.Requirements);
        Assert.Null(draft.Benefits);

        const string aiJson = """
            {
              "requirements": "Build software with Java.",
              "benefits": "We offer health insurance."
            }
            """;
        var merged = await JdAiEnricher.EnrichAsync(new FakeAi(aiJson), text, draft, CancellationToken.None);
        Assert.NotNull(merged);
        Assert.Equal(draft.Requirements, merged.Requirements);
        Assert.Equal("We offer health insurance.", merged.Benefits);
        Assert.Equal(
            new[] { "Qualifications", "Benefits" },
            merged.Sections!.Select(s => s.Title).ToArray());
        Assert.Equal("We offer health insurance.", merged.Sections[1].Body);
    }

    // ================= JSON 契约 =================

    [Fact]
    public void SectionJson_RoundTrip_Canonicalize_AndInvalid()
    {
        var sections = new[]
        {
            new JdSection("Qualifications", "qualifications", "Build software.", "auto", false),
            new JdSection("Benefits", "benefits", "Health insurance.", "auto", true),
        };
        var json = JdSectionJson.Serialize(sections);
        var parsed = JdSectionJson.Parse(json);
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("Benefits", parsed[1].Title);
        Assert.True(parsed[1].Edited);

        // 非法 JSON：IsValid false；Parse 宽容返回 null（读路径不 500）。
        Assert.False(JdSectionJson.IsValid("{not json"));
        Assert.Null(JdSectionJson.Parse("{not json"));
        Assert.True(JdSectionJson.IsValid(null));
        Assert.Null(JdSectionJson.Parse(null));

        // Canonicalize：空节丢弃、key 由最终标题重算（改标题后 key 不过期）。
        var dirty = """[{"title":"  Requirements  ","key":"stale","body":"Build software.","origin":"auto","edited":false},{"title":"","body":"","origin":"auto","edited":false},{"title":"Benefits","key":"benefits","body":"Health.","origin":"weird","edited":false}]""";
        var canonical = JdSectionJson.Canonicalize(dirty);
        Assert.NotNull(canonical);
        var kept = JdSectionJson.Parse(canonical)!;
        Assert.Equal(2, kept.Count);
        Assert.Equal("Requirements", kept[0].Title);
        Assert.Equal("requirements", kept[0].Key);
        Assert.Equal("auto", kept[1].Origin);
    }

    // ================= R-154-9：抓取路径块级换行 =================

    [Fact]
    public void ExtractText_BlockTags_BecomeNewlines()
    {
        // 154 之前：全部标签替换为空格，标题与正文糊成一行，小节扫描认不出标题。
        // 154 之后：块级标签换行，标题独立成行。
        const string html = "<html><body><h2>Qualifications</h2><p>Build stuff</p>" +
            "<ul><li>Java</li><li>React</li></ul><h2>Benefits</h2><p>Great perks</p></body></html>";
        var text = YourInterview.Services.Jobs.Infrastructure.Services.JobPageFetcher.ExtractText(html);
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        Assert.Equal(
            new[] { "Qualifications", "Build stuff", "Java", "React", "Benefits", "Great perks" },
            lines);
    }
}
