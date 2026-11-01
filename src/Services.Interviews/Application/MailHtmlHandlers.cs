using System.Text.Json.Nodes;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;

namespace YourInterview.Services.Interviews.Application;

/// <summary>票 309 §6：GET /mailbox/assets/{assetId}/html[?images=1]。</summary>
public sealed record GetMailHtmlQuery(Guid UserId, Guid AssetId, bool ImagesAllowed)
    : IRequest<Result<MailHtmlContent>>;

/// <summary>serve 后 HTML + 是否拦截了远端图（→ X-Images-Blocked）。</summary>
public sealed record MailHtmlContent(string Html, bool ImagesBlocked);

public sealed class GetMailHtmlQueryHandler(
    InterviewsDbContext db,
    IMailboxProviderFactory providers,
    MailboxProtection protection,
    MailboxOAuthResolver oauth,
    IOptions<MailboxOptions> options)
    : IRequestHandler<GetMailHtmlQuery, Result<MailHtmlContent>>
{
    public async Task<Result<MailHtmlContent>> Handle(GetMailHtmlQuery request, CancellationToken ct)
    {
        var asset = await LoadOwnedAssetAsync(request.UserId, request.AssetId, ct);
        if (asset is null) return Result.Failure<MailHtmlContent>(Error.NotFound("材料"));
        var opts = options.Value;
        if (asset.HtmlBody is not null)
            return Serve(asset, request.ImagesAllowed, opts);

        // 懒回填（§7）：HtmlBody 为 NULL 且有 provider message id 且无墓碑才取。
        // 非字符串 message id / 非布尔墓碑值按缺失处理（脏 meta 不致 500）。
        var meta = ParseMeta(asset.MetaJson);
        string? providerMessageId = null;
        if (meta?.TryGetPropertyValue("mailboxMessageId", out var mid) is true
            && mid is JsonValue midValue && midValue.TryGetValue<string>(out var midStr))
            providerMessageId = midStr;
        var tombstone = meta?.TryGetPropertyValue("htmlBackfillUnavailable", out var ts) is true
            && ts is JsonValue tsValue && tsValue.TryGetValue<bool>(out var tsBool) && tsBool;
        if (tombstone || string.IsNullOrWhiteSpace(providerMessageId))
            return Result.Failure<MailHtmlContent>(Error.NotFound("材料"));

        MailboxConnection? conn;
        IMailboxProvider? provider;
        string? access;
        try
        {
            (conn, provider, access) = await MailboxImportHelper.GetActiveAccessAsync(
                db, providers, protection, oauth, request.UserId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is MailboxAuthException or MailboxRateLimitedException
            or MailboxProviderException or HttpRequestException)
        {
            // 取 token 阶段的瞬时失败：不写墓碑、不存任何东西，下次展开重试。
            return Result.Failure<MailHtmlContent>(Error.NotFound("材料"));
        }
        if (conn is null || provider is null || access is null)
            return Result.Failure<MailHtmlContent>(Error.NotFound("材料"));

        MailboxMessage? message;
        try
        {
            message = provider is IIncrementalMailboxProvider inc
                ? await inc.GetMessageFullAsync(access, providerMessageId, ct)
                : await provider.GetMessageAsync(access, providerMessageId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is MailboxAuthException or MailboxRateLimitedException
            or MailboxProviderException or HttpRequestException)
        {
            // 取信阶段的瞬时失败：同上，不写墓碑、不存任何东西。
            return Result.Failure<MailHtmlContent>(Error.NotFound("材料"));
        }
        if (message is null)
        {
            // provider 404 = 邮件已删：立墓碑（此后 /html 直接 404，不再重取），
            // 纯文本转写稿不受影响（零数据丢失）。脏 meta 立不了碑时不覆盖原 meta，
            // 本次只 404（下次展开重试取信，代价是一次额外取信，不丢数据）。
            if (SetTombstone(asset)) await db.SaveChangesAsync(ct);
            return Result.Failure<MailHtmlContent>(Error.NotFound("材料"));
        }

        var html = await MailboxImportHelper.ApplyBackfillAsync(
            provider, access, opts, asset, message, ct);
        await db.SaveChangesAsync(ct);
        if (html is null) return Result.Failure<MailHtmlContent>(Error.NotFound("材料"));
        return Serve(asset, request.ImagesAllowed, opts);
    }

    private Result<MailHtmlContent> Serve(InterviewAsset asset, bool imagesAllowed, MailboxOptions opts)
    {
        var cidMap = MailHtmlSanitizer.ParseCidMap(asset.MetaJson);
        var (html, blocked) = MailHtmlServePass.Apply(
            asset.HtmlBody!, asset.Id, cidMap, imagesAllowed, opts);
        return Result.Success(new MailHtmlContent(html, blocked));
    }

    private async Task<InterviewAsset?> LoadOwnedAssetAsync(Guid userId, Guid assetId, CancellationToken ct)
    {
        var asset = await db.Assets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null) return null;
        // 资产级端点：归属在方法内校验（与 transcript 的 entry 级校验同款口径）；
        // 未拥有 → 404，绝不 403（不泄露存在性）。
        var owned = await db.Entries.IgnoreQueryFilters()
            .AnyAsync(e => e.Id == asset.InterviewEntryId && e.UserId == userId, ct);
        return owned ? asset : null;
    }

    private static JsonObject? ParseMeta(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson)) return null;
        try { return JsonNode.Parse(metaJson) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <returns>墓碑是否写盘（脏 meta 返回 false，调用方不 SaveChanges）。</returns>
    private static bool SetTombstone(InterviewAsset asset)
    {
        JsonObject? meta;
        if (string.IsNullOrWhiteSpace(asset.MetaJson)) meta = new JsonObject();
        else meta = ParseMeta(asset.MetaJson);
        if (meta is null) return false;
        meta["htmlBackfillUnavailable"] = true;
        asset.SetMeta(meta.ToJsonString());
        return true;
    }
}

/// <summary>票 309 §6：GET /mailbox/assets/{assetId}/inline/{cid}（仅内联图片）。</summary>
public sealed record GetMailInlineImageQuery(Guid UserId, Guid AssetId, string Cid)
    : IRequest<Result<MailInlineImage>>;

public sealed record MailInlineImage(byte[] Bytes, string ContentType);

public sealed class GetMailInlineImageQueryHandler(
    InterviewsDbContext db,
    IOptions<MailboxOptions> options)
    : IRequestHandler<GetMailInlineImageQuery, Result<MailInlineImage>>
{
    public async Task<Result<MailInlineImage>> Handle(GetMailInlineImageQuery request,
        CancellationToken ct)
    {
        var opts = options.Value;
        var asset = await db.Assets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == request.AssetId, ct);
        // check 1：存在 + 归属（未拥有 → 404，绝不 403）。
        if (asset is null) return Result.Failure<MailInlineImage>(Error.NotFound("材料"));
        var owned = await db.Entries.IgnoreQueryFilters()
            .AnyAsync(e => e.Id == asset.InterviewEntryId && e.UserId == request.UserId, ct);
        if (!owned) return Result.Failure<MailInlineImage>(Error.NotFound("材料"));

        // check 2：归一化 CID 必须在该资产自己的 cidMap 里。
        var cid = MailHtmlSanitizer.NormalizeCid(request.Cid);
        var cidMap = MailHtmlSanitizer.ParseCidMap(asset.MetaJson);
        if (cid is null || !cidMap.TryGetValue(cid, out var entry))
            return Result.Failure<MailInlineImage>(Error.NotFound("材料"));

        // check 3：内容类型白名单（非图片、SVG → 404；顺序在 check 5 之前，
        // 故缺失文件的非图片 part 仍 404，不走占位回落）。
        if (!MailHtmlServePass.IsInlineContentType(entry.ContentType))
            return Result.Failure<MailInlineImage>(Error.NotFound("材料"));

        // check 4：路径 containment（逃逸 → 404；顺序同样在 check 5 之前）。
        var full = MailHtmlServePass.ResolveInlinePath(opts, entry.File);
        if (full is null) return Result.Failure<MailInlineImage>(Error.NotFound("材料"));

        // check 5：文件缺失/不可读 → 占位图字节 200（绝不 404 破图）。
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(full, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return Result.Success(new MailInlineImage(MailHtmlSanitizer.PlaceholderBytes, "image/png"));
        }
        return Result.Success(new MailInlineImage(bytes, entry.ContentType));
    }
}
