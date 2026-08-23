using RocketWiki.Sync.Cli;

// Exit codes: 0 = success (including "nothing pending to export" and idempotent
// re-delivery of an already-applied bundle - both are normal operational outcomes,
// design.md §12), 1 = usage/input error, 2 = refused (bundle gap, hash-chain mismatch,
// tampered payload, per-space sequence gap, or a --baseline target that isn't an
// exported native space). 2 is deliberately distinct from 1 so a scheduled job can
// page an operator on integrity refusals but not on a typo'd flag.
return await SyncCli.RunAsync(args);
