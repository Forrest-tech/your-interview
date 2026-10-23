using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Knowledge.Domain;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;

namespace YourInterview.Services.Knowledge.Application;

// ============================ 校验器 ============================

public sealed class CreateKnowledgeCommandValidator : AbstractValidator<CreateKnowledgeCommand>
{
    public CreateKnowledgeCommandValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Topic).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Question).NotEmpty().MaximumLength(8000);
        RuleFor(x => x.Difficulty).InclusiveBetween(1, 5);
        RuleFor(x => x.Importance).InclusiveBetween(1, 5);
    }
}

public sealed class UpdateKnowledgeCommandValidator : AbstractValidator<UpdateKnowledgeCommand>
{
    public UpdateKnowledgeCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Topic).NotEmpty().MaximumLength(100);
        // 票61:与 Create 口径对齐 —— Question 必填。全量 PUT 的唯一前端调用方是
        // 详情保存(回填现存 question),无任何合法「清空 question」流;
        // 空串放行只会误清已存题干(数据丢失)。
        RuleFor(x => x.Question).NotEmpty().MaximumLength(8000);
        RuleFor(x => x.Difficulty).InclusiveBetween(1, 5);
        RuleFor(x => x.Importance).InclusiveBetween(1, 5);
    }
}

public sealed class RenameTopicCommandValidator : AbstractValidator<RenameTopicCommand>
{
    public RenameTopicCommandValidator()
    {
        // 票83:此前改名无校验器,超长新名直达 Topic 列(HasMaxLength(100))在
        // DB 层爆异常而非明确 400 —— 与 AddCustomCategory 同口径补齐。
        // 票107:必填/长度文案分挂两条规则(WithMessage 只挂紧邻的前一条规则)。
        RuleFor(x => x.NewName).NotEmpty().WithMessage("分类名必填")
            .MaximumLength(TechStackLocalDataRules.MaxCategoryNameLength)
            .WithMessage("分类名长度不能超过 {MaxLength} 个字符");
    }
}

/// <summary>
/// 票109:qa-import / prep-report 入库链(ImportInterviewCandidatesCommand)此前
/// 无校验器,字段只有 EF 硬限 —— 超限穿透 DB 回 500。本校验器只补长度
/// (上限 = KnowledgeItem 的 EF 映射逐字),不加 NotEmpty:Handler 对空
/// Title/Topic 行是「跳过」语义,加必填会把整单变 400,属行为变更。
/// text 字段(ConceptExplanation/BetterAnswer/KeyPointsJson)无 DB 限,不入围。
/// </summary>
public sealed class ImportInterviewCandidatesCommandValidator : AbstractValidator<ImportInterviewCandidatesCommand>
{
    public ImportInterviewCandidatesCommandValidator()
    {
        RuleFor(x => x.Company)
            .MaximumLength(300).WithMessage("公司名称长度不能超过 {MaxLength} 个字符");
        RuleFor(x => x.RoundStage)
            .MaximumLength(60).WithMessage("轮次阶段长度不能超过 {MaxLength} 个字符");
        RuleForEach(x => x.Items).SetValidator(new InterviewCandidateRowValidator());
    }
}

public sealed class InterviewCandidateRowValidator : AbstractValidator<InterviewCandidateRow>
{
    public InterviewCandidateRowValidator()
    {
        RuleFor(x => x.Title)
            .MaximumLength(500).WithMessage("标题长度不能超过 {MaxLength} 个字符");
        RuleFor(x => x.Topic)
            .MaximumLength(100).WithMessage("分类长度不能超过 {MaxLength} 个字符");
        RuleFor(x => x.SubTopic)
            .MaximumLength(200).WithMessage("子分类长度不能超过 {MaxLength} 个字符");
        RuleFor(x => x.Question)
            .MaximumLength(8000).WithMessage("问题长度不能超过 {MaxLength} 个字符");
        RuleFor(x => x.TagsJson)
            .MaximumLength(1000).WithMessage("标签长度不能超过 {MaxLength} 个字符");
        RuleFor(x => x.ClientKey)
            .MaximumLength(200).WithMessage("导入键长度不能超过 {MaxLength} 个字符");
    }
}

public sealed class RecordReviewCommandValidator : AbstractValidator<RecordReviewCommand>
{
    private static readonly string[] Allowed = ["Again", "Hard", "Good", "Easy"];

    public RecordReviewCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Result).Must(r => Allowed.Contains(r, StringComparer.OrdinalIgnoreCase))
            .WithMessage("复习结果只能是 Again / Hard / Good / Easy");
    }
}

