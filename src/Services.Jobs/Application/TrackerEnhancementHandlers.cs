using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;

namespace YourInterview.Services.Jobs.Application;

// ============================ DTO ============================

public sealed record AnswerTemplateDto(
    Guid Id, string Category, string Question, string Answer, int SortOrder,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record CommunicationDto(
    Guid Id, Guid ApplicationId, string Type, string? Subject, string Content,
    string? ContactName, string? ContactEmail, DateTimeOffset OccurredAt,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

// ============================ 申请问答库(用户级) ============================

public sealed record ListAnswerTemplatesQuery(Guid UserId, string? Category = null)
    : IRequest<Result<IReadOnlyList<AnswerTemplateDto>>>;

public sealed record CreateAnswerTemplateCommand(Guid UserId, string Category, string Question, string Answer)
    : IRequest<Result<Guid>>;

public sealed record UpdateAnswerTemplateCommand(Guid Id, Guid UserId, string Category, string Question, string Answer)
    : IRequest<Result>;

public sealed record DeleteAnswerTemplateCommand(Guid Id, Guid UserId) : IRequest<Result>;

// ============================ 沟通记录(投递级) ============================

public sealed record ListCommunicationsQuery(Guid ApplicationId)
    : IRequest<Result<IReadOnlyList<CommunicationDto>>>;

public sealed record CreateCommunicationCommand(
    Guid ApplicationId, string Type, string? Subject, string Content,
    string? ContactName, string? ContactEmail, DateTimeOffset OccurredAt)
    : IRequest<Result<Guid>>;

public sealed record UpdateCommunicationCommand(
    Guid Id, Guid ApplicationId, string Type, string? Subject, string Content,
    string? ContactName, string? ContactEmail, DateTimeOffset OccurredAt)
    : IRequest<Result>;

public sealed record DeleteCommunicationCommand(Guid Id, Guid ApplicationId) : IRequest<Result>;

// ============================ 校验 ============================

public sealed class CreateAnswerTemplateCommandValidator : AbstractValidator<CreateAnswerTemplateCommand>
{
    public CreateAnswerTemplateCommandValidator()
    {
        RuleFor(x => x.Category).NotEmpty().MaximumLength(100).WithMessage("分类必填");
        RuleFor(x => x.Question).NotEmpty().MaximumLength(500).WithMessage("问题必填");
        RuleFor(x => x.Answer).NotEmpty().MaximumLength(20000).WithMessage("答案必填");
    }
}

public sealed class CreateCommunicationCommandValidator : AbstractValidator<CreateCommunicationCommand>
{
    public CreateCommunicationCommandValidator()
    {
        RuleFor(x => x.Type).NotEmpty()
            .Must(t => new[] { "Email", "Call", "Interview", "Message", "Note" }.Contains(t))
            .WithMessage("沟通类型必须是 Email / Call / Interview / Message / Note");
        RuleFor(x => x.Content).NotEmpty().MaximumLength(20000).WithMessage("内容必填");
        RuleFor(x => x.Subject).MaximumLength(300);
        RuleFor(x => x.ContactName).MaximumLength(300);
        RuleFor(x => x.ContactEmail).MaximumLength(300);
    }
}

// ============================ Handler:问答库 ============================

public sealed class ListAnswerTemplatesQueryHandler(JobsDbContext db)
    : IRequestHandler<ListAnswerTemplatesQuery, Result<IReadOnlyList<AnswerTemplateDto>>>
{
    public async Task<Result<IReadOnlyList<AnswerTemplateDto>>> Handle(ListAnswerTemplatesQuery request, CancellationToken ct)
    {
        var q = db.AnswerTemplates.AsNoTracking().Where(t => t.UserId == request.UserId);
        if (!string.IsNullOrWhiteSpace(request.Category))
            q = q.Where(t => t.Category == request.Category.Trim());

        var items = await q.OrderBy(t => t.Category).ThenBy(t => t.SortOrder).ThenBy(t => t.Question)
            .Select(t => new AnswerTemplateDto(t.Id, t.Category, t.Question, t.Answer, t.SortOrder, t.CreatedAt, t.UpdatedAt))
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<AnswerTemplateDto>>(items);
    }
}

public sealed class CreateAnswerTemplateCommandHandler(JobsDbContext db)
    : IRequestHandler<CreateAnswerTemplateCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateAnswerTemplateCommand request, CancellationToken ct)
    {
        var sortOrder = await db.AnswerTemplates.AsNoTracking()
            .Where(t => t.UserId == request.UserId).CountAsync(ct) + 1;

        var t = new ApplicationAnswerTemplate(request.UserId, request.Category.Trim(),
            request.Question.Trim(), request.Answer, sortOrder);
        db.AnswerTemplates.Add(t);
        await db.SaveChangesAsync(ct);
        return Result.Success(t.Id);
    }
}

