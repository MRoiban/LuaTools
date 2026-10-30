namespace LuaToolsGui.Services.Downloads;

/// <summary>Shared GUI/agent cancellation. Cleanup uses only the downloader's tracked, contained files.</summary>
public static class DownloadCancellationCoordinator
{
    public static async Task<bool> CancelAsync(DownloadQueue queue, DownloadItem item, bool deleteCreated)
    {
        queue.Cancel(item);
        if (!deleteCreated) return true;
        await item.Completion;
        string? output = item.Job.OutputPath;
        string[] files = item.CreatedFiles.ToArray();
        return await Task.Run(() => DepotDownloaderService.TryDeleteCreatedFiles(output, files));
    }
}
