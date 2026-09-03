using RocketWiki.Core.Enums;

namespace RocketWiki.Importer.Cli;

internal static class CliArgumentParser
{
    /// <summary>
    /// The environment variable the connection string is read from when neither
    /// <c>--connection-string</c> nor <c>--connection-string-file</c> is given. Same name
    /// RocketWiki.Sync and the Helm migration Job use, so one Secret key serves all three.
    /// </summary>
    public const string ConnectionStringVariable = "ROCKETWIKI_CONNECTIONSTRING";

    public const string Usage = """
        RocketWiki.Importer — Confluence space migration tool (design.md §13)

        THE DATABASE CONNECTION STRING
          Three sources, in this precedence:

            1. --connection-string <value>       (DISCOURAGED — see below)
            2. --connection-string-file <path>   read from a file, trimmed
            3. $ROCKETWIKI_CONNECTIONSTRING      the default when neither flag is given

          Prefer 2 or 3. A value passed as --connection-string is in this process's
          ARGV, which on every platform this runs on is readable by other local users
          (`ps`, /proc/<pid>/cmdline, Process Explorer) — and it lands in shell history
          and in crash dumps besides. The flag is kept because scripted invocations
          exist, not because it is a good idea.

        Dry run (safe — reads the export, converts everything, writes nothing):
          RocketWiki.Importer --export <path> --space-key <key> --dry-run [--report <path>]

        Real import (creates the space and pages for real):
          RocketWiki.Importer --export <path> --space-key <key>
                               [connection-string source, see above]
                               --attachments-root <directory>
                               --acting-user-id <existing-user-guid>
                               --importer-principal-id <sub-claim-value>
                               --grant-role <space-admin>
                               --grant-expression <access-rule-expression-json>
                               --local-instance-id <id>
                               [--report <path>]

        --local-instance-id is REQUIRED for a real import and must match the API's
        configured Instance:Id (design.md §12). It used to default to "standalone",
        which is the one value that cannot be right anywhere but a test: a space's
        OriginInstanceId is what marks it native or replica, so an import run without
        the flag against an API configured with any other identity produces spaces
        that are permanently READ-ONLY replicas of an instance that does not exist.
        A dry run needs none — it writes nothing.

        --grant-role must be space-admin: a space is born with a space-admin role grant
        (design.md §6.5.1), and the grant is also the importer's own write permission
        for the run. The importer pairs it with an access grant for the same subjects,
        since a role confers no visibility (§6.4). --grant-expression must be a rule
        the importer principal itself satisfies, for the same reason; the importer
        runs in the "confluence-importer" group and carries no attributes, so an
        attr-based rule needs a principal that has that attribute. Both are checked
        before the space is created, and refused with an explanation rather than
        half-importing.

        --space-key must match the export's own space key exactly — a safety check
        against pointing the tool at the wrong export file.

        --report defaults to "<export-file>-import-report.txt" if not given.

        Run this in --dry-run mode first (design.md §16): it exercises the exact same
        conversion and page-tree logic as a real import, with zero side effects and no
        database required, so link resolution, unsupported macros, and lossy content
        surface in the report before anything is written for real.
        """;