public sealed class UpdateAnswerTemplateCommandHandler(JobsDbContext db)
    : IRequestHandler<UpdateAnswerTemplateCommand, Result>
{
    public async Task<Result> Handle(UpdateAnswerTemplateCommand request, CancellationToken ct)
    {
        var t = await db.AnswerTemplates.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (t is null) return Result.Failure(Error.NotFound("问答模板"));

        t.Update(request.Category.Trim(), request.Question.Trim(), request.Answer, t.SortOrder);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class DeleteAnswerTemplateCommandHandler(JobsDbContext db)
    : IRequestHandler<DeleteAnswerTemplateCommand, Result>
{
    public async Task<Result> Handle(DeleteAnswerTemplateCommand request, CancellationToken ct)
    {
        var t = await db.AnswerTemplates.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (t is null) return Result.Failure(Error.NotFound("问答模板"));

        t.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ============================ Handler:沟通记录 ============================

public sealed class ListCommunicationsQueryHandler(JobsDbContext db)
    : IRequestHandler<ListCommunicationsQuery, Result<IReadOnlyList<CommunicationDto>>>
{
    public async Task<Result<IReadOnlyList<CommunicationDto>>> Handle(ListCommunicationsQuery request, CancellationToken ct)
    {
        if (!await db.Applications.AsNoTracking().AnyAsync(x => x.Id == request.ApplicationId, ct))
            return Result.Failure<IReadOnlyList<CommunicationDto>>(Error.NotFound("投递记录"));

        var items = await db.Communications.AsNoTracking()
            .Where(c => c.ApplicationId == request.ApplicationId)
            .OrderByDescending(c => c.OccurredAt).ThenByDescending(c => c.CreatedAt)
            .Select(c => new CommunicationDto(c.Id, c.ApplicationId, c.Type, c.Subject, c.Content,
                c.ContactName, c.ContactEmail, c.OccurredAt, c.CreatedAt, c.UpdatedAt))
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<CommunicationDto>>(items);
    }
}

public sealed class CreateCommunicationCommandHandler(JobsDbContext db)
    : IRequestHandler<CreateCommunicationCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateCommunicationCommand request, CancellationToken ct)
    {
        if (!await db.Applications.AsNoTracking().AnyAsync(x => x.Id == request.ApplicationId, ct))
            return Result.Failure<Guid>(Error.NotFound("投递记录"));

        var c = new ApplicationCommunication(request.ApplicationId, request.Type, request.Subject,
            request.Content, request.ContactName, request.ContactEmail, request.OccurredAt);
        db.Communications.Add(c);
        await db.SaveChangesAsync(ct);
        return Result.Success(c.Id);
    }
}

public sealed class UpdateCommunicationCommandHandler(JobsDbContext db)
    : IRequestHandler<UpdateCommunicationCommand, Result>
{
    public async Task<Result> Handle(UpdateCommunicationCommand request, CancellationToken ct)
    {
        var c = await db.Communications.FirstOrDefaultAsync(
            x => x.Id == request.Id && x.ApplicationId == request.ApplicationId, ct);
        if (c is null) return Result.Failure(Error.NotFound("沟通记录"));

        c.Update(request.Type, request.Subject, request.Content, request.ContactName,
            request.ContactEmail, request.OccurredAt);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class DeleteCommunicationCommandHandler(JobsDbContext db)
    : IRequestHandler<DeleteCommunicationCommand, Result>
{
    public async Task<Result> Handle(DeleteCommunicationCommand request, CancellationToken ct)
    {
        var c = await db.Communications.FirstOrDefaultAsync(
            x => x.Id == request.Id && x.ApplicationId == request.ApplicationId, ct);
        if (c is null) return Result.Failure(Error.NotFound("沟通记录"));

        c.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
