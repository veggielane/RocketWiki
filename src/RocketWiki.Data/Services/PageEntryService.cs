using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Data.Access;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed <see cref="IPageEntryService"/>. Lives in Data for the reason PageService
/// does: it needs the DbContext, and Core may not reference EF.
///
/// <para>The gate order is page-then-entry on every path, and both gates are real. The
/// page gate is the existing one, loaded through <see cref="PermissionContextLoader"/> so
/// space grants, the restriction chain and the page's own clearance check are inherited
/// rather than re-derived. The entry gate is <see cref="ClearanceGate.Check"/> against the
/// entry's own marking — the same function pages use, called once more per entry.</para>
/// </summary>
public sealed class PageEntryService(RocketWikiDbContext db, string localInstanceId) : IPageEntryService
{
    private readonly PermissionContextLoader _permissions = new(db);

    public async Task<ReadResult<IReadOnlyList<PageEntryView>>> ListAsync(
        Guid pageId, string collection, Principal principal, CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(pageId, principal, cancellationToken);
        if (gate.Failure is { } failure)
        {
            return failure switch
            {
                { IsNotFound: true } => new ReadResult<IReadOnlyList<PageEntryView>>.NotFound(),
                _ => new ReadResult<IReadOnlyList<PageEntryView>>.Denied(failure.Reason!),
            };
        }

        var normalized = NormalizeCollection(collection);
        var entries = await db.PageEntries.AsNoTracking()
            .Include(e => e.Countries)
            .Where(e => e.PageId == pageId && e.Collection == normalized)
            .OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);

        // Pruned, never counted. The caller is told what it may see and nothing whatever
        // about the rest: no total, no "n hidden", and the empty list a fully-pruned
        // collection returns is byte-identical to the one an empty collection returns.
        var visible = entries
            .Where(e => ClearanceGate.Check(e.ToMarking(), principal).IsAllowed)
            .Select(ToView)
            .ToList();

