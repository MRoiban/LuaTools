# Security policy for this fork

This source fork starts from LuaTools v1.3.2. Its MIT license and upstream copyright
notices remain in LICENSE. Changes here reduce the trust granted to remote services
and executable dependencies; they are not an independent security audit or a promise
that upstream code, third-party binaries or DLLs are safe.

## Trust boundary

The WPF catalog and local library tools run with the current Windows user's privileges.
Steam directories, imported Lua/manifests, saved settings and remote responses are
untrusted inputs. Avoid running the application as administrator. Network metadata
and artwork can still contact selected HTTPS services and public image origins and
reveal an IP address;
manual authentication and API-key use still disclose information to the selected service.

This fork does not donate Steam keys or send launch analytics. Legacy settings cannot
enable donation. DNS uses the Windows system resolver by default; the original DNS
settings allow manual opt-in to bounded Cloudflare DNS-over-HTTPS with normal HTTPS
certificate validation. It never checks, stages or applies upstream
application updates, and does not run Velopack startup hooks. Windows autostart is
default OFF: an explicit user toggle registers only LuaTools itself in the current
user Run key with `--minimized`, opening it in the tray. Startup never writes or
repairs that registration. No Steam-injection startup hook is added. External protocol URLs and
loader flags cannot start silent installs. Startup does not install/update a companion
plugin or modify the selected Steam mode. An explicitly installed, enabled source-owned
plugin resumes its isolated browser injection and in-process backend at startup.
Eight persisted switches default to off: executable game fixes,
BetterSteamTools, OpenSteamTools, Steam plugin, DepotDownloaderMod, Steamless,
SteamAutoCrack and CloudRedirect. Turning one on only saves consent. A subsequent
explicit Install or Run action can download, install and execute unreviewed code for
that workflow; it does not constitute independent hash trust or a security audit.
Turning it off blocks app-initiated installs, tool launches and plugin injection,
including previously installed bundles. It does not terminate external programs or
uninstall native DLLs. Steam can load previously installed DLLs on subsequent launches
even while their switch is off; the application does not block Steam itself.
Fork application releases are signed with a dedicated release key and staged separately
from explicit restart approval; see [RELEASES.md](RELEASES.md). Automatic runtime installation,
upstream app updates, executable background updates, telemetry and donation remain disabled.

State is isolated in `%APPDATA%\LuaToolsGui-SecurityHardened`; no upstream settings,
authentication tokens, plugin code or executable cache is automatically imported. Hubcap
API keys are protected with CurrentUser DPAPI; plaintext or invalid saved values are
rejected. This protects at rest against other Windows identities, not malicious code
running as the same user. Do
not copy old executable/plugin caches into the fork. Ordinary library edits still modify
user-selected files; backups and review remain necessary.

TLS uses normal certificate verification and approved HTTPS destinations. DNS uses
the system resolver by default or explicitly selected Cloudflare DNS-over-HTTPS. The
original capability-matched GitHub mirrors remain available through exact approved
HTTPS origins, bounded responses and validated download inputs. They remain external
trust dependencies. The original `http://167.235.229.108/check_apis?appid=...` availability lookup
is an explicit metadata-only exception: GET, fixed IP/path and canonical app ID, no
credentials/cookies/proxy/redirects, 64 KiB response limit and bounded time. Returned
source names/statuses are validated before display; downloads still use HTTPS. An
on-path attacker can observe the queried app ID or falsify availability. This is not
an authenticated availability guarantee, and no other remote HTTP route is enabled.
A checksum supplied by the same server as a binary detects transfer corruption; it
does not establish independent authenticity. Targeted source reviews are recorded in [audits/README.md](audits/README.md);
these are not exhaustive audits or source-to-binary equivalence proofs.
The independent dependency allowlist remains empty. `UserAuthorizedDependencies`
handles bounded tool/plugin staging and complete-bundle integrity receipts for explicitly
enabled workflows; these user-writable receipts are neither authenticity proofs nor independent
approvals. HTTPS destination/redirect/credential controls, response limits and filesystem
path checks still apply after opt-in. ZIP declared-size and CRC checks establish format
integrity only, not trust in downloaded code. Other remote plaintext requests remain blocked.

