# LuaTools agent guide
LuaTools is a Windows desktop app that manages game Lua scripts, Steam unlocker modes, plugins and game fixes.
Games have Steam App IDs; sources supply scripts, depots describe builds, presets save variants, and jobs track work in the download queue.

Start with `luatools doctor --json`, then `luatools commands --brief --json` and `luatools describe game --json`.
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
luatools history list --kind download --status failed --limit 1 --json
luatools download list --json
luatools activity-log list --since 3h --severity warn --limit 20 --json
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
`job wait` returns on a terminal state or when a decision is needed. Inspect state/result, resolve awaiting_confirmation with `job confirm`, then wait again. Jobs do not survive app exit. Keep the app running and inspect job results before shutting it down.
Quota is spent before overwrite is known: inspect sources and plans before accepting acquisition.
A timeout after acceptance does not cancel work; inspect the returned job ID before retrying.
Use --idempotency-key for mutation retries; exit 6 means state or context changed and needs a fresh plan.

## Added games and Steam installations
A game is added when it has a live Lua. Steam installation is separate: game list/get report added and installed independently.
`game list` lists added games, including uninstalled ones. `game search --added --query harvest` searches this added library; --installed remains a legacy alias for --added. Search without either flag searches the remote catalogue. Search supports --limit, --cursor, --all and --count like other lists.
Activity severity is a minimum threshold: warn includes warning, error and critical. --since accepts UTC RFC3339 or positive relative durations up to 24h.
History is newest first; `history list --status failed --limit 1` returns the last failure's `message`, not merely its state.

## Multi-step recipes
Find a game by name, then list provider-specific fixes. Take `appid` from the search result; Elden Ring is App ID 1245620:
```sh
luatools fix search --query Elden --json
luatools fix list --appid 1245620 --json
```
Take `id` and `provider` from fix list for fix apply; never use the display title as an ID.
Save the current Lua, then restore it later. Take `hash` from create or list; replace HASH below with that exact returned value. The preset must be inactive before apply:
```sh
luatools preset create --appid 730 --from-live --name before-patch --dry-run --json
luatools preset create --appid 730 --from-live --name before-patch --yes --json
luatools preset list --appid 730 --json
luatools preset apply HASH --appid 730 --dry-run --json
luatools preset apply HASH --appid 730 --yes --json
```
Inspect `depot plan` before choosing a strict language/OS selection. Defaults include eligible neutral, English and selected-language content, honor matching active pins, exclude shared depots, and fall back to eligible depots if no language matches. These defaults do not promise exclusive language content; inspect the plan and pass explicit --depot IDs for a strict selection. Then download with the same language/OS options:
```sh
luatools depot plan 730 --language english --os windows --json
luatools depot download 730 --language english --os windows --output D:\Depots --dry-run --json
luatools depot download 730 --language english --os windows --output D:\Depots --yes --json
```
Repair drift only for game 730. --ids and --where intersect the drifted selection:
```sh
luatools launch-option list --drifted --json
luatools launch-option apply --drifted --ids 730 --restart-steam --dry-run --json
luatools launch-option apply --drifted --ids 730 --restart-steam --yes --json
```
Local --file, --diff-file, --data @path and --ids-from inputs are checked before contacting LuaTools. Missing/unreadable inputs exit 2; correct the named path and retry. With valid input, an absent app exits 9 if it cannot be started.