    public static CliOptions? Parse(string[] args)
    {
        string? exportPath = null;
        string? spaceKey = null;
        string? reportPath = null;
        string? connectionString = null;
        string? connectionStringFile = null;
        string? attachmentsRoot = null;
        string? importerPrincipalId = null;
        string? grantExpression = null;
        string? localInstanceId = null;
        Guid? actingUserId = null;
        SpaceRole? grantRole = null;
        var dryRun = false;

        var i = 0;
        while (i < args.Length)
        {
            var flag = args[i];
            switch (flag)
            {
                case "--dry-run":
                    dryRun = true;
                    i++;
                    break;
                case "--export":
                    exportPath = TakeValue(args, ref i);
                    break;
                case "--space-key":
                    spaceKey = TakeValue(args, ref i);
                    break;
                case "--report":
                    reportPath = TakeValue(args, ref i);
                    break;
                case "--connection-string":
                    connectionString = TakeValue(args, ref i);
                    break;
                case "--connection-string-file":
                    connectionStringFile = TakeValue(args, ref i);
                    break;
                case "--attachments-root":
                    attachmentsRoot = TakeValue(args, ref i);
                    break;
                case "--importer-principal-id":
                    importerPrincipalId = TakeValue(args, ref i);
                    break;
                case "--grant-expression":
                    grantExpression = TakeValue(args, ref i);
                    break;
                case "--local-instance-id":
                    localInstanceId = TakeValue(args, ref i);
                    break;
                case "--acting-user-id":
                {
                    var raw = TakeValue(args, ref i);
                    if (raw is null || !Guid.TryParse(raw, out var parsed))
                    {
                        return null;
                    }

                    actingUserId = parsed;
                    break;
                }

                case "--grant-role":
                {
                    var raw = TakeValue(args, ref i);
                    if (raw is null || !Enum.TryParse<SpaceRole>(raw, ignoreCase: true, out var parsed))
                    {
                        return null;
                    }

                    grantRole = parsed;
                    break;
                }

                default:
                    return null; // unrecognized flag - fail closed to a usage message, not a guess
            }
        }

        if (exportPath is null || spaceKey is null)
        {
            return null;
        }

        reportPath ??= exportPath + "-import-report.txt";

        connectionString = ResolveConnectionString(connectionString, connectionStringFile);

        if (!dryRun)
        {
            // --local-instance-id is REQUIRED on a real run, and used to default to
            // "standalone". design.md §12: a space's OriginInstanceId is what
            // Space.IsReplicaOf compares against the API's own configured Instance:Id, so a
            // real import run without the flag stamped every imported space as native to
            // the literal string "standalone" — and if the API is configured with any other
            // identity, every one of those spaces is a permanent read-only REPLICA of an
            // instance that does not exist. Unpickable afterwards without a data fix.
            //
            // The Helm chart closed exactly this default for api.instanceId (schema
            // minLength 1, required) after it produced the mirror-image bug; the importer
            // kept it. A warning would not do: the damage is silent, permanent, and nobody
            // reads warnings in a batch log. A dry run still needs none — it writes nothing.
            if (connectionString is null || attachmentsRoot is null || actingUserId is null
                || importerPrincipalId is null || grantRole is null || grantExpression is null
                || localInstanceId is null)
            {
                return null;
            }
        }

        return new CliOptions
        {
            ExportPath = exportPath,
            ExpectedSpaceKey = spaceKey,
            ReportPath = reportPath,
            DryRun = dryRun,
            ConnectionString = connectionString,
            AttachmentsRoot = attachmentsRoot,
            ActingUserId = actingUserId,
            ImporterPrincipalUserId = importerPrincipalId,
            InitialGrantRole = grantRole,
            InitialGrantExpressionJson = grantExpression,
            // A dry run writes nothing, so it needs no identity; the value is never read.
            LocalInstanceId = localInstanceId ?? "standalone",
        };
    }

    /// <summary>
    /// The connection string, from whichever of the three sources supplied one. See
    /// <see cref="Usage"/> for why argv is the discouraged one.
    ///
    /// <para>Precedence is explicit rather than merged: an operator who passes the flag
    /// gets exactly what they passed, and nothing silently overrides it. A file that does
    /// not exist or is empty resolves to null, which lands in the usage message — better
    /// than a half-configured run that connects somewhere unintended.</para>
    /// </summary>
    private static string? ResolveConnectionString(string? fromArgv, string? filePath)
    {
        if (fromArgv is not null)
        {
            return fromArgv;
        }

        if (filePath is not null)
        {
            // Trimmed: a secret file written by an editor or a k8s projection almost
            // always carries a trailing newline, and a connection string with one appended
            // fails in a way that names neither the file nor the newline.
            var fromFile = File.Exists(filePath) ? File.ReadAllText(filePath).Trim() : null;
            return string.IsNullOrEmpty(fromFile) ? null : fromFile;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment;
    }

    private static string? TakeValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            i = args.Length;
            return null;
        }

        var value = args[i + 1];
        i += 2;
        return value;
    }
}
