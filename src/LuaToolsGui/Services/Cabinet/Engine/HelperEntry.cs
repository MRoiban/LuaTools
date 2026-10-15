#nullable disable
// LuaTools: the helper-process branches of Cabinet's Main, verbatim. Cabinet's engine relaunches
// its own executable (Application.ExecutablePath, here LuaTools.exe) with these arguments.
using System;
using System.Threading;

namespace CabinetEngine
{
    static class HelperEntry
    {
        internal static bool Handle(string[] args)
        {
            if (args.Length == 5 && args[0] == "--presence-source")
            {
                try { App.RunPresenceSource(args); }
                catch (Exception ex) { App.Log("presence source failed: " + ex.Message); Environment.ExitCode = 1; }
                return true;
            }
            if (args.Length == 5 && args[0] == "--schema")
            {
                try
                {
                    App.SetErrorMode(0x0001 | 0x0002 | 0x8000);
                    int parent = int.Parse(args[2]); long started = long.Parse(args[3]);
                    using (var stop = EventWaitHandle.OpenExisting(args[4]))
                        SchemaFill.Fetch(args[1], delegate { return !App.ParentAlive(parent, started) || stop.WaitOne(0); }, App.Log);
                }
                catch (Exception ex) { App.Log("schema failed: " + ex.Message); Environment.ExitCode = 1; }
                return true;
            }
            if (args.Length > 0 && args[0] == "--shadow")
            {
                try { if (args.Length != 5) throw new ArgumentException("Invalid shadow arguments"); App.RunShadow(args); }
                catch (Exception ex) { App.Log("shadow failed: " + ex.Message); Environment.ExitCode = 1; }
                return true;
            }
            return false;
        }
    }
}
