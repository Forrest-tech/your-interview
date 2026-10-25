
using System.Linq;
using System.Text.RegularExpressions;
using YourInterview.Services.Jobs.Application;
using YourInterview.SharedContracts.Ai;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票 152：Smart paste 博客污染切除 + 解析提示词原样化。
///
/// 夹具 IntuitFixture：2026-10-25 自原帖 URL curl 原始 HTML，按 JobPageFetcher 提纯前段逻辑
/// （取 &lt;main&gt;、去 script/style/nav 等标签、去标签成行）提取的真实文本，未手写、未改写。
/// 原帖：https://jobs.intuit.com/job/toronto/senior-software-developer-full-stack/27595/102162337168
/// （2026-10-19 发布；抓取日期 2026-10-25）。
/// </summary>
public sealed class Ticket152BlogPollutionTests
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
The Data Mesh Strategy Behind Intuit’s Global Financial Technology Platform
Intuit has built foundational capabilities for collecting, processing and transforming raw data into a connected mesh of high quality data. Those capabilities are enabling our technologists to build personalized experiences, with speed and at scale, to deliver on our mission to power prosperity around the world
Article
Our approach to hybrid work
Intuit embraces a hybrid way of working that brings the best of in-person collaboration and connections together with the flexibility of virtual work.
Blog
How Intuit data analysts write SQL 2x faster with internal GenAI tool
For Intuit, turning data into actionable insights is pivotal to our success in delivering awesome experiences to 100 million customers That’s why we’ve developed an internal generative AI powered tool called Query Kickstart to improve speed to insight by accelerating SQL query authoring for our data workers.
Blog
How to Accelerate Development Velocity in the GenAI Era? Build a GenOS.
Intuit is using generative AI to power our internal developer platforms to enable our software engineers and data workers to be more effective and efficient.
Blog
Starting a Successful Tax Career
4 Ways Intuit helped me start a successful tax career
Blog
Improving Tax Skills
I was ready to up my tax game, that's why I joined Intuit
Blog
Career Path
Accelerate Your Career Path Inside Intuit
Video
Offices of the future
We're building the workforce and workplace of the future by investing in our sites around the world. This video shows how our office designs and features spark innovation for our global teams.
Blog
Innovative thinking at Intuit India
Building a true platform culture to solve customer problems
Blog
Using AI Together
11 Things we've learned about using AI together
Blog
AI skills to boost your tech career
AI skills to boost your tech career
Blog
Intuit Survey
US high school students want financial education at school
Blog
Velocity Hacks
Velocity Hacks to work harder not smarter
Blog
Tips for Tax & Bookkeeping Pros
How Intuit helps tax and bookkeeping pros do their best work
Blog
Tax Experiences
The Role of AI in Delivering Personalized Tax Experiences
Blog
Career Tips
Navigating your career path, tips for engineers
Blog
Hybrid Work
How Intuit's hybrid work model is evolving
Blog
We innovate—and coding is a by-product of that
From intern to director; coding is a key aspect of Intuit.
Blog
Future of Fintech
How Intuit is shaping the future of fintech
Blog
Intuit's new tools to improve business finances
Intuit's new tools to improve business finances
Blog
Global Engineering Days
Roughly 7,500 participants worked together at Intuit’s biannual week-long event to turn ideas into innovative solutions for approximately 100 million consumer and business customers.
Blog
Thomas Ranese as CMO
Intuit Appoints Thomas Ranese as CMO
Blog
Meet James Harris
From an Atlanta Barbershop to Intuit’s Strategic Accounts
Blog
From Super Nintendo to Silicon Valley
Petagae Butcher on paving a career in tech
Blog
Intuit Israel ranked in best tech companies for parents
Israel's top 10 best high tech companies for parents
Blog
Indigenous ties inspire new Toronto workplace
Toronto's inclusive workplace design inspired by indigenous ties
Article
AI-driven tech that unlocks your career growth
At Intuit, you can build AI-powered solutions while shaping a career path that creates unparalleled impact
Article
Benefits
We support our employees by offering benefits you need to stay healthy, achieve financial security, and enjoy peace of mind for you and your family.
Blog
Intuit introduces Intuit Enterprise Suite to help businesses grow, streamline operations, and scale
We're further expanding mid-market offerings with an all-in-one solution that serves larger and more complex businesses and fuels their growth.
Article
3 Ways to Drive Continuous AI Transformation
To drive continuous AI innovation, businesses should consider three key strategies: rigorous technical reviews, cross-functional AI integration, and structured conflict resolution.
Article
Intuit is focused on letting the builders build
Intuit is focused on fostering innovation by encouraging an entrepreneurial mindset among its technologists, allowing them to build and make mistakes, and highlighting the increasing expectations for new graduates entering the tech industry.
Article
In Conversation With Intuit Chief Data Officer Ashok Srivastava
Intuit's Chief Data Officer, Ashok Srivastava, discusses the company's strategic use of AI and data science to improve financial products, emphasizing employee education and ethical AI practices.
Article
Revolutionizing Knowledge Discovery with GenAI to Transform Document Management
Revolutionizing Knowledge Discovery with GenAI to Transform Document Management
Blog
A tale of innovation and modernization
Building extensible capabilities to elevate developer experience and seamlessly adapt to customers' changing needs have been critical transformations in Intuit's platform journey.
Blog
Innovate at speed — by keeping privacy paramount
ntuit uses AI to empower small business success with personalized data insights and tools that drive growth, emphasizing data privacy and responsible AI to build trust.
Blog
Leader experience: How to ace your software engineer interview at Intuit
Uttam Ramamurthy, principal software engineer and one of the first builder catalyst hires at Intuit India, speaks about his interview experience including various assessments—that gives candidates a competitive edge.
Video
Intuit Academy
Start your career as a remote tax preparer or bookkeeper through our Intuit Academy program. The program consists of self-paced online courses, followed by an exam. Upon passing the exam, you will receive a badge that you can easily display to advance your career in the industry.
Blog
Learn how our tax experts are helping Americans navigate a complex tax code
The tax code is complicated. The good news is that Intuit tax experts help people confidently tackle tax time.
Blog
AI-powered experiences take ‌center stage at Intuit’s Global Engineering Days
Intuit's, GED Spring 2025, week-long code-a-thon expanded on AI-related projects to push the boundaries of what can be done for customers.
Blog
AI in 2025 – Intuit Tech Leaders See Beyond the Hype
Excitement and skepticism will mark the year ahead, as AI-powered experiences proliferate and customers ultimately decide what’s best for them.
Blog
10 Skills You Need to Succeed as a Data Scientist
Considering a career as a data scientist? Explore essential data science skills to enhance your career prospects with Intuit as your guide.
Blog
What’s the Difference Between Front-End, Back-End, and Full-Stack Developers?
Thinking of becoming a front-end, back-end, or full-stack developer? Learn how the roles and their responsibilities compare with Intuit as your guide.
Blog
10 Essential Software Engineer Skills to Succeed in Today’s Tech Industry
This guide covers 10 in-demand software engineer skills, technical and otherwise. We’ll also go into how to build those skills and how to prove yourself to prospective employers.
Blog
How to Write Software Engineer Cover Letters That Stand Out (With Examples)
Applying to software engineering jobs? Learn how to write a cover letter for software engineers that stands out with helpful tips from Intuit.
Blog
How to Use AI for Data Analysis: Workflows, Use Cases, and Best Practices
AI is changing the way that data analysis is done. Explore AI's role in data analysis and enhance your decision-making process with Intuit.
Blog
How AI Is Transforming Financial Technology (Fintech)
AI is transforming fintech beyond traditional automation with intelligent systems for personalized experiences, real-time fraud detection, and efficiency.
Blog
Get to know our data engineer and software engineers at Intuit
At Intuit, our software engineers and data engineers both uniquely apply their skillset to help power our worldwide mission of powering prosperity around the world.
Blog
Beyond the Code: Your Career Path as a Software Engineer or Developer at Intuit
Discover the dynamic software engineering roles at Intuit, where you'll work on cutting-edge fintech solutions, harness the power of AI and machine learning, and contribute to the company's mission of powering financial prosperity for small and medium-sized businesses.
Blog
How to Learn AI: A Beginner's Guide to Artificial Intelligence at Intuit
Explore how Intuit is leading the way in AI-powered financial technology, empowering its technologists to innovate, the impact of AI on workflows, and tips for staying consistent in your AI learning journey.
Blog
Hybrid vs Remote Work: Why Intuit's Hybrid Model Boosts Culture and Productivity
Discover Intuit's hybrid work model and the benefits it offers for fostering belonging, collaboration, and productivity.
Blog
Interview with an Intuit Principal Data Scientist
Discover what it took as Alex Zhou became a data Scientist at Intuit, including essential skillsets, and examples of high-impact projects.
Blog
AI-powered experiences take ‌center stage at Intuit’s Global Engineering Days
Intuit's Global Engineering Days (GED) brought together technologists for a weeklong code-a-thon focused on AI-powered experiences, with many projects incorporating GenAI to improve product usability and internal productivity.
Blog
Understanding Systems of Intelligence: Your Complete Guide
Thinking of integrating AI into your business? Learn from Intuit how systems of intelligence transform business operations through data-driven insights.
Blog
4 Things I’ve Learned about Growing My Career as a Working Parent
From "parenting out loud" to securing a promotion after maternity leave, Jenny shares how a supportive manager and a "brag book" fueled her trajectory at Intuit.
Article
Intuit and Anthropic Partner to Bring Trusted Financial Intelligence and Custom AI Agents to Consumers and Businesses
Mid-market businesses will be able to build secure, customizable AI agents on Intuit’s platform with Claude, tailored to their unique, industry-based needs.
Article
Management Reiterates Confidence in Long-Term Growth Trajectory
Intuit significantly scales share repurchase program; Intuit’s founder and executive leadership team terminate pre-scheduled stock sales.
Blog
How to Build a Culture of Innovation
Creating a culture of innovation does more than help your company identify and leverage new opportunities. It also can create an environment that encourages employees to break boundaries and try new things. When workers are inspired to pursue innovation, they’ll feel a greater sense of ownership in the company.
Blog
A Day in the Life of a Staff Product Manager at Intuit
At Intuit, we believe the foundation of great product management is a relentless focus on customer problems. Read on to understand what a day in the life of Jason Wirth, Staff Product Manager at Intuit, entails.
Blog
Internal Mobility
Internal Mobility Can Enhance Skill Development
Blog
4 Reasons to Make an Intuit Sales Career Your New Year’s Resolution
It’s a new year and it’s a great time to be in financial technology sales—and there’s no better place to do it than Intuit. As rapid innovation and growth transform our industry, Intuit’s longtime leadership in AI puts us at the forefront of this exciting market.
Blog
Intuit helps Americans keep more of their hard-earned money—so they can build their American Dream
Every tax season, Intuit helps millions of Americans navigate a complex tax system with confidence.
Blog
Unlock Your Career: Essential Data Scientist Skills and the Intuit Path to Success
At Intuit, our data-driven work model drives innovation for impact across customer solutions. When data scientists have insights into how customers use our products, we are able to develop strong insights into which features matter most in our customers’ financial lives.
Blog
How to Build a Strong Hybrid Work Culture
Building a strong hybrid work culture requires intentional planning and communication. Learn strategies to foster company culture in a hybrid work setting.
Interview with an Intuit Principal Data Scientist
Discover what it took as Alex Zhou became a data Scientist at Intuit, including essential skillsets, and examples of high-impact projects.
How AI Is Transforming Financial Technology (Fintech)
AI is transforming fintech beyond traditional automation with intelligent systems for personalized experiences, real-time fraud detection, and efficiency.
Article
Beyond the Code: Your Career Path as a Software Engineer or Developer at Intuit
Discover the dynamic software engineering roles at Intuit, where you'll work on cutting-edge fintech solutions, harness the power of AI and machine learning, and contribute to the company's mission of powering financial prosperity for small and medium-sized businesses.
Blog
4 Things I’ve Learned about Growing My Career as a Working Parent
From "parenting out loud" to securing a promotion after maternity leave, Jenny shares how a supportive manager and a "brag book" fueled her trajectory at Intuit.
Blog
Understanding Systems of Intelligence: Your Complete Guide
Thinking of integrating AI into your business? Learn from Intuit how systems of intelligence transform business operations through data-driven insights.
Blog
Thomas Ranese as CMO
Intuit Appoints Thomas Ranese as CMO
Blog
Internal Mobility
Internal Mobility Can Enhance Skill Development
Blog
Intuit and Anthropic Partner to Bring Trusted Financial Intelligence and Custom AI Agents to Consumers and Businesses
Mid-market businesses will be able to build secure, customizable AI agents on Intuit’s platform with Claude, tailored to their unique, industry-based needs.
Blog
Blog
Management Reiterates Confidence in Long-Term Growth Trajectory
Intuit significantly scales share repurchase program; Intuit’s founder and executive leadership team terminate pre-scheduled stock sales.
Blog
How to Build a Culture of Innovation
Creating a culture of innovation does more than help your company identify and leverage new opportunities. It also can create an environment that encourages employees to break boundaries and try new things. When workers are inspired to pursue innovation, they’ll feel a greater sense of ownership in the company.
Blog
Intuit introduces Intuit Enterprise Suite to help businesses grow, streamline operations, and scale
We're further expanding mid-market offerings with an all-in-one solution that serves larger and more complex businesses and fuels their growth.
Blog
4 Reasons to Make an Intuit Sales Career Your New Year’s Resolution
It’s a new year and it’s a great time to be in financial technology sales—and there’s no better place to do it than Intuit. As rapid innovation and growth transform our industry, Intuit’s longtime leadership in AI puts us at the forefront of this exciting market.
Blog
How AI Is Transforming Finance
Find out how AI is actually used in finance, what the future may hold, and how organizations can apply it responsibly and strategically.
Blog
Indigenous ties inspire new Toronto workplace
Toronto's inclusive workplace design inspired by indigenous ties
Blog
Innovative thinking at Intuit India
Building a true platform culture to solve customer problems
Blog
Intuit Israel ranked in best tech companies for parents
Israel's top 10 best high tech companies for parents
Blog
Starting a Successful Tax Career
4 Ways Intuit helped me start a successful tax career
Blog
Leveraging AI
How Intuit is leveraging AI to enhance customer experience
Blog
Leveraging AI
How Intuit is leveraging AI to enhance customer experience
Blog
How to Use AI for Data Analysis: Workflows, Use Cases, and Best Practices
AI is changing the way that data analysis is done. Explore AI's role in data analysis and enhance your decision-making process with Intuit.
Blog
Velocity Hacks
Velocity Hacks to work harder not smarter
Blog
Maximizing Your Sales Interview Success: Key Insights from Intuit Mailchimp’s Head of Sales
In today’s tech sales market, a great interview can define the trajectory of your career. But in a competitive market, how do you stand out?
Video
How Top Sales Achievers Celebrate Success and Make an Impact with Intuit's President's Club
The prestigious Intuit President’s Club is one of the rewards programs that epitomizes our recognition — an exclusive luxury getaway where top sales talent can connect with senior leaders, network with peers, and embody Intuit's values through team-building and community service, all in stunning global locations. Be part of this extraordinary experience and elevate your career with Intuit!
Article
Forbes: America's Top 100 Companies
From employee sentiment and customer loyalty to financial performance and cybersecurity — and so much more — these companies rise to the top.
Blog
Why elite tech sellers are choosing Intuit
Legacy ERPs list above $80K a year and take months to close. Intuit Enterprise Suite runs near $12K, with 90% of customers live in 30 days. Here's what that changes for sellers, and the mid-market roles we're hiring across pipeline and closing.
Video
Crushing It at Intuit: Traits of Our Top Performers
Our best people don't ask whether a goal can be reached. They focus on when and how. Here's a closer look at the grit, hunger, and humility behind top performance at Intuit, and what it takes to thrive in a culture that is competitive and human-centered at the same time.
Jobs For You
Viewed Jobs
Related Jobs
Saved Jobs
You haven't viewed any jobs yet. Start exploring now to find the perfect opportunity for you!
View all of our available opportunities
Winter 2027: Software Developer Co-op (8 months) Toronto, Canada
Staff Software Developer, Canada Tax Content Toronto, Canada
Staff Software Developer, Verified Identity Network Toronto, Canada
You haven't saved any jobs yet. Start exploring now to find the perfect opportunity for you!
View all of our available opportunities
Sign Up for Job Alerts
Be the first to find out about open jobs that fit what you're looking for.
""";

    private static string Normalize(string s)
        => Regex.Replace(s.ToLowerInvariant(), @"\s+", " ").Trim();

    // ================= R-152-1 / R-152-2 / R-152-3：模块切除 =================

    [Fact]
    public void Cut_RemovesBlogModuleAfterBenefitsSentence()
    {
        const string text = "Benefits\nWe support our employees by offering benefits you need to stay healthy.\nBlog\nIntuit introduces Intuit Enterprise Suite to help businesses grow\nBlog\n3 Ways to Drive Continuous AI Transformation\n";
        var cut = RelatedModuleCut.Cut(text);
        Assert.Contains("We support our employees", cut);
        Assert.DoesNotContain("Enterprise Suite", cut);
        Assert.DoesNotContain("3 Ways", cut);
    }

    [Fact]
    public void Cut_RelatedStoriesHeader_Triggers()
    {
        const string text = "Job Overview\nCome join us.\nRelated stories\nBlog\nTeaser one\nBlog\nTeaser two\n";
        var cut = RelatedModuleCut.Cut(text);
        Assert.Contains("Come join us", cut);
        Assert.DoesNotContain("Teaser", cut);
    }

    [Fact]
    public void Cut_SingleIsolatedBlogLine_DoesNotTrigger()
    {
        // A-152-4：单个孤立 Blog 行不触发切除，后续正文必须保留。
        const string text = "Requirements\nExperience with Java\nBlog\nMore requirements here\nAnd more\nStill more\nEven more\nDone\n";
        Assert.Equal(text, RelatedModuleCut.Cut(text));
    }

    [Fact]
    public void Parse_BenefitsSection_StopsAtModule()
    {
        const string text = "Senior Developer\nAcme\nBenefits\nWe offer health insurance and paid leave.\nBlog\nEnterprise Suite teaser\nBlog\nAnother teaser\n";
        var d = JdPasteParser.Parse(text, PasteKind.Text);
        Assert.Equal("We offer health insurance and paid leave.", d.Benefits);
    }

    [Fact]
    public void Parse_Summary_StopsAtModule()
    {
        const string text = "Job Overview\nCome join our team to build great products for customers worldwide.\nBlog\nTeaser one here\nBlog\nTeaser two here\n";
        var d = JdPasteParser.Parse(text, PasteKind.Text);
        Assert.NotNull(d.JdSummary);
        Assert.DoesNotContain("Teaser", d.JdSummary);
    }

    // ================= R-152-8(c) / A-152-1：Intuit 整帖夹具 =================

    [Fact]
    public void Parse_IntuitFixture_CleanSections()
    {
        var d = JdPasteParser.Parse(IntuitFixture, PasteKind.Text);

        // benefits：原帖 JD 正文里没有 Benefits 小节；轮播里的 "Benefits" 卡片属于模块，已切除。
        // （定稿 §0.1 称福利句“对的”只是页面逐字，不属于 JD 内容；切除后 benefits 应为空。）
        Assert.Null(d.Benefits);

        // requirements：Qualifications 小节原文保留。
        Assert.Contains("Experience developing web applications using Java and JavaScript", d.Requirements);
        Assert.Contains("Strong knowledge of Java and Spring Boot", d.Requirements);
        Assert.DoesNotContain("Enterprise Suite", d.Requirements);
        Assert.DoesNotContain("Ashok Srivastava", d.Requirements);

        // salary：补偿段的真实区间（规则归一为 k 写法），绝不是 70-85% bullet 的派生值。
        // （定稿 A-152-1 称 salary 为 null 是基于“原帖无薪资数字”的误判；实证：原帖正文含
        // "Toronto $132,000 - $178,500"，规则路径理应解出真实区间。见实装报告偏差说明。）
        Assert.Equal("$132k - $178k", d.Salary);

        // jdSummary：原文摘录，≤500、句末、非中文改写、为切除后材料的子串。
        Assert.NotNull(d.JdSummary);
        Assert.True(d.JdSummary.Length <= 500, $"summary length {d.JdSummary.Length} > 500");
        Assert.Contains(d.JdSummary[^1], new[] { '.', '!', '?', '。', '！', '？' });
        Assert.DoesNotMatch(@"[\u4e00-\u9fff]", d.JdSummary);
        var cut = RelatedModuleCut.Cut(IntuitFixture);
        Assert.Contains(Normalize(d.JdSummary), Normalize(cut));

        // matchKeywords：每项均在切除后材料中出现；模块独有词不得漏入。
        Assert.NotNull(d.MatchKeywords);
        var items = d.MatchKeywords.Split([','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var kw in items)
            Assert.Contains(Normalize(kw), Normalize(cut));
        foreach (var banned in new[] { "Go", "Rust", "SQL", "REST", "Machine Learning" })
            Assert.DoesNotContain(banned, items);
    }

    // ================= 父任务追加断言：70-85% bullet 绝不得成为薪资 =================

    [Fact]
    public void Parse_PercentageBullet_NeverBecomesSalary()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer\nAcme Corp\nToronto, ON\nResponsibilities\nRoughly 70-85% hands-on coding\nWe are hiring a Senior Developer to build great software products for our customers in Canada.",
            PasteKind.Text);
        Assert.Null(d.Salary);
    }

    [Fact]
    public void Parse_RealRangeStillExtracted_AfterPercentageGuard()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer\nAcme Corp\nToronto, ON\nResponsibilities\nRoughly 70-85% hands-on coding\nThe expected base pay range for this position is:\nToronto $132,000 - $178,500\nWe are hiring a Senior Developer to build great software products for our customers in Canada.",
            PasteKind.Text);
        Assert.Equal("$132k - $178k", d.Salary);
    }

    // ================= R-152-6：摘要摘录 =================

    [Fact]
    public void Parse_Summary_ExcerptCapsAt500WithSentenceEnd()
    {
        var longPara = string.Concat(Enumerable.Repeat("This is a substantial sentence about the role and its responsibilities. ", 30));
        var d = JdPasteParser.Parse("Senior Developer\nAcme\n" + longPara, PasteKind.Text);
        Assert.NotNull(d.JdSummary);
        Assert.True(d.JdSummary.Length <= 500, $"summary length {d.JdSummary.Length} > 500");
        Assert.EndsWith(".", d.JdSummary);
    }

    [Fact]
    public void Parse_Summary_FirstSentenceOverLimit_KeptWhole()
    {
        var big = "This role " + new string('x', 600) + ". Short tail.";
        var d = JdPasteParser.Parse("Senior Developer\nAcme\n" + big, PasteKind.Text);
        Assert.NotNull(d.JdSummary);
        Assert.True(d.JdSummary.Length > 500, "首句超 N 时应保留整首句");
        Assert.EndsWith(".", d.JdSummary);
        Assert.DoesNotContain("Short tail", d.JdSummary);
    }

    // ================= R-152-4 / R-152-5：提示词原样化 + 输出校验 =================

    private sealed class FakeAi(string json) : IAiGatewayClient
    {
        public string CapturedPrompt { get; private set; } = string.Empty;

        public Task<AiCompletionResult> CompleteAsync(string purpose, string prompt, string? systemPrompt,
            double? temperature, int? maxTokens, CancellationToken ct)
        {
            CapturedPrompt = prompt;
            return Task.FromResult(new AiCompletionResult(json, "fake", null, null, null));
        }

        public Task<PrepSourceSettingsDto> GetPrepSourceSettingsAsync(CancellationToken ct)
            => throw new NotImplementedException();

        public Task<PrepSourceSearchDto> SearchPrepSourcesAsync(string source, string company,
            string? role, string? stage, CancellationToken ct)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task Enrich_ValidatesAiValuesAgainstMaterial_AndIgnoresDroppedFields()
    {
        // draft 取自不含薪资/福利/公司介绍/身份要求的小文本；材料（draft.JdText）另行指定，
        // 使 AI 值的“在材料中 / 不在材料中”可精确控制。
        const string material = "Senior Developer\nAcme Corp\nToronto, ON\nHybrid Full-time\nAcme is a great company that builds software.\nWe offer health insurance and paid leave.\nMust be legally authorized to work in Canada\nWe are hiring a Senior Developer to build great software products with Java for our customers in Canada.\n";
        var draft = JdPasteParser.Parse(
            "Senior Developer\nAcme Corp\nToronto, ON\nWe are hiring a Senior Developer to build great software products for our customers in Canada.",
            PasteKind.Text) with { JdText = material };

        const string aiJson = """
            {
              "company": "Acme Corp",
              "role": "Senior Developer",
              "location": "Toronto",
              "salary": "70k - 85k",
              "workMode": "Hybrid",
              "jobType": "Full-time",
              "companyDescription": "Acme is a great company that builds software.",
              "requirements": "改写后的任职要求，不在原文中。",
              "benefits": "We offer health insurance and paid leave.",
              "workAuthorization": "Must be legally authorized to work in Canada",
              "matchKeywords": "Java, QuantumFlux",
              "jdSummary": "这是一段中文摘要，应该被忽略",
              "priority": "High",
              "deadline": "2026-12-31"
            }
            """;
        var fake = new FakeAi(aiJson);
        var merged = await JdAiEnricher.EnrichAsync(fake, material, draft, CancellationToken.None);

        Assert.NotNull(merged);
        // 提示词已原样化：无中文摘要字段、无 jdSummary/priority/deadline 字段。
        Assert.DoesNotContain("中文摘要", fake.CapturedPrompt);
        Assert.DoesNotContain("jdSummary", fake.CapturedPrompt);
        Assert.DoesNotContain("\"priority\"", fake.CapturedPrompt);
        Assert.DoesNotContain("\"deadline\"", fake.CapturedPrompt);
        Assert.Contains("只搬运原文", fake.CapturedPrompt);
        // 无依据薪资被校验置 null；改写段落被置 null；有依据值保留。
        Assert.Null(merged.Salary);
        Assert.Null(merged.Requirements);
        Assert.Equal("Acme is a great company that builds software.", merged.CompanyDescription);
        Assert.Equal("We offer health insurance and paid leave.", merged.Benefits);
        Assert.Equal("Must be legally authorized to work in Canada", merged.WorkAuthorization);
        Assert.Equal("Hybrid", merged.WorkMode);
        Assert.Equal("Full-time", merged.JobType);
        // 关键词逐个校验：不在材料中的单个丢弃，不株连整组。
        Assert.NotNull(merged.MatchKeywords);
        Assert.Contains("Java", merged.MatchKeywords);
        Assert.DoesNotContain("QuantumFlux", merged.MatchKeywords);
        // 已删除的 AI 字段一律被忽略：摘要/优先级/截止日期保持 draft 值。
        Assert.Equal(draft.JdSummary, merged.JdSummary);
        Assert.Equal(draft.Priority, merged.Priority);
        Assert.Null(merged.Deadline);
    }

    [Fact]
    public async Task Enrich_IntuitFixture_MergedCompanyDescriptionKept_TeaserBenefitsKilled()
    {
        var draft = JdPasteParser.Parse(IntuitFixture, PasteKind.Text);
        Assert.Equal("$132k - $178k", draft.Salary);
        Assert.Null(draft.Benefits);

        const string aiJson = """
            {
              "companyDescription": "Intuit is the global financial technology platform that powers prosperity for the people and communities we serve. With tens of millions of customers worldwide using products such as TurboTax, Credit Karma, QuickBooks, and Mailchimp, we believe that everyone should have the opportunity to prosper. We never stop working to find new, innovative ways to make that possible.",
              "benefits": "Intuit introduces Intuit Enterprise Suite to help businesses grow, streamline operations, and scale",
              "salary": "70k - 85k"
            }
            """;
        var merged = await JdAiEnricher.EnrichAsync(new FakeAi(aiJson), IntuitFixture, draft, CancellationToken.None);
        Assert.NotNull(merged);
        // 公司介绍原文在切除后材料中 → 保留；博客 teaser 不在材料中 → 置 null；薪资 draft 优先。
        Assert.Contains("Intuit is the global financial technology platform", merged.CompanyDescription);
        Assert.Null(merged.Benefits);
        Assert.Equal("$132k - $178k", merged.Salary);
    }
}
