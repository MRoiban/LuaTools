# Online-Fix.me fix provider

Online-Fix.me appears in the Fixes tab as one more provider beside the lua.tools providers
(DenuvOwO, Generic, Online Fix, Rockstar Games, …, which arrive as listing tags). It is a client-side
provider: no lua.tools account, no daily limit, no lua.tools API call.

Port of Luamoon `moon/plugin/backend/onlinefixme.lua` (behaviour, not code shape).

## Live facts (checked 2026-10-23 from Linux, UA `LuaTools-Fixes/0.2`)
- `GET https://uploads.online-fix.me:2053/uploads/<Game>/Fix%20Repair/` → nginx autoindex
  (`<title>Index of …`, `<a href="Name.rar">`, size/date columns, a Cloudflare script tag appended).
- A missing folder answers **401** (not 404). `/uploads/` itself is 401: there is no global index,
  so the provider cannot enumerate games; it can only look up one game by title.
- Folder lookup is case-insensitive. `Hollow Knight: Silksong` is 401, `Hollow Knight Silksong` is 200.
- Listing response sets an anonymous `online_fix_auth` cookie. The archive GET without it is **403**;
  with that cookie + `Referer: <Fix Repair folder>` it is 200 `application/octet-stream`.
- Archives are single-volume **RAR5**, file data AES-encrypted with the public password
  `online-fix.me`, headers not encrypted, flat layout relative to the game root
  (e.g. `OnlineFix64.dll`, `winmm.dll`, `Raft_Data/Plugins/x86_64/steam_api64.dll`). Typical 8–12 MB.

## Matching a Steam app to a folder (`OnlineFixMeListing`)
Title source: the installed game's appmanifest name when installed, else the listing/card name.
Candidates, in order, de-duplicated (case-insensitive), at most 3 requests per lookup:
1. Cleaned title: remove `™ ® ©`, trim, collapse internal whitespace.
2. If it contains `:` → remove `:` and collapse whitespace (`Hollow Knight Silksong`).
3. If it contains `'` or `’` → also remove those (after step 2's rule).
First candidate whose Fix Repair listing is 200 with ≥1 eligible archive wins. 401/403/404 = not found
at that title. Any other status / transport failure = provider unavailable (stop, don't try further).
Segment rules (as Luamoon): non-empty, ≤ 200 chars, not `.`/`..`, no control chars, no `/ \ ? #`.
Encoding: RFC 3986 unreserved kept, everything else `%XX` (UTF-8), so space → `%20`.

Manual override: the user may paste a folder URL from the site. Accepted only if it starts with
`OnlineFixMeUploadsBase` and the remainder is exactly one segment, optionally followed by
`/Fix%20Repair/` (with or without a trailing file name). Anything else is rejected (InvalidFolder).

## Listing parsing / archive selection
- Body bounded to 1 MiB, 15 s; ≤ 512 `href`s examined. Parse `href="…"` attributes only (regex,
  not an HTML parser); percent-decode; must pass the segment rules.
- Eligible: name contains `fix_repair` (case-insensitive) AND ends with `.rar`, `.zip` or `.7z`
  AND is not a multi-volume part (`.partN.rar`, `.rNN`, `.zNN`, `.7z.NNN`). Never full-game uploads:
  only the `Fix Repair` subfolder is ever listed.
- Size/date: parsed best-effort from the text after `</a>` on the same line; absent → null.
- De-duplicate by exact name; sort ordinal by name. 200 without an `Index of` title → Unavailable.
- Results cached 5 minutes per folder; download always re-lists fresh.

## Download
`OnlineFixMeClient.DownloadArchiveAsync(folder, archiveName)`: fresh listing GET (collects cookie,
confirms `archiveName` is still eligible and listed) → archive GET with `Cookie` (only cookies from
that listing response; name `[A-Za-z0-9_-]{1,64}`, value `[A-Za-z0-9%._~+/=-]{1,4096}`, ≤ 4) and
`Referer`. Response budget = `ManifestContentPolicy.MaxArchiveBytes` (256 MiB) / 5 min; saved through
`HttpFileDownloader.SaveResponseAsync`. Cookies live only for that one call: never cached, logged or
persisted.

## Network policy
- `OutboundNetworkPolicy.Validate`: HTTPS, host `uploads.online-fix.me`, port **2053**, path starting
  `/uploads/`, no query/userinfo/fragment. Port 2053 is allowed for nothing else.
- `HttpCredentialPurpose.OnlineFixMe`: a `Cookie` header is allowed only for GET to that origin with
  path `/uploads/<seg>/Fix%20Repair/<file>`, and never with `Authorization`/`apikey`.
- `ENDPOINTS.md` documents the host, what is sent (game title in the path, anonymous cookie) and why.

## Extraction (decision: SharpCompress, managed)
No existing dependency opens RAR. Options weighed:
- **SharpCompress 0.50.7 (MIT, pure managed, no transitive deps)** — verified on the real Raft
  archive: RAR5 + AES decrypts byte-identically to 7-Zip. We enumerate entries ourselves, so path
  validation, size bounds and link rejection are ours, and no external process is started.
- User-installed 7-Zip/WinRAR: native code we don't control, a process launch (a new executable
  workflow), path handling delegated to the tool, and absent on many machines. Rejected.
- ZIP-only v1: every observed archive is RAR → the provider would be useless. Rejected.

`OnlineFixMeArchiveConverter` turns the downloaded archive into a **validated ZIP snapshot**, which is
then handed to the existing `ApplyDenuvoFix` path unchanged (so `ExecutableFixPolicy.ReadPackage`,
plan/backup/record/hashes, the applied-fix index and `RevertDenuvoFix` all apply as-is). Rejects the
whole archive (nothing applied) on: path traversal / rooted / drive / `:` / reserved names
(`ManifestContentPolicy.ValidateRelativePath`), links or special entries, any `.luatools-fix` segment,
case-insensitive duplicates, > `MaxEntries` files, an entry whose ACTUAL bytes exceed
`MaxManifestBytes`, total actual bytes > `MaxArchiveBytes`, total > max(1 MiB, archive size × 200),
wrong password / corrupt data, unsupported formats, multi-volume archives.

## Apply / consent / undo
- Fix id: `onlinefixme:<archive name>` (record file name via `SafeFixKey`).
- `ManifestJobFactory.CreateOnlineFixMeJob`: `ExecutableFixPolicy.RequireEnabled` and the
  installed-game check happen **before any network request**; then download → convert → apply via
  `ApplyDenuvoFix`. Every archive is treated as executable (they all ship DLLs); the same
  "executable game fixes" switch as other fixes governs it. No lua.tools sign-in needed.
- Revert/undo, applied state and applied-fix index reuse the Denuvo-fix code paths by fix id.

## UI
- Grid: a synthetic provider pill **Online-Fix.me** appended after the listing tags. Selecting it shows
  the user's installed Steam games (listing card if the game is in the listing, otherwise a synthetic
  card with Steam's header image), since the site has no index and fixes only apply to installed games.
- Flyout: the Online-Fix.me lookup runs alongside the lua.tools fixes request; each eligible archive
  becomes a fix row tagged Online-Fix.me (Fix + Revert buttons, no Manifest button), so the provider
  also shows in the per-game provider pills. Status line: searching / not found under "<title>" (with
  a paste-folder-URL box + Look up) / unavailable.
- `luatools://fix/<appid>` falls back to an installed game's synthetic card when the game isn't listed.
