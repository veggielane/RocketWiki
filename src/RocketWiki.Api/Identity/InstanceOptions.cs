using System.ComponentModel.DataAnnotations;

namespace RocketWiki.Api.Identity;

/// <summary>
/// The <c>Instance</c> configuration section (design.md §12): this deployment's sync
/// identity. Bound in Program.cs like every other options family and <b>validated at
/// startup</b> — an empty id would make every space's <c>OriginInstanceId</c> compare
/// against nothing, which is the silent fail-OPEN the Helm chart's values schema exists
/// to refuse (a blank id on a low/high pair makes every mirrored space native and
/// therefore writable); refusing it here covers Compose, <c>aspire run</c> and a
/// hand-run binary too.
///
/// <para><b>Resolved lazily, never captured off the builder.</b> The first version of
/// Program.cs read <c>Instance:Id</c> at the top of the file and closed over the string in
/// a dozen service factories and the DbContext options callback — the same eager-read
/// shape the selector catalog had before it moved (see ProtectiveMarkingConfiguration's
/// doc for the test-host consequence). Every consumer now goes through the
/// <see cref="InstanceIdentity"/> singleton, itself built from
/// <c>IOptions&lt;InstanceOptions&gt;</c>, and the DbContext options are stamped through
/// EF's provider-aware <c>ConfigureDbContext</c> hook, which takes a service provider. One
/// definition, one read moment, and a test host's own <c>Instance:Id</c> is the one the
/// host runs with.</para>
/// </summary>
public sealed class InstanceOptions
{
    public const string SectionName = "Instance";

    /// <summary>The default for the single-instance deployment, which is the common case
    /// and should not need a value to run. Anything taking part in sync must set its own.</summary>
    public const string DefaultId = "standalone";

    [Required(AllowEmptyStrings = false, ErrorMessage = "Instance:Id must be a non-empty instance identifier (design.md §12).")]
    public string Id { get; set; } = DefaultId;
}
