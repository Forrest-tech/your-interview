using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Domain;
using YourInterview.Services.Jobs.Domain;

namespace YourInterview.Services.Jobs.Infrastructure.Persistence;

public static class JobsDbSeeder
{
    public static async Task MigrateAndSeedAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobsDbContext>();
        // 用 EF 迁移建表(而非 EnsureCreated —— 后者一旦库中存在同名 schema 就会静默跳过)
        await db.Database.MigrateAsync();

        if (!(config["Seed:CreateDemoData"] ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Jobs 数据库就绪(未写入演示数据)");
            return;
        }

        if (await db.Applications.AnyAsync()) { logger.LogInformation("Jobs 演示数据已存在,跳过"); return; }

        // 用 Forrest 真实投递历史构造演示数据(公司名脱敏保留真实投递过的岗位)
        var seeds = new[]
        {
            ("PartnerRe", "Senior Full Stack Developer", "Toronto, ON", "Hybrid 3d", "$110,250-$134,750", "High", "Applied", "DirectEmployer"),
            ("Xplore Inc.", "Senior .NET Developer", "Remote (Canada)", "Remote", "—", "High", "Applied", "DirectEmployer"),
            ("Descartes Systems", "Senior Full Stack .NET Developer & Team Lead", "Waterloo, ON", "Hybrid", "$100,000-$120,000", "High", "Interview", "DirectEmployer"),
            ("Rentsync", "Senior .NET Developer", "Toronto, ON", "Hybrid 2-3d", "$100,000-$120,000", "High", "Rejected", "DirectEmployer"),
            ("Geotab", "Senior Software Developer", "Oakville, ON", "Hybrid", "—", "High", "Interview", "DirectEmployer"),
            ("CIBC (via Kumaran)", "Senior Developer", "Toronto, ON", "Hybrid", "—", "Medium", "Rejected", "Staffing"),
            ("Gateway Services", "Software Engineer", "Guelph, ON", "Onsite", "—", "High", "Rejected", "DirectEmployer"),
            ("Pack-Smart (Vision Systems)", "C# Developer", "Vaughan, ON", "Onsite", "$100,000-$130,000", "High", "Screen", "DirectEmployer"),
            ("VueReal", "Systems/SRE Engineer", "Waterloo, ON", "Onsite", "—", "Medium", "Screen", "DirectEmployer"),
            ("Q1 Technologies", "Full Stack Developer", "Toronto, ON", "Hybrid 2d", "—", "Medium", "Applied", "Agency"),
            ("Iris Software", "Senior .NET Developer", "Mississauga, ON", "Hybrid", "—", "Medium", "Applied", "Staffing"),
            ("Transworld Systems (TSI)", "Senior Developer", "Remote", "Remote", "$90,000-$108,000", "High", "Saved", "DirectEmployer"),
            ("Livingston International", "Senior Developer", "Toronto, ON", "Hybrid", "$70,000-$106,000", "Low", "Paused", "DirectEmployer"),
            ("AKITU Inc.", "Full-Stack Developer (C#/Angular)", "Oakville, ON", "Hybrid", "$80,000+", "Medium", "Saved", "DirectEmployer"),
            ("ProCharted", "Senior Software Developer, Desktop Automation", "Burnaby, BC", "Hybrid", "$120,000-$185,000", "Low", "Paused", "DirectEmployer"),
            ("Jade Global", "DevOps Engineer", "Remote (Canada)", "Remote", "—", "Low", "Paused", "Agency"),
            ("Ontario CJDD", "Senior Software Developer", "Toronto, ON", "Hybrid", "—", "Medium", "Applied", "DirectEmployer"),
            ("OST", "Senior .NET Developer", "Toronto, ON", "Hybrid", "—", "Medium", "Interview", "DirectEmployer"),
            ("Agility Consulting → Pinnacle", "Backend Developer (C#/.NET)", "North York, ON", "Hybrid", "$130,000", "High", "Screen", "Agency"),
            ("Merrithew", ".NET Full Stack Developer", "Toronto, ON", "Hybrid", "$85,000-$95,000", "Low", "Paused", "DirectEmployer"),
            ("Metrolinx", "Senior Software Developer", "Toronto, ON", "Hybrid", "$110,000-$154,000", "High", "Applied", "DirectEmployer"),
            ("Libro Credit Union", "Software Developer Analyst L3", "London, ON", "Hybrid", "—", "Medium", "Applied", "DirectEmployer"),
            ("Rakuten Kobo", "Software Engineer II", "Toronto, ON", "Hybrid", "—", "Medium", "Applied", "DirectEmployer"),
            ("InfoTrack", "Senior Full Stack Developer", "Remote (Canada)", "Remote", "—", "Medium", "Applied", "DirectEmployer"),
            ("Hammond", "Data & AI Developer", "Toronto, ON", "Hybrid", "—", "Low", "Applied", "DirectEmployer"),
            ("BULLIT", "Fullstack .NET Developer (Azure)", "Toronto, ON", "Hybrid", "—", "Medium", "Applied", "Agency"),
            ("Equitable Life", "Core Systems Developer", "Waterloo, ON", "Hybrid", "—", "Medium", "Applied", "DirectEmployer"),
            ("JM Group", ".NET Full Stack Developer", "Toronto, ON", "Hybrid", "—", "Medium", "Applied", "Agency"),
            ("SystemSoft", ".NET Developer", "Markham, ON", "Hybrid", "—", "Medium", "Applied", "Staffing"),
            ("Nityo Infotech", "Fullstack Developer", "Toronto, ON", "Hybrid", "—", "Low", "Paused", "Staffing"),
            ("Epicor (Innovative Automation)", "Kinetic Developer", "Barrie, ON", "Onsite", "$66,000-$112,000", "Low", "Paused", "DirectEmployer"),
            ("autoTRADER / Dealertrack", "Software Engineer", "Toronto, ON", "Hybrid", "—", "Medium", "Applied", "DirectEmployer"),
            ("LRO Staffing (NRC)", "Senior Programmer Analyst", "Ottawa, ON", "Onsite", "$80-$120/hr", "Low", "Paused", "Staffing")
        };

        var rnd = new Random(7);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var (companyName, role, location, workMode, salary, priority, status, companyType) in seeds)
        {
            var company = new Company(companyName, null, null, location, null, null);
            company.Update(companyName, null, null, location, null, null, companyType, null);
            db.Companies.Add(company);
            await db.SaveChangesAsync();

            var app = new JobApplication(company.Id, role, location, null, salary, workMode, "LinkedIn", null);
            if (Enum.TryParse<Domain.Priority>(priority, true, out var p)) app.SetPriority(p);

            var appliedDaysAgo = rnd.Next(3, 90);
            var appliedDate = today.AddDays(-appliedDaysAgo);

            if (status != "Saved") app.MarkApplied(appliedDate, "演示数据");

            if (Enum.TryParse<Domain.ApplicationStatus>(status, true, out var st) && st != Domain.ApplicationStatus.Saved)
            {
                foreach (var step in PathTo(app.Status, st))
                    if (app.CanTransitionTo(step)) app.ChangeStatus(step, "演示数据");
            }

            // 已进入面试的补轮次
            if (app.Status is Domain.ApplicationStatus.Interview or Domain.ApplicationStatus.Offer
                or Domain.ApplicationStatus.Accepted)
            {
                app.AddRound("Recruiter Screen", appliedDate.AddDays(rnd.Next(3, 10)), "Talent Acquisition", "Phone", null);
                app.AddRound("Technical", appliedDate.AddDays(rnd.Next(11, 20)), "Engineering Manager", "Video", null);
            }

            // 跟进提醒
            if (app.Status is Domain.ApplicationStatus.Applied or Domain.ApplicationStatus.Screen)
                app.ScheduleFollowUp(DateTimeOffset.UtcNow.AddDays(rnd.Next(1, 7)));

            db.Applications.Add(app);
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Jobs 演示数据已写入:{Count} 条投递记录", seeds.Length);
    }

