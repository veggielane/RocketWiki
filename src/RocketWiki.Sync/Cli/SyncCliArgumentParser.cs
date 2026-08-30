namespace RocketWiki.Sync.Cli;

internal static class SyncCliArgumentParser
{
    /// <summary>
    /// The environment variable the connection string is read from when neither
    /// <c>--connection-string</c> nor <c>--connection-string-file</c> is given. Same name
    /// the Helm migration Job uses, so one Secret key serves both.
    /// </summary>
    public const string ConnectionStringVariable = "ROCKETWIKI_CONNECTIONSTRING";

    public const string Usage = """
        RocketWiki.Sync — low→high bundle export/import CLI (design.md §12)

        THE DATABASE CONNECTION STRING
          Three sources, in this precedence:

            1. --connection-string <value>       (DISCOURAGED — see below)
            2. --connection-string-file <path>   read from a file, trimmed
            3. $ROCKETWIKI_CONNECTIONSTRING      the default when neither flag is given

          Prefer 2 or 3. A value passed as --connection-string is in this process's
          ARGV, which on every platform this runs on is readable by other local users
          (`ps`, /proc/<pid>/cmdline, Process Explorer) — and it lands in shell history
          and in crash dumps besides. Option 2 suits a Kubernetes/Docker secret mount;
          option 3 suits a scheduled job whose environment is already scoped. The flag
          is kept because scripted invocations exist, not because it is a good idea.

        Export (run on the LOW instance — drains the sync outbox into the next
        numbered bundle, or produces a one-time baseline snapshot):
          RocketWiki.Sync export [connection-string source, see above]
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
          RocketWiki.Sync import [connection-string source, see above]
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
        string? connectionStringFile = null;
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
                case "--connection-string-file":
                    connectionStringFile = TakeValue(args, ref i);
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

        connectionString = ResolveConnectionString(connectionString, connectionStringFile);
        if (connectionString is null || attachmentsRoot is null)
        {
            return null;
        }

        var complete = verb switch
        {
            SyncVerb.Export => outputDirectory is not null && instanceId is not null,
            SyncVerb.Import => bundlePath is not null && originInstanceId is not null && instanceId is not null,
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
