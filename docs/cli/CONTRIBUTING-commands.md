# Adding an agent command

1. Read the final plug-and-play decisions in `design.md` and its capability-map row.
2. Edit only `src/LuaTools.Agent/Catalogue/<resource>.json` for the descriptor.
   New resource files are embedded and merged automatically in both executables.
   Keep resource/verb exact; include Action-based purpose, capability IDs, syntax,
   typed flags with defaults/allowed values/required, side effects, reversibility,
   preconditions, output fields, two runnable examples, and see-also commands.
3. Add `Services/Agent/<Resource>Resource.cs` as a partial `AgentCommandService`.
   Annotate one `(AgentRequest, CancellationToken) -> Task<Envelope>` method with
   `[AgentResource("resource")]`. The small reflection registry discovers it;
   do not edit the registry or add a switch branch elsewhere.
4. Reuse existing DI services; never call plugin HTTP routes, resolve arbitrary
   services, or duplicate installers, quarantine, auth, or the download scheduler.
5. Use `Snapshot` for brief dispatcher reads/admission; await I/O off the dispatcher.
6. Route every mutation through `_mutations.ExecuteAsync(request, prepare, ct)`.
   Preparation must be side-effect-free. Return `PreparedMutation` containing a
   `MutationPlan` and a short commit delegate. Include target versions, selected
   Steam/owner identity, all impacts, consent/restart requirements, and live gates.
   Revalidate versions/gates inside commit immediately before service actions.
7. Use `BulkSelection.Select` for ids/where and `BulkSelection.Data` for JSON.
   The client resolves explicit `@file`, stdin JSON and `--ids-from`; the host
   validates JSON again. Reject unknown fields. Never open arbitrary client paths.
8. Commit must return quickly: enqueue existing `DownloadJob`s and return their
   actual `DownloadItem.Id`. Register nonqueue asynchronous service operations
   with `Jobs.Register`; pass host lifetime, never connection lifetime. This wraps
   operations; it adds no scheduler. Unsupported cancellation stays disabled.
9. Queue confirmations use `ConsentCoordinator.WaitAsync`/`Decide`, also usable
   by GUI callers. Executable settings use `ExecutableConsentCoordinator.Set`.
   Quarantine/digest checks and live gate revocation still apply inside services.
10. Expose `AvailableActions.For` with every applicable live disabled reason.
    Return per-item results; partial success uses `PARTIAL_FAILURE`, exit 1.
    Put plans/results in structured error details. Never expose secrets or
    journal payloads: only digests, IDs and secret-free acceptance receipts.
11. Localize new GUI/host messages in both RESX files and their Designer accessor.
12. Update the map status only when the command works. Add service and real-pipe
    tests for reads, dry-run, consent, read-only, stale plans, versions, gates,
    idempotency and partial failure. The portable suite validates map/catalogue.
13. Run `scripts/verify --ci` on the feature branch. Never push security-hardened.
