using RocketWiki.Importer.Cli;

// Exit codes: 0 = clean (no review needed), 1 = usage/input error, 2 = import blocked
// before anything was written, 3 = completed but ImportReport.NeedsReview is true — a
// distinct code so scripts/CI can tell "ran fine, but read the report" apart from both
// "broken" and "nothing to look at".
return await ImporterCli.RunAsync(args);
