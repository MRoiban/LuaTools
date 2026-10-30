# LuaTools agent guide
LuaTools is a Windows desktop app that manages game Lua scripts, Steam unlocker modes, plugins and game fixes.
Games have Steam App IDs; sources supply scripts, depots describe builds, presets save variants, and jobs track work in the download queue.

Start with `luatools doctor --json`, then `luatools commands --json` and `luatools describe game --json`.
The client starts the installed app minimized when needed; use --no-start to prevent that.
Commands use `luatools resource verb identity --flag value`; discover exact schemas with describe and examples with --help.
JSON output is one envelope: ok, data, page, warnings and error; errors also go to stderr.
Redirected output defaults to JSON, except help and guide, which stay readable unless --json is explicit.
IDs are stable; names are display text. Fix IDs require their provider and App ID; preset IDs are hashes.
Use --fields with names from describe and --limit to bound lists; follow page.next_cursor with --cursor.

## Recipes
Inspect a game and its sources before adding it:
```sh
luatools game get 570 --json
luatools source list --appid 570 --json
luatools game add 570 --source auto --dry-run --json
luatools game add 570 --source auto --yes --json
```
Diagnose a failed download; doctor includes redacted failure text:
```sh
luatools doctor --json
luatools download list --json
luatools activity-log list --severity warn --limit 20 --json
```
Repair a broken setup; inspect skipped repairs and follow their explicit commands:
```sh
luatools doctor --fix --dry-run --json
luatools doctor --fix --yes --json
```
Find and inspect a game fix before selecting its provider-specific ID:
```sh
luatools fix list --appid 570 --limit 10 --json
luatools fix apply --help
```
Review and apply a fix using the ID/provider returned by fix list; replace the example ID first:
```sh
luatools fix apply example-fix --appid 570 --provider luatools --dry-run --json
luatools fix apply example-fix --appid 570 --provider luatools --yes --json
```
Check for signed updates, then inspect the application before planning an update:
```sh
luatools update get --json
luatools update check --dry-run --json
luatools update check --yes --json
luatools update apply --dry-run --json
```
Inspect Steam and plan automatic folder detection:
```sh
luatools steam get --json
luatools steam update --auto --dry-run --json
luatools steam update --auto --yes --json
```

## Rules of engagement and gotchas
Dry-run first for mutations. --dry-run validates and plans without committing; unsupported planning exits 5.
--yes consents to the submitted effects; it does not bypass permissions, quarantine or path checks.
Stop on exit 5: inspect the disabled reason and use describe; do not retry around a gate.
Steam restarts need --restart-steam as well as --yes except the explicit steam restart verb.
Doctor --fix never enables executable workflows, spends download quota, overwrites game data or applies app updates.
It reports repairs requiring extra authority as skipped; invoke the exact suggested command after reviewing its dry-run.
Discord login requires a human browser step. Public sources work as a guest.
Secrets go through --secret-stdin or --code-stdin, never command arguments or logs.
Jobs do not survive app exit. Keep the app running and inspect job results before shutting it down.
Quota is spent before overwrite is known: inspect sources and plans before accepting acquisition.
A timeout after acceptance does not cancel work; inspect the returned job ID before retrying.
Use --idempotency-key for mutation retries; exit 6 means state or context changed and needs a fresh plan.

## Command reference (generated from catalogue)
