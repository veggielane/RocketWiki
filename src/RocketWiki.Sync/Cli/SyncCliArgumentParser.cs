namespace RocketWiki.Sync.Cli;

internal static class SyncCliArgumentParser
{
    public const string Usage = """
        RocketWiki.Sync — low→high bundle export/import CLI (design.md §12)

        Export (run on the LOW instance — drains the sync outbox into the next
        numbered bundle, or produces a one-time baseline snapshot):
          RocketWiki.Sync export --connection-string <sql-server-connection-string>
                                 --output <bundle-directory>
                                 --instance-id <this-instance's-Instance:Id>
                                 --attachments-root <file-storage-root>
                                 [--baseline <space-key>]

          --baseline <space-key>: full current-state snapshot bundle for a NEWLY
          exported space. Run exactly once per space, right after flagging it
          exported and before relying on incremental drains (design.md §12: "a newly
          exported space first produces a baseline bundle, then incrementals").
          Refused unless the space is flagged exported AND native to this instance —
          a replica must never emit sync content it doesn't own.

        Import (run on the HIGH instance — verifies the manifest hash chain and
        applies bundles strictly in order, refusing gaps; re-delivery is a no-op):
          RocketWiki.Sync import --connection-string <sql-server-connection-string>
                                 --bundle <bundle-file-or-directory>
                                 --origin-instance-id <the-low-instance's-id>
                                 --attachments-root <file-storage-root>

          When --bundle is a directory, every bundle-*.zip in it is applied in
          bundle-number order, stopping loudly on the first refusal.

        --attachments-root is the instance's FileSystem storage root: export reads
        attachment bytes from it into bundles; import writes arriving blobs to it.

        Exit codes: 0 success / nothing to do, 1 usage or input error, 2 refused
        (gap, chain mismatch, tampered payload, or invalid --baseline target).
        """;

    public static SyncCliOptions? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        SyncVerb verb;
        switch (args[0])
        {
            case "export":
                verb = SyncVerb.Export;
                break;
            case "import":
                verb = SyncVerb.Import;
                break;
            default:
                return null;
        }

        string? connectionString = null;
        string? attachmentsRoot = null;
        string? outputDirectory = null;
        string? instanceId = null;
        string? baselineSpaceKey = null;
        string? bundlePath = null;
        string? originInstanceId = null;

        var i = 1;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--connection-string":
                    connectionString = TakeValue(args, ref i);
                    break;
                case "--attachments-root":
                    attachmentsRoot = TakeValue(args, ref i);
                    break;
                case "--output":
                    outputDirectory = TakeValue(args, ref i);
                    break;
                case "--instance-id":
                    instanceId = TakeValue(args, ref i);
                    break;
                case "--baseline":
                    baselineSpaceKey = TakeValue(args, ref i);
                    break;
                case "--bundle":
                    bundlePath = TakeValue(args, ref i);
                    break;
                case "--origin-instance-id":
                    originInstanceId = TakeValue(args, ref i);
                    break;
                default:
                    return null; // unrecognized flag - fail closed to the usage message, not a guess
            }
        }

        if (connectionString is null || attachmentsRoot is null)
        {
            return null;
        }

        var complete = verb switch
        {
            SyncVerb.Export => outputDirectory is not null && instanceId is not null,
            SyncVerb.Import => bundlePath is not null && originInstanceId is not null,
            _ => false,
        };

        if (!complete)
        {
            return null;
        }

        return new SyncCliOptions
        {
            Verb = verb,
            ConnectionString = connectionString,
            AttachmentsRoot = attachmentsRoot,
            OutputDirectory = outputDirectory,
            InstanceId = instanceId,
            BaselineSpaceKey = baselineSpaceKey,
            BundlePath = bundlePath,
            OriginInstanceId = originInstanceId,
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
