using System.Collections.Concurrent;
using System.IO;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class CacheStartupTests
{
    [Fact]
    public void StartupCacheSaveCompletesWithoutPumpingUiContinuations()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LuaToolsStartupTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "cache.json");
        var context = new QueuedContext();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try { new CacheService(path).SaveHardwareAppIds(Enumerable.Range(1, 10000).Select(i => (long)i), 123); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        bool completed = thread.Join(TimeSpan.FromSeconds(3));
        // Drain only for cleanup when testing the broken implementation, never to make the assertion pass.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (thread.IsAlive && DateTime.UtcNow < deadline)
        {
            if (context.Work.TryDequeue(out var work)) work.Callback(work.State);
            else thread.Join(10);
        }
        try
        {
            Assert.True(completed, "Synchronous cache save waited for the blocked UI synchronization context.");
            Assert.Null(error);
            Assert.Equal(10000, new CacheService(path).GetHardwareAppIds().Count);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { if (!thread.IsAlive && Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        public ConcurrentQueue<(SendOrPostCallback Callback, object? State)> Work { get; } = new();
        public override void Post(SendOrPostCallback d, object? state) => Work.Enqueue((d, state));
    }
}
