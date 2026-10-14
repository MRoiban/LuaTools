namespace LuaToolsGui.Services;

internal static class ApiContract
{
    internal static bool IsAppId(string? value) => value is { Length: > 0 and <= 10 }
        && value.All(c => c is >= '0' and <= '9') && value[0] != '0'
        && uint.TryParse(value, out var id) && id > 0;
    internal static void RequireAppId(string value)
    {
        if (!IsAppId(value)) throw new ArgumentException("Invalid application identifier.");
    }
    internal static bool ValidText(string? value, int max) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= max && !value.Any(char.IsControl);
    internal static bool ValidCredential(string? value) => ValidText(value, 16384)
        && !value!.Any(char.IsWhiteSpace);
    internal static bool ValidFixId(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':');
}
