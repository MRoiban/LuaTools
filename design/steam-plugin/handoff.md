# Steam store plugin: design handoff

- **Mockup:** `design/steam-plugin/mockup.html`, revision 2. SHA-256 `6887a257a4d4838da46ee40c47bed176dffa88595bff51ee9a716abc85352fcd`.
- **Status:** selected by the parent after desktop and narrow review. **Not user-approved.** Implementation is already authorized.
- **Companion:** `feature-parity.md` lists every reference behaviour, with lines and the deviations that need owner sign-off.
- **Authority:** the mockup governs visuals. Implementers own integration, real data, accessibility semantics and tests. Raise any visual deviation, with a reason, before making it.

The mockup is design-only. It has an inline state switcher (State / Overlay / Width / Denuvo), a `#state=…&overlay=…&width=narrow&denuvo=1` hash, and `window.__mock.set()`. It has no network, bridge, persistence or account access.

## Visual verification

**Reference capture.** I inspected a public, signed-out Steam store page (`store.steampowered.com/app/1145360`) in an incognito preview tab. No account was used and no actions were taken. Computed styles were captured there and are reproduced as tokens. I also confirmed three DOM anchors on that page:

- `#game_area_purchase`: its first child is `.game_area_purchase_game_wrapper`.
- `.apphub_OtherSiteInfo`
- `._1wn1lBlAzl3HMRqS1llwie`: an empty 5×46 slot at the right end of the "Store menu" nav. This is the original header-icon target.

**Mockup inspection.** The served mockup was inspected at 1280, 1100, 1000 and 390 px:

- Picker, idle with Denuvo, installed bar plus success, menu, remove modal, and the daily-limit error at 390.
- `html_preview` at 728 px, with no console output.
- No horizontal overflow at 390 (`scrollWidth` 390).

**Limits.** The Steam client's embedded CEF was not inspected; the client store omits the global header, which the mockup models. Big Picture was not inspected. Motiva Sans is not installed locally, so the mockup falls back to Arial, as Steam does.

## Layout

1. **Store panel.** Inserted as the first child of `#game_area_purchase`, so it sits above "Buy {Game}" and below the queue/DRM notices. It uses the native purchase-box geometry:
   - 4 px radius, padding `16px 16px 26px`, `margin-bottom: 28px`.
   - 21 px Motiva title; subline 13 px `#acdbf5`.
   - Black action bar absolutely placed at `right:16px; bottom:-17px`, with an optional price-style text cell and 32 px buttons.
   - 20 px glyph top-right, in the slot Steam uses for platform icons.
   - **Fallback anchors:** `.apphub_OtherSiteInfo` (desktop) or `#queueBtnFollow`'s parent (Big Picture), as a single `btnv6_blue_hoverfade btn_medium` button that opens the same flow inside a modal.
2. **Source rows.** Native DLC-row look: `rgba(0,0,0,.2)`, 2 px gap, min-height 32, padding `4px 6px 4px 12px`. Contents: name (13 px Motiva), usage stat (11 px muted), status dot + label (12 px), and an 86 px action column with a small green Download button or an "Add key" link.
3. **Progress.** A 6 px bar with track `rgba(0,0,0,.45)` and a `#1a9fff→#47bfff` fill. Indeterminate shows a 30 % sweep. Percent sits right-aligned beneath it, in 11 px muted text.
4. **Installed bar.** Replaces the panel when the game is already added: `rgba(0,0,0,.2)`, 4 px radius, min-height 48. Contents:
   - 22 px green check tile.
   - 15 px white "{Game} is added via LuaTools" plus an optional Denuvo tag.
   - Small blue-fade buttons: Fixes, Remove, Restart Steam.
5. **Store-nav icon and menu.** A 27 px square icon after search, background `rgba(103,193,245,.15)`. The dropdown is `popup_block_new`: `#3d4450`, shadow `0 0 12px #000`, 13 px items `#dcdedf`, hover inverts to `#dcdedf`/`#171a21`, min-width 220, right-aligned to the icon. Items in order:
   - Header "LuaTools"
   - Fixes for {Game}… (app pages only)
   - Remove {Game}… (installed only)
   - Added since last restart (count)
   - divider
   - Restart Steam…
   - Check for updates
   - Settings…
   - Discord
