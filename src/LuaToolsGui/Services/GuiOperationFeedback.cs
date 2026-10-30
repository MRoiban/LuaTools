using Microsoft.Extensions.Logging;
namespace LuaToolsGui.Services;

internal static class GuiOperationFeedback
{
    internal static void Report(ToastService toast, string title, string message, Exception error)
    {
        AppEventLog.Shared.Write(LogLevel.Warning, "GUI", message, error);
        toast.Show(title, message, error: true);
    }
}
