using RocketWiki.Core.Enums;

namespace RocketWiki.Importer.Cli;

internal static class CliArgumentParser
{
    public const string Usage = """
        RocketWiki.Importer — Confluence space migration tool (design.md §13)

        Dry run (safe — reads the export, converts everything, writes nothing):
          RocketWiki.Importer --export <path> --space-key <key> --dry-run [--report <path>]

        Real import (creates the space and pages for real):
          RocketWiki.Importer --export <path> --space-key <key>
                               --connection-string <sql-server-connection-string>
                               --attachments-root <directory>
                               --acting-user-id <existing-user-guid>
                               --importer-principal-id <sub-claim-value>
                               --grant-role <editor|space-admin>
                               --grant-expression <access-rule-expression-json>
                               [--local-instance-id <id>] [--report <path>]

        --grant-role must be editor or space-admin: the grant is also the importer's
        own write permission for the run, so a viewer grant would create the space and
        then refuse every page. --grant-expression must be a rule the importer
        principal itself satisfies, for the same reason; the importer runs in the
        "confluence-importer" group and carries no attributes, so an attr-based rule
        needs a principal that has that attribute. Both are checked before the space
        is created, and refused with an explanation rather than half-importing.

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

        if (!dryRun)
        {
            if (connectionString is null || attachmentsRoot is null || actingUserId is null
                || importerPrincipalId is null || grantRole is null || grantExpression is null)
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
            LocalInstanceId = localInstanceId ?? "standalone",
        };
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