## Command reference (generated from catalogue)
- account: luatools account get; luatools account login --browser; luatools account logout
- activity-log: luatools activity-log list [--since <UTC-or-duration>] [--severity <level>] [--category <name>]; luatools activity-log watch [--cursor <opaque>]
- application: luatools application start; luatools application restart; luatools application exit
- cabinet-cloud: luatools cabinet-cloud get; luatools cabinet-cloud connect --secret-stdin [--server <HTTPS-origin>]; luatools cabinet-cloud disconnect; luatools cabinet-cloud update --friend-labels true
- cabinet: luatools cabinet get; luatools cabinet sync
- cloud-save: luatools cloud-save update --enabled true
- cloudredirect: luatools cloudredirect get [--refresh]; luatools cloudredirect update [--enabled <bool>] [--refresh]; luatools cloudredirect manage
- commands: luatools commands --json
- completion: luatools completion <bash|zsh|fish|powershell>
- depot: luatools depot list [--games | --appid <appid>] [--section <name>]; luatools depot plan <appid> [--depot <id>...] [--all-depots] [--output <path>] [--language <name>] [--os <platform>]; luatools depot get <depotid> --appid <appid>; luatools depot search --appid <appid> --query <text>; luatools depot reconcile <appid>; luatools depot update <depotid> --appid <appid> [--preset <hash>] [--locked <bool>] [--enabled <bool>]; luatools depot download <appid> [--depot <id>...] --output <path> [--language <name>] [--os <platform>]
- describe: luatools describe <resource>
- dlc: luatools dlc get <appid> [--batch <id>]; luatools dlc check <appid> [--owned <appid>...]; luatools dlc add <appid> [--batch <id>]; luatools dlc cancel <id>
- doctor: luatools doctor [--fix]
- download: luatools download list; luatools download update <id> --move <up-or-down>; luatools download delete <id>; luatools download watch [--cursor <opaque>]
- fix: luatools fix list [--appid <appid>] [--refresh] [--my-games] [--tag <tag>] [--provider <provider>]; luatools fix get --appid <appid>; luatools fix search [--query <text>] [--appid <appid> --folder-url <url>]; luatools fix apply <fixid> --appid <appid> --provider <provider>; luatools fix revert <fixid> --appid <appid>
- game: luatools game list [--featured | --pending-added | --cabinet] [--refresh]; luatools game get <appid-or-Steam-link> [--installed]; luatools game search --query <text> [--added]; luatools game add <appid> --source <source-or-auto>; luatools game delete <appid>...; luatools game remove-drm <appid>; luatools game install <appid>
- guide: luatools guide
- help: luatools help [resource]
- history: luatools history list [--kind <download-or-pending-added>] [--status <state>]; luatools history delete <id-or-all-or-pending-added>
- hubcap-key: luatools hubcap-key get [--refresh]; luatools hubcap-key update --secret-stdin; luatools hubcap-key delete
- job: luatools job list [--active] [--idempotency-key <key>]; luatools job get <id>; luatools job wait <id>; luatools job confirm <id> --decision <replace-or-skip>; luatools job cancel <id> [--keep-files | --delete-created]; luatools job retry <id>; luatools job pause <id>; luatools job resume <id>
- launch-option: luatools launch-option get <appid>; luatools launch-option list [--drifted]; luatools launch-option create <appid> --file <path>; luatools launch-option update <appid> [--entry <index>] --file <path> [--apply <bool>]; luatools launch-option delete <appid> --entry <index>; luatools launch-option revert <appid>; luatools launch-option apply --drifted
- link: luatools link get --kind <kind> [--appid <appid>] [--source <source>] [--url <url>]
- lua: luatools lua get <appid> [--content] [--diff-file <path>]; luatools lua add --file <path>...; luatools lua update <appid> --file <path>
- manifest: luatools manifest add <fixid> --appid <appid>
- mode: luatools mode get [--refresh]; luatools mode list; luatools mode update <ost-or-bst-or-custom>
- ping: luatools ping
- plugin: luatools plugin get [--refresh]; luatools plugin update --install; luatools plugin delete
- preset: luatools preset list --appid <appid>; luatools preset get <hash> --appid <appid>; luatools preset create --appid <appid> [--file <path> | --from-live]; luatools preset update <hash> --appid <appid> [--name <name> | --from-live]; luatools preset apply <hash> --appid <appid>; luatools preset delete <hash> --appid <appid>
- queue: luatools queue list
- session: luatools session --stdio
- setting: luatools setting list; luatools setting get <key>; luatools setting update [key] --value <value>
- source: luatools source list --appid <appid>
- status: luatools status
- steam: luatools steam get; luatools steam update [--path <path> | --auto]; luatools steam restart
- tool: luatools tool launch steamautocrack
- update: luatools update get; luatools update check; luatools update apply
- version: luatools version
- whoami: luatools whoami