6. **Modals.** Steam `newmodal` look: scrim `rgba(0,0,0,.8)`, card max 460 px, background `radial-gradient(… rgba(74,81,92,.4) …), #25282e`, 22 px Motiva title, 14 px body, right-aligned footer. Cancel/Dismiss are grey `#3d4450`. The confirm button is green for Restart/OK and blue-fade for Remove. There is a × close button. Use `position: fixed` in production; the mockup uses absolute positioning only so it fits the preview frame.
7. **Narrow layout (≤600 px, or the store's responsive mode).**
   - The action bar becomes static and right-aligned below the content.
   - A bar with a text cell stretches full width.
   - Source rows wrap: name on its own line, then stat/status, then the action pushed right.
   - Installed-bar buttons become equal-width full-row buttons.
   - Modals top-pad 70 px.

**Copy rules.** No explanatory paragraphs, tooltips, security text or dashboard branding. Only the strings shown in the mockup. App-supplied `installStatus`/`error` strings are rendered verbatim.

## Tokens

| Token | Value | Use |
|---|---|---|
| page / text / muted / link | `#1b2838` / `#c6d4df` / `#8f98a0` / `#67c1f5` | inherited from Steam; don't set the page background |
| font body / head | `Arial, Helvetica, sans-serif` 12 px / `"Motiva Sans", Arial…` | |
| purchase bg | `linear-gradient(-60deg, rgba(226,244,255,.3) 5%, rgba(84,107,115,.3) 95%)` | panel |
| green button | span `linear-gradient(to right,#75b022 5%,#588a1b 95%)`, text `#d2efa9`; hover `#8ed629→#6aa621`, `#fff` | primary |
| blue-fade button | `rgba(103,193,245,.2)`, text `#67c1f5`; hover `linear-gradient(-60deg,#417a9b 5%,#67c1f5 95%)`, `#fff` | secondary |
| grey button | `#3d4450`, text `#dfe3e6` | modal cancel |
| button geometry | 1 px outer pad, 2 px radius, span `0 15px` 15 px/30 px; small `0 10px` 12 px/22 px | |
| ok / warn / error text | `#a4d007` / `#d9a441` (tag `#e2b764` on `rgba(217,164,65,.18)`) / `#e7a3a1` | statuses |
| focus | `outline: 2px solid #67c1f5; outline-offset: 2px` on `:focus-visible` | all controls |

Prefix all classes `lt-` and scope them under the panel, menu and modal roots. Reuse Steam's own `btn_green_steamui` / `btnv6_blue_hoverfade` classes where they exist on the page; otherwise ship the equivalent CSS. Don't restyle Steam elements. No external fonts, icon CDNs or images: use inline SVG, plus `GetIconDataUrl` for the nav icon.

## State → backend mapping

The bridge is `window.Millennium.callServerMethod("luatools", Method, args)`. The polyfill queues it, and `CefInjectorService.CallBackendMethod` (`CefInjectorService.cs:269`) maps it to `HttpServerService` (`HttpServerService.cs:189`). Methods:

| Method | Route |
|---|---|
| `HasLuaToolsForApp` | GET `/has/{appid}` → `{exists}` |
| `StartLuaToolsAdd {appid,name}` | POST `/add/{appid}` → `PluginAddService.Start` |
| `GetLuaToolsAddStatus` | GET `/add-status/{appid}` |
| `PickLuaToolsAddSource {appid,source}` | POST `/add-source/{appid}` → `PluginAddService.Pick` |
| `CancelAddViaLuaTools` | POST `/cancel/{appid}` (cancels the queue item `manifest:{appid}`) |
| `DeleteLuaToolsForApp` | POST `/remove/{appid}` |
| `OpenFix` | POST `/open/fix/{appid}` |
| `OpenSettings` | POST `/open/settings` |
| `RestartSteam` | POST `/restart-steam` → `{success,error}` |
| `CheckForUpdatesNow` | POST `/check-updates` → `{success}` |
| `ReadLoadedApps` | GET `/loaded-apps` → `{apps:[{appid,name}]}` |
| `DismissLoadedApps` | POST `/loaded-apps` |
| `OpenExternalUrl {url}` | POST `/open-url` (`OutboundNetworkPolicy` applies) |
| `GetIconDataUrl` | GET `/icon` |

`/add-status` fields (`HttpServerService.cs:279`): `checking`, `fastFetch`, `sourcesLoaded`, `installStatus`, `installFailed`, `error`, `installed`, and `sources[]`. Each source has `name`, `displayName`, `status`, `available`, `canDownload`, `locked`, `needsKey`, `stats`, `downloading`, `progress`, `indeterminate`.

| Mockup state | Real condition | Actions |
|---|---|---|
| `idle` "Add {Game} via LuaTools / Not added" | `/has` → `exists:false` and no active add | Add via LuaTools → `StartLuaToolsAdd` (pass the scraped name) |
| `installed` bar | `/has` → `exists:true` (also `add-status.installed`) | Fixes → `OpenFix`; Remove → modal → `DeleteLuaToolsForApp`; Restart Steam → modal → `RestartSteam` |
| `checking` | after Start, `checking:true` with empty `sources` | Cancel → `CancelAddViaLuaTools` |
| `pick` | `sourcesLoaded && !fastFetch`, no source `downloading` | row/Download → `PickLuaToolsAddSource {source: name}`; Add key → `OpenSettings`; Cancel = `CancelAddViaLuaTools`, then clear locally (no download is active yet) |
| row status | `downloading` → spinner "Downloading"; `needsKey&&locked&&stats` → "Limit reached"; `needsKey&&locked` → "Needs key" + Add key; `status!=="available"` → "Not found" (hide stat); else "Available" + stat; Download button only if `canDownload && !fastFetch` | |
| `downloading` / `indeterminate` | any source `downloading`. Bar width = `floor(progress)`; `indeterminate` → sweep with "…". Title "Adding {Game}", subline "Downloading from {displayName}…". With FastFetch on there is no picker, so go straight from checking to this state. | Cancel → `CancelAddViaLuaTools` |
| `added` | `installStatus` set (or `installed:true`) after an add | subline = `installStatus` verbatim; Restart Steam → restart modal. Next page load shows `installed`. |
| `err-failed` | `error && installFailed && !fastFetch` with sources | error line above pickable rows; tag "Failed" |
| `err-nosource` | `error` with FastFetch (e.g. `Strings.Err_NoSourceAvailable`), rows non-clickable | Try Again → `StartLuaToolsAdd`; Close → idle |
| `err-key` / `err-limit` | `error`, with a `needsKey` row that is `locked` (no stats → key; stats → limit) | Open Settings → `OpenSettings`. **Assumption:** no structured error code exists, so classify from row state and show `error` text verbatim. |
| `cancelled` | user pressed Cancel; the next poll reports `error`/`installFailed` from the cancelled queue item | track cancel locally and show "Cancelled", not the error. **Assumption:** `DownloadAsync` surfaces cancellation as a failure (`PluginAddService.cs:303`). |
| `removed` | `DeleteLuaToolsForApp` → `success` (`count` may be 0) | Restart Steam, or Add via LuaTools again; on failure show the `err` subline with the response `error` |
| `offline` | bridge call rejected or timed out | Retry → re-run `/has` |
| `disabled` | bridge error text "Steam plugin is disabled in Settings." (`CefInjectorService.cs:321`), or 403 | no action |
| menu "Added since last restart" | `ReadLoadedApps` count (fetch when the menu opens) | Install = `steam://install/{appid}` navigation; SteamDB → `OpenExternalUrl https://steamdb.info/app/{id}/`; Dismiss → `DismissLoadedApps`; Restart Steam → restart modal |
| auto popup "Added since last restart" | store host, once per session (`sessionStorage` gate), `apps.length > 0` | same modal |
| "Checking for updates" modal | `CheckForUpdatesNow` → `success`; on `success:false` show the `error` | OK closes |
| restart modal | — | Restart → `RestartSteam`; on `success:false` show the error in the modal body |
| Denuvo tag | `.DRM_notice` text contains "Denuvo" (no fix data exists today) | — |

## Integration boundaries and required plumbing changes

**Ownership**

- **Design (this agent):** `design/steam-plugin/*` only.
- **Frontend implementer:** a new source-owned store script and CSS, built into the app and loaded by the injector. It replaces the downloaded `luatools.js`, which today is located by `CefInjectorService.FindLuaToolsJs` under `%AppData%\…\plugin`. Exact file location and bundling are the parent's decision.
- **Integration implementer:** `CefInjectorService`, `HttpServerService` and `PluginAddService` changes, plus tests.
- **Out of scope:** the native loader replacement (separate task), Millennium Lua backend compatibility, and Steam install changes.

**Required fixes for these states to be truthful**

1. **Bridge timeout.** The polyfill resolves `{success:true}` after about 5 s instead of rejecting (`CefInjectorService.cs:489`). This makes `offline` undetectable and can fake success for Remove and Restart. It must reject with a timeout error.
2. **Unknown methods.** `CallBackendMethod` returns `{"success":true}` for unknown methods (`:303`). The new frontend must only call mapped methods. Don't add any new mutating methods without review.
3. **Non-2xx responses.** These currently deserialize the body. An empty 403 body throws, which gives an `e` error; that is acceptable, but surface it as `disabled`/`offline`, not a silent success.

**Security posture**

The audit's page-world bridge finding (`audits/steam-plugin.md`) is unchanged by this design. The design keeps every mutating action behind an explicit click, and Remove and Restart behind a confirm modal. That is UX, not a security boundary. Keep the method surface no larger than the table above.

**Other constraints**

- Keep `window.__LuaToolsReady` semantics: set it once the nav icon is mounted, so the injector stops re-injecting. Keep an in-realm re-injection guard.
- Re-render on SPA navigation. Poll only while a panel is in an add state, every 350 ms, with one timer per appid, cleared on Cancel, completion, navigation and unmount.
- Localisation: author new string resources. Don't reuse the reference's unlicensed translation tables or code.

## Acceptance checks

1. At 1280 and 940 px, the panel left/right edges align with "Buy {Game}" exactly. The action bar overhangs by 17 px like the native one, with no overlap with the next box.
2. At ≤600 px, nothing scrolls horizontally. Rows wrap as in the mockup, and long game names wrap in the title.
3. Every state in the mockup switcher is reachable from real backend responses as mapped above, including FastFetch on/off, key-locked, limit-reached, no-source, cancel, remove, offline and disabled.
4. A source can be picked, its progress shown and cancelled without a page reload. The poll timer count returns to zero after close, cancel, success and navigation.
5. Remove and Restart never fire without the confirm modal. Esc, × and a scrim click cancel.
6. The menu shows Fixes and Remove only on `/app/{id}` pages (Remove only when added). It is keyboard-operable, Esc closes it and focus returns to the icon.
7. All controls have a visible `:focus-visible` ring. Modals trap focus. Controller navigation works in Big Picture (manual check).
8. No requests leave the page except bridge calls: no CDN, fonts or images. No `eval`. No theme JSON fetch.
9. Coexists with real Millennium on the same page (pass-through polyfill), and with SteamDB's button row.
10. Side-by-side screenshot review against the mockup at 1280 and 390 for idle, picker, downloading, installed, an error, the menu and the remove modal.

## Parent implementation decisions

Use the purchase panel and installed manage bar. Keep the existing Discord link. Use Add via LuaTools. No approval pause is required by the user or skill. Bundle the frontend with the fork; use a CDP isolated world and closed shadow roots rather than the page-world Millennium queue. Remote strings are text only. Source-owned native loader is implemented alongside this frontend. Controller behavior and Steam CEF remain manual checks until tested.
