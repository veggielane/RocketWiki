using RocketWiki.Core.Access;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;

namespace RocketWiki.Importer.Pipeline;

/// <summary>
/// Inputs the pipeline cannot derive on its own — same reasoning as every
/// <c>RocketWiki.Core.Services</c> interface: <c>ActingUserId</c> is the caller's
/// already-resolved local <c>User.Id</c> (JIT provisioning from a token is transport
/// middleware's job, and the importer is not a transport), and <c>AuditContext</c> is
/// per-request metadata, not a domain fact.
/// </summary>
/// <param name="ImporterPrincipal">The principal presented to every service call for ABAC evaluation.</param>
/// <param name="ActingUserId">
/// An existing RocketWiki <c>User.Id</c> to record as the author of every imported
/// revision, attachment upload, and comment. There is no shadow-user creation service
/// today (unlike design.md §12's low/high sync, which has one) to map individual
/// Confluence authors to distinct RocketWiki users — every imported item is attributed to
/// this single acting identity. Per-item original authorship (email/display name) is
/// still captured and surfaced in <see cref="ImportReport"/> so it is not lost, just not
/// yet applied to <c>AuthorUserId</c>.
/// </param>
/// <param name="InitialSpaceGrant">
/// The role grant the space is created with (design.md §6.5.1: atomic with space creation,
/// paired by the importer with an access grant for the same subjects, and
/// deliberately not defaulted here either — see <see cref="RocketWiki.Core.Services.InitialGrant"/>'s
/// own doc comment). An imported space starting open to "everyone" by accident, for
/// content that may have been export-controlled under Confluence's own permissions, is
/// exactly the kind of silent default this project's ABAC model exists to prevent.
/// Confluence's own space/page permissions are <b>not</b> translated by this importer —
/// that mapping (classic per-space roles → ABAC rule expressions) is a separate design
/// question this pipeline does not attempt to answer; it only requires the caller to
/// decide deliberately.
/// </param>
public sealed record ImportOptions(
    Principal ImporterPrincipal,
    Guid ActingUserId,
    AuditContext AuditContext,
    InitialGrant InitialSpaceGrant);