    /// <summary>计算从当前状态到目标状态需要经过的状态序列(状态机最短路径)。</summary>
    private static IEnumerable<Domain.ApplicationStatus> PathTo(Domain.ApplicationStatus from, Domain.ApplicationStatus to)
    {
        if (to == from) yield break;
        if (from == Domain.ApplicationStatus.Saved || from == Domain.ApplicationStatus.Applied)
        {
            if (to is Domain.ApplicationStatus.Screen or Domain.ApplicationStatus.Interview
                or Domain.ApplicationStatus.Offer or Domain.ApplicationStatus.Accepted)
            {
                yield return Domain.ApplicationStatus.Screen;
                if (to is Domain.ApplicationStatus.Interview or Domain.ApplicationStatus.Offer
                    or Domain.ApplicationStatus.Accepted)
                {
                    yield return Domain.ApplicationStatus.Interview;
                    if (to is Domain.ApplicationStatus.Offer or Domain.ApplicationStatus.Accepted)
                    {
                        yield return Domain.ApplicationStatus.Offer;
                        if (to == Domain.ApplicationStatus.Accepted)
                            yield return Domain.ApplicationStatus.Accepted;
                    }
                }
            }
            else
            {
                yield return to;
            }
        }
        else
        {
            yield return to;
        }
    }
}
