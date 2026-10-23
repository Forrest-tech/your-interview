using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;

namespace YourInterview.Services.Jobs.Application;

// ============================ 校验 ============================

public sealed class CreateCompanyCommandValidator : AbstractValidator<CreateCompanyCommand>
{
    public CreateCompanyCommandValidator()
    {
        // 票107:WithMessage 只挂紧邻的前一条规则——必填/长度文案须分挂两条规则,
        // 否则长度违规会被误标「必填」、空值只回英文默认文案。
        RuleFor(x => x.Name).NotEmpty().WithMessage("公司名称必填")
            .MaximumLength(300).WithMessage("公司名称长度不能超过 {MaxLength} 个字符");
        RuleFor(x => x.Website).MaximumLength(1024)
            .Must(BeValidUrl).When(x => !string.IsNullOrWhiteSpace(x.Website))
            .WithMessage("官网地址格式不正确");
        RuleFor(x => x.CompanyType).Must(t => new[] { "DirectEmployer", "Agency", "Staffing", "Unknown" }.Contains(t))
            .WithMessage("公司类型必须是 DirectEmployer / Agency / Staffing / Unknown");
    }

    private static bool BeValidUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// JD 长文本的全仓统一长度口径 (FUN-04 / 票62)。
/// 列已扩为 text 不再有库端上限,闸门收敛到应用层一处:所有写 JdText 的命令
/// (Create / Update / SetJdContent / SmartAdd) 共用此上限,超限一律 400 校验,
/// 不许再出现"一条路径 400、另一条路径落库 500"的缝隙。
/// 票62 起,SmartAdd 的四段长文本 (JdSummary / Requirements / Benefits /
/// CompanyDescription) 同样扩 text 并共用此上限 —— 四列的写路径只有
/// Create / Update / SmartAdd 三条,校验器逐条对齐,不许再漏。
/// 取值与 ParseJdCommand 的粘贴输入上限 (200_000) 对齐 —— 能粘进来的 JD 就一定存得下。
/// </summary>
public static class JdTextLimits
{
    public const int MaxLength = 200_000;
}

public sealed class CreateApplicationCommandValidator : AbstractValidator<CreateApplicationCommand>
{
    public CreateApplicationCommandValidator()
    {
        RuleFor(x => x.CompanyId).NotEmpty();
        RuleFor(x => x.Role).NotEmpty().WithMessage("岗位名称必填")
            .MaximumLength(300).WithMessage("岗位名称长度不能超过 {MaxLength} 个字符");
        RuleFor(x => x.Link).MaximumLength(2048);
        // FUN-02/FUN-04:JdText 与统一口径对齐 (JdTextLimits.MaxLength),超长给明确 400 而不是落库 500。
        RuleFor(x => x.JdText).MaximumLength(JdTextLimits.MaxLength);
        // 票62:四段长文本同口径 (列已扩 text),超长同样 400 而不是落库 500。
        RuleFor(x => x.JdSummary).MaximumLength(JdTextLimits.MaxLength);
        RuleFor(x => x.Requirements).MaximumLength(JdTextLimits.MaxLength);
        RuleFor(x => x.Benefits).MaximumLength(JdTextLimits.MaxLength);
        RuleFor(x => x.CompanyDescription).MaximumLength(JdTextLimits.MaxLength);
        RuleFor(x => x.Notes).MaximumLength(8000);
        RuleFor(x => x.Priority).Must(p => p is null || Enum.TryParse<Priority>(p, true, out _))
            .WithMessage("优先级必须是 Low / Medium / High / Critical");
    }
}

public sealed class UpdateApplicationCommandValidator : AbstractValidator<UpdateApplicationCommand>
{
    public UpdateApplicationCommandValidator()
    {
        // FUN-04:此前 Update 无校验,JdText 超列上限直接落库 500。与统一口径对齐。
        // JdText 为 null = 未提供 (保持原值),MaximumLength 对 null 放行,语义不变。
        RuleFor(x => x.JdText).MaximumLength(JdTextLimits.MaxLength);
        // 票62:四段长文本同口径。UpdateDetails 对 JdSummary 直接赋值、对其余三列
        // null = 保持原值;MaximumLength 对 null 放行,两种语义都不受影响。
        RuleFor(x => x.JdSummary).MaximumLength(JdTextLimits.MaxLength);
        RuleFor(x => x.Requirements).MaximumLength(JdTextLimits.MaxLength);
        RuleFor(x => x.Benefits).MaximumLength(JdTextLimits.MaxLength);
        RuleFor(x => x.CompanyDescription).MaximumLength(JdTextLimits.MaxLength);
    }
}

public sealed class SetJdContentCommandValidator : AbstractValidator<SetJdContentCommand>
{
    public SetJdContentCommandValidator()
    {
        // FUN-04:此前 SetJdContent 无校验,同上。与统一口径对齐。
        RuleFor(x => x.JdText).MaximumLength(JdTextLimits.MaxLength);
    }
}

public sealed class ChangeApplicationStatusCommandValidator : AbstractValidator<ChangeApplicationStatusCommand>
{
    public ChangeApplicationStatusCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Status).NotEmpty()
            .Must(s => Enum.TryParse<ApplicationStatus>(s, true, out _))
            .WithMessage("无效的状态值");
    }
}

