using Microsoft.Win32;

namespace LuaToolsGui.Services;

public interface IStartupRegistration
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

internal sealed class MemoryStartupRegistration : IStartupRegistration
{
    public bool IsEnabled { get; private set; }
    public void SetEnabled(bool enabled) => IsEnabled = enabled;
}

/// <summary>Only explicit user toggles write the Run key; construction and reads never repair it.</summary>
internal sealed class WindowsStartupRegistration : IStartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string Command => $"\"{Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable")}\" --minimized";
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return string.Equals(key?.GetValue("LuaTools") as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key, writable: true);
        if (enabled) key.SetValue("LuaTools", Command, RegistryValueKind.String);
        else key.DeleteValue("LuaTools", throwOnMissingValue: false);
    }
}
