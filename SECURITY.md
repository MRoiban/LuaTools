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
application updates, and does not run Velopack startup hooks. External protocol URLs and
loader flags cannot start silent installs. Startup does not install/update a companion
plugin, modify the selected Steam mode, start Steam browser injection, or expose the
companion HTTP server. Eight persisted switches default to off: executable game fixes,
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

The Steam plugin runtime starts only after a successful explicit authorized install,
and stops when its switch is turned off. It is not activated at application startup.
Its local HTTP bridge requires an ephemeral per-process token and rejects browser Origin
headers; loopback alone does not authorize requests. Browser-injected plugin code remains
unreviewed code under the user's consent.

## Building and reviewing a release

Use an installed, trusted .NET 8 SDK on Windows. Inspect changes to project files,
package versions/lockfiles, embedded resources and workflows before building. NuGet
packages and GitHub Actions are supply-chain dependencies; lockfiles and pinned action
commits constrain drift but do not audit those dependencies.

```powershell
dotnet restore LuaToolsGui.sln --locked-mode
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
