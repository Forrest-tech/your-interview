using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Domain.JsonFields;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using YourInterview.Services.Interviews.Infrastructure.Services;
using YourInterview.Services.Interviews.Infrastructure.Storage;
using YourInterview.SharedContracts.Ai;

namespace YourInterview.Services.Interviews.Application;

// ============================ 校验器 ============================
// 注意:校验器由 ServiceDefaults 的反射扫描自动注册(踩过"忘了注册导致校验静默失效"的坑)。

public sealed class CreateEntryCommandValidator : AbstractValidator<CreateEntryCommand>
{
    public CreateEntryCommandValidator()
    {
        RuleFor(x => x.CompanyName).NotEmpty().MaximumLength(300)
            .WithMessage("公司名不能为空且不超过 300 字");
        RuleFor(x => x.Role).NotEmpty().MaximumLength(300).WithMessage("岗位名不能为空");
        RuleFor(x => x.JdText).MaximumLength(60000);
    }
}

public sealed class UpdateEntryCommandValidator : AbstractValidator<UpdateEntryCommand>
{
    public UpdateEntryCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.CompanyName).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Role).NotEmpty().MaximumLength(300);
        RuleFor(x => x.RoundNo).InclusiveBetween(1, 20).WithMessage("轮次应在 1-20 之间");
    }
}

public sealed class AttachAssetCommandValidator : AbstractValidator<AttachAssetCommand>
{
    private static readonly string[] AllowedKinds =
        ["Audio", "Transcript", "Notes", "Text", "PrepDoc", "Email", "Feedback", "Other"];
    private static readonly string[] AllowedPhases = ["Pre", "Post"];

    public AttachAssetCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Kind).Must(k => AllowedKinds.Contains(k, StringComparer.OrdinalIgnoreCase))
            .WithMessage("材料类型无效");
        // 文本类材料必须有内容 —— 这条规则挡掉了"传了个空的 Transcript"
        RuleFor(x => x.TranscriptText)
            .NotEmpty()
            .When(x => !string.Equals(x.Kind, "Audio", StringComparison.OrdinalIgnoreCase))
            .WithMessage("文本类材料必须提供内容");
        // 2026-10-20 详情页 v2:统一入口允许条目级材料(RoundId=null → 沟通记录),
        // 不再强制 RoundId;给了 RoundId 时由领域层校验轮次存在。
        RuleFor(x => x.Phase)
            .Must(p => p is null || AllowedPhases.Contains(p, StringComparer.OrdinalIgnoreCase))
            .WithMessage("阶段只能是 Pre(面试前) / Post(面试后)");
    }
}

public sealed class UploadAssetFileCommandValidator : AbstractValidator<UploadAssetFileCommand>
{
    public UploadAssetFileCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(500)
            .WithMessage("请选择要上传的录音文件");
        // 大小上限在 Handler 里对着真实流校验(信 Form 长度不如信实读字节数),
        // 这里只挡明显非法的值,避免双重报错口径。
        RuleFor(x => x.DurationSeconds).InclusiveBetween(0, 24 * 3600)
            .When(x => x.DurationSeconds.HasValue)
            .WithMessage("录音时长不能超过 24 小时");
        // 2026-10-20 详情页 v2:录音也可归条目级(沟通电话录音),RoundId 可空。
        RuleFor(x => x.Phase)
            .Must(p => p is null || p.Equals("Pre", StringComparison.OrdinalIgnoreCase)
                                || p.Equals("Post", StringComparison.OrdinalIgnoreCase))
            .WithMessage("阶段只能是 Pre(面试前) / Post(面试后)");
    }
}

public sealed class AddQuestionCommandValidator : AbstractValidator<AddQuestionCommand>
{
    public AddQuestionCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.QuestionText).NotEmpty().MaximumLength(8000);
        RuleFor(x => x.Difficulty).InclusiveBetween(1, 5);
    }
}

public sealed class AddRoundCommandValidator : AbstractValidator<AddRoundCommand>
{
    public AddRoundCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.Stage).NotEmpty().MaximumLength(50);
    }
}

public sealed class UpdateRoundCommandValidator : AbstractValidator<UpdateRoundCommand>
{
    public UpdateRoundCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.RoundId).NotEmpty();
        RuleFor(x => x.Stage).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Outcome).NotEmpty();
        RuleFor(x => x.Interviewers).MaximumLength(1000);
        RuleFor(x => x.Location).MaximumLength(300);
    }
}

// ============================ Playbook 重做(A1–A7)校验器 ============================

public sealed class UpdateQuestionMasteryCommandValidator
    : AbstractValidator<UpdateQuestionMasteryCommand>
{
    public UpdateQuestionMasteryCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.QuestionId).NotEmpty();
        RuleFor(x => x.Mastery).InclusiveBetween(0, 3)
            .WithMessage("掌握度只能是 0-3(0=待练,1=练习中,2=已掌握,3=还卡壳)");
    }
}

public sealed class CreateActionCommandValidator : AbstractValidator<CreateActionCommand>
{
    public CreateActionCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(500)
            .WithMessage("待办标题不能为空且不超过 500 字");
        RuleFor(x => x.Detail).MaximumLength(8000);
    }
}

public sealed class UpdateActionCommandValidator : AbstractValidator<UpdateActionCommand>
{
    public UpdateActionCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.ActionId).NotEmpty();
        RuleFor(x => x.Title).MaximumLength(500).When(x => x.Title is not null);
    }
}

public sealed class UpdateRoundEmailsCommandValidator : AbstractValidator<UpdateRoundEmailsCommand>
{
    public UpdateRoundEmailsCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.RoundId).NotEmpty();
        RuleFor(x => x.Emails).NotNull().WithMessage("emails 不能为空");
        RuleForEach(x => x.Emails).ChildRules(email =>
        {
            email.RuleFor(e => e.From).MaximumLength(500);
            email.RuleFor(e => e.Subject).MaximumLength(1000);
            email.RuleFor(e => e.Date).MaximumLength(100);
            email.RuleFor(e => e.Snippet).MaximumLength(4000);
        });
    }
}