The Steam plugin runtime requires a successful explicit installation receipt,
resumes at startup/re-enable, and stops when its switch is turned off.
Plugin requests stay inside the LuaTools process attached to the selected Steam installation
and Windows session. No plugin HTTP server listens on port 6767. Discovery checks local TCP
listener ownership before attaching and again before dispatch; endpoint literals and target
URLs remain constrained. Both standard Steam debugging ports and Falcon's OS-assigned ports
are supported. Cabinet friend-label browser connections use the same ownership checks.
The frontend and WinMM wrapper
are now source-owned and bundled with the fork. The frontend runs in a CDP isolated
world with a narrow bridge. Local CDP process spoofing and same-user filesystem attacks
remain limitations; see [implementation record](design/steam-plugin/implementation.md).

## Building and reviewing a release

Cabinet runs as an unmodified port of the user's own app (github.com/MRoiban/cabinet),
under `src/LuaToolsGui/Services/Cabinet/Engine`. Its upstream safeguards are kept as written; no
fork hardening is layered on top. It is off by default. When enabled, LuaTools takes Cabinet's
own `Local\Cabinet` single-instance mutex and uses Cabinet's data folder, `%LOCALAPPDATA%\Cabinet`.
LuaTools and standalone Cabinet therefore share one ledger and never run the engine at once.
The engine edits Steam's local achievement/stat cache, `localconfig.vdf` playtime and Workshop
junctions, with Cabinet's first-write backups. Hidden `LuaTools.exe --shadow/--schema/--presence-source`
helpers load Steam's own `steamclient64.dll` to report the real game to Steam. Fork updates wait
while helpers run. Network access matches standalone Cabinet: Workshop profiles from GitHub raw,
an opt-in Cabinet Cloud and Steam's local debugging port for friend labels. See
[ENDPOINTS.md](ENDPOINTS.md). Controller layouts, screenshot attribution, the tray, the
`cabinet:` protocol and Cabinet's self-updater are not included.

Use an installed, trusted .NET 8 SDK on Windows. Inspect changes to project files,
package versions/lockfiles, embedded resources and workflows before building. NuGet
packages and GitHub Actions are supply-chain dependencies; lockfiles and pinned action
commits constrain drift but do not audit those dependencies.

```powershell
dotnet restore LuaToolsGui.sln --locked-mode
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugin-loader.ps1 -Test
dotnet build LuaToolsGui.sln -c Release --no-restore
dotnet test LuaToolsGui.sln -c Release --no-build --no-restore
dotnet publish src/LuaToolsGui/LuaToolsGui.csproj -c Release --no-restore --self-contained false -o artifacts/publish
```

The source does not fetch or execute upstream release binaries during this process.
The build job has read-only repository permissions and tests the locked source. Only
the release job on `MRoiban/LuaTools` branch `security-hardened` has release-write
permission and access to the dedicated signing secret. Successful branch commits
produce versioned portable releases. Pull requests cannot invoke that signing job.
Framework-dependent builds require a separately installed .NET 8 Desktop Runtime.

The client pins the release public key. It checks a signed manifest, version, repository,
package size/hash and archive paths before permitting the explicit restart action to
launch the new build. It never falls back to upstream application feeds. See
[RELEASES.md](RELEASES.md) for the versioning, signing and portable bootstrap details.
A valid signature identifies a release authorized by this fork; it is not a malware
scan or proof of correct code. Protect repository write access, workflows and the
GitHub signing secret: compromise of that signing pipeline can authorize harmful updates.

Review commits and dependency/workflow changes before pushing the release branch.
The automation verifies tests and signs builds; it does not conduct a human source
review. The dependency reports in [audits/README.md](audits/README.md) document remaining
issues and do not approve the third-party executable bundles. No dependency binaries
are bundled or automatically mirrored into fork application releases.

## Reporting a vulnerability

Use a private security report to the maintainer of the repository hosting this fork,
if that repository has enabled private reporting. Otherwise arrange a private contact
with that maintainer before sharing credentials, keys or exploit details. Do not send
Steam keys, authentication files or complete local configuration files in a public issue.
