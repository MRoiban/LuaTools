namespace LuaToolsGui.Services.Downloads;
/// <summary>Shared history operation after adapter-specific consent.</summary>
public static class DownloadHistoryCoordinator
{
    public static void Clear(DownloadQueue queue) => queue.ClearHistory();
    public static void Remove(DownloadQueue queue, DownloadHistoryEntry entry) => queue.RemoveHistory(entry);
}
