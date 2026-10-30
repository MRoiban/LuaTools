using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

/// <summary>Steam-plugin backend events, for services without an injected logger.</summary>
public static class PluginLog
{
    private const string Category = "Plugin";

    public static void Log(string msg) => AppEventLog.Shared.Write(LogLevel.Information, Category, msg);

    public static void Error(string msg, Exception ex) => AppEventLog.Shared.Write(LogLevel.Error, Category, msg, ex);
}
