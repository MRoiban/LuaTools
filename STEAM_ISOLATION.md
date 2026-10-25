# Steam installation and session compatibility

LuaTools supports ordinary Steam without Falcon. Existing registry detection, the
Steam-location setting, plugin consent, store actions and signed updates remain in
place. Normal Steam still launches steam.exe directly and may use debugging port
8080. No Falcon binary is bundled with, installed by, or required by this update.

The plugin matches a debugger listener to both the selected Steam directory and
the current Windows session. It supports IPv4/IPv6 loopback and variable debugger
ports. Game-fix windows and other store commands dispatch inside that LuaTools
process; they no longer use a shared HTTP server on port 6767. Requests are rejected
when activation, installation selection or listener ownership changes. The source
frontend's trusted-click, isolated-world, method, argument and deadline checks remain.
Cabinet's friend-label browser connection uses the same ownership checks.

Start/stop/restart operations identify the selected steam.exe and Windows session.
Other installations and sessions are excluded before reading process image paths.
Unreadable identities fail closed before stopping clients or editing launch options.

When Falcon supplies DUO_PORTABLE_LAUNCHER, DUO_PORTABLE_STEAM and DUO_PORTABLE_STATE,
LuaTools follows that Steam installation and forwards start/shutdown through its
launcher. Partial or mismatched context is rejected. A desktop-launched LuaTools
also recognizes SteamFL.exe and PortableSteam.exe beside the selected Steam and
uses Falcon's default portable-state directory. Custom state directories require
the inherited context. Falcon itself supplies IPC and registry isolation.

For concurrent players, use separate Steam directories and Windows sessions. Update
LuaTools in both users' sessions. Falcon builds that assign CEF an available port
avoid the old fixed-port collision; this LuaTools update can discover those ports
but does not update Falcon's native hooks. Existing activation resumes on restart;
normal Steam users need no migration or new configuration.

Validation covers ordinary Steam without Falcon, both installations/sessions,
different debugger ports, in-process backend isolation, selection changes, malformed
isolation context, scoped shutdown and failure before file mutation. Native Cabinet
achievement/cloud helper isolation and live two-player interaction are separate
from these routing tests and are not claimed as fully validated here.