        return new ReadResult<IReadOnlyList<PageEntryView>>.Found(visible);
    }

    public async Task<ReadResult<PageEntryView>> GetAsync(
        Guid entryId, Principal principal, CancellationToken cancellationToken = default)
    {
        var entry = await db.PageEntries.AsNoTracking()
            .Include(e => e.Countries)
            .FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken);
        if (entry is null)
        {
            return new ReadResult<PageEntryView>.NotFound();
        }

        var gate = await GateAsync(entry.PageId, principal, cancellationToken);
        if (gate.Failure is { } failure)
        {
            return failure.IsNotFound
                ? new ReadResult<PageEntryView>.NotFound()
                : new ReadResult<PageEntryView>.Denied(failure.Reason!);
        }

        var clearance = ClearanceGate.Check(entry.ToMarking(), principal);
        return clearance.IsAllowed
            ? new ReadResult<PageEntryView>.Found(ToView(entry))
            : new ReadResult<PageEntryView>.Denied(clearance.DenialReason!);
    }

    public async Task<PageMutationResult<PageEntryView>> CreateAsync(
        CreatePageEntryRequest request, Principal principal, Guid actingUserId, AuditContext auditContext,
        CancellationToken cancellationToken = default)
    {
        var (page, space, error) = await LoadForWriteAsync(request.PageId, principal, cancellationToken);
        if (error is not null)
        {
            return PageMutationResult<PageEntryView>.Failure(error);
        }

        if (Validate(request.Collection, request.Data) is { } invalid)
        {
            return PageMutationResult<PageEntryView>.Failure(invalid);
        }

        var pageMarking = await _permissions.LoadMarkingAsync(page!.Id, cancellationToken);
        // Inheriting the page's marking is the ordinary case: a form submitter should not
        // have to reason about classification to file a record.
        var marking = request.Marking ?? pageMarking;
        if (CheckMarking(marking, pageMarking, principal) is { } refused)
        {
            return PageMutationResult<PageEntryView>.Failure(refused);
        }

        var now = DateTime.UtcNow;
        var entry = new PageEntry
        {
            PageId = page.Id,
            Collection = NormalizeCollection(request.Collection),
            Data = request.Data,
            Version = 1,
            Level = marking.Level,
            Prefix = marking.Prefix,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            UpdatedByUserId = actingUserId,
        };
        foreach (var country in marking.EyesOnly)
        {
            entry.Countries.Add(new PageEntryCountry { PageEntryId = entry.Id, CountryValue = country });
        }

        db.PageEntries.Add(entry);
        db.AuditContext = auditContext;
        db.RaiseDomainEvent(new PageEntryCreatedEvent(entry.Id, page.Id, space!.Id, space.Key, actingUserId, entry.Collection));

        await db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<PageEntryView>.Success(ToView(entry));
    }

    public async Task<PageMutationResult<PageEntryView>> UpdateAsync(
        UpdatePageEntryRequest request, Principal principal, Guid actingUserId, AuditContext auditContext,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadEntryForWriteAsync(request.EntryId, request.ExpectedVersion, principal, cancellationToken);
        if (loaded.Error is not null)
        {
            return PageMutationResult<PageEntryView>.Failure(loaded.Error);
        }

        var entry = loaded.Entry!;
        if (Validate(entry.Collection, request.Data) is { } invalid)
        {
            return PageMutationResult<PageEntryView>.Failure(invalid);
        }

        if (request.Marking is { } newMarking)
        {
            var pageMarking = await _permissions.LoadMarkingAsync(entry.PageId, cancellationToken);
            if (CheckMarking(newMarking, pageMarking, principal) is { } refused)
            {
                return PageMutationResult<PageEntryView>.Failure(refused);
            }

            entry.Level = newMarking.Level;
            entry.Prefix = newMarking.Prefix;
            // Replaced wholesale, like a page's country set: a partial update would let a
            // caller change the level without ever stating what caveat they meant.
            db.PageEntryCountries.RemoveRange(entry.Countries);
            entry.Countries.Clear();
            foreach (var country in newMarking.EyesOnly)
            {
                entry.Countries.Add(new PageEntryCountry { PageEntryId = entry.Id, CountryValue = country });
            }
        }

        entry.Data = request.Data;
        entry.Version++;
        entry.UpdatedAtUtc = DateTime.UtcNow;
        entry.UpdatedByUserId = actingUserId;

        db.AuditContext = auditContext;
        db.RaiseDomainEvent(new PageEntryUpdatedEvent(
            entry.Id, entry.PageId, loaded.Space!.Id, loaded.Space.Key, actingUserId, entry.Collection));

        await db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<PageEntryView>.Success(ToView(entry));
    }

    public async Task<PageMutationResult<PageEntryView>> DeleteAsync(
        DeletePageEntryRequest request, Principal principal, Guid actingUserId, AuditContext auditContext,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadEntryForWriteAsync(request.EntryId, request.ExpectedVersion, principal, cancellationToken);
        if (loaded.Error is not null)
        {
            return PageMutationResult<PageEntryView>.Failure(loaded.Error);
        }

        var entry = loaded.Entry!;
        entry.IsDeleted = true;
        entry.Version++;
        entry.UpdatedAtUtc = DateTime.UtcNow;
        entry.UpdatedByUserId = actingUserId;

        db.AuditContext = auditContext;
        db.RaiseDomainEvent(new PageEntryDeletedEvent(
            entry.Id, entry.PageId, loaded.Space!.Id, loaded.Space.Key, actingUserId, entry.Collection));

        await db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<PageEntryView>.Success(ToView(entry));
    }

    /// <summary>
    /// The two marking rules, together because they are one decision about one value.
    ///
    /// <para>Below the page is refused because the page is the container: a reader who
    /// cannot open the page never reaches the entry, so a lower marking is a claim that
    /// will eventually be believed by someone. Above your own clearance is refused because
    /// §21.6 already says you may not set a marking you could not then read, and an entry
    /// is no different — it would also let someone write a record and then be unable to
    /// correct it.</para>
    /// </summary>
    private static PageMutationError? CheckMarking(
        ProtectiveMarking marking, ProtectiveMarking pageMarking, Principal principal)
    {
        if (marking.Level < pageMarking.Level)
        {
            return new ValidationError(
                $"An entry cannot be marked below its page ({ProtectiveMarking.LevelToken(pageMarking.Level)}).");
        }

        var readable = ClearanceGate.Check(marking, principal);
        return readable.IsAllowed ? null : new ForbiddenError(readable.DenialReason!);
    }

    private static PageMutationError? Validate(string collection, string data)
    {
        var normalized = NormalizeCollection(collection);
        if (normalized.Length == 0 || normalized.Length > Configurations.PageEntryConfiguration.MaxCollectionLength)
        {
            return new ValidationError(
                $"A collection name must be 1-{Configurations.PageEntryConfiguration.MaxCollectionLength} characters.");
        }

        if (data.Length > Configurations.PageEntryConfiguration.MaxDataLength)
        {
            return new ValidationError(
                $"An entry is limited to {Configurations.PageEntryConfiguration.MaxDataLength} characters; use an attachment for anything larger.");
        }

        // An object at the root, not a bare scalar or array. Checked here rather than
        // trusted from the client because the storage contract is the server's to keep,
        // and every consumer downstream reads it as an object.
        try
        {
            if (JsonDocument.Parse(data).RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ValidationError("An entry must be a JSON object.");
            }
        }
        catch (JsonException)
        {
            return new ValidationError("An entry must be valid JSON.");
        }

        return null;
    }

    /// <summary>Lower-cased invariant. See PageEntry.Collection for why a lookup string
    /// that is not normalized behaves differently in production and in the test tier.</summary>
    private static string NormalizeCollection(string collection) =>
        collection.Trim().ToLowerInvariant();

    private async Task<(GateFailure? Failure, Page? Page, Space? Space)> GateAsync(
        Guid pageId, Principal principal, CancellationToken cancellationToken)
    {
        var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        if (page is null)
        {
            return (new GateFailure(true, null), null, null);
        }

        var space = await db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return (new GateFailure(true, null), null, null);
        }

        var context = await _permissions.LoadAsync(page, space.IsReplicaOf(localInstanceId), cancellationToken);
        var permission = context.Compute(principal);
        return permission.CanView
            ? (null, page, space)
            : (new GateFailure(false, permission.ViewDenialReason ?? "forbidden"), null, null);
    }

    private async Task<(Page? Page, Space? Space, PageMutationError? Error)> LoadForWriteAsync(
        Guid pageId, Principal principal, CancellationToken cancellationToken)
    {
        var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        if (page is null)
        {
            return (null, null, new NotFoundError(pageId));
        }

        var space = await db.Spaces.FirstOrDefaultAsync(s => s.Id == page.SpaceId, cancellationToken);
        if (space is null)
        {
            return (null, null, new NotFoundError(page.SpaceId));
        }

        if (space.IsReplicaOf(localInstanceId))
        {
            return (null, null, new ReadOnlyReplicaError(space.Id, space.OriginInstanceId));
        }

        var context = await _permissions.LoadAsync(page, isReplicaSpace: false, cancellationToken);
        var permission = context.Compute(principal);
        return permission.CanEdit
            ? (page, space, null)
            : (null, null, new ForbiddenError(permission.EditDenialReason ?? "forbidden"));
    }

    private async Task<(PageEntry? Entry, Space? Space, PageMutationError? Error)> LoadEntryForWriteAsync(
        Guid entryId, int expectedVersion, Principal principal, CancellationToken cancellationToken)
    {
        // A tombstoned entry is never found here, and that is load-bearing rather than
        // incidental: PageEntryConfiguration's global query filter (!IsDeleted) is what
        // stops an update or a second delete from resurrecting one. The guard lives in the
        // model rather than in a check on this line on purpose - it then holds for every
        // query in the service, present and future - but it is worth knowing that this
        // method depends on it.
        var entry = await db.PageEntries
            .Include(e => e.Countries)
            .FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken);
        if (entry is null)
        {
            return (null, null, new NotFoundError(entryId));
        }

        var (page, space, error) = await LoadForWriteAsync(entry.PageId, principal, cancellationToken);
        if (error is not null)
        {
            return (null, null, error);
        }

        // Clearance BEFORE the version check, so a caller who may not read this entry
        // cannot learn its version by comparing which refusal they get.
        var clearance = ClearanceGate.Check(entry.ToMarking(), principal);
        if (!clearance.IsAllowed)
        {
            return (null, null, new NotFoundError(entryId));
        }

        if (entry.Version != expectedVersion)
        {
            return (null, null, new StaleRevisionError(expectedVersion, entry.Version, null, null));
        }

        _ = page;
        return (entry, space, null);
    }

    private static PageEntryView ToView(PageEntry entry) => new(
        entry.Id, entry.PageId, entry.Collection, entry.Data, entry.Version,
        entry.ToMarking(), entry.CreatedAtUtc, entry.UpdatedAtUtc, entry.UpdatedByUserId);

    private sealed record GateFailure(bool IsNotFound, string? Reason);
}
