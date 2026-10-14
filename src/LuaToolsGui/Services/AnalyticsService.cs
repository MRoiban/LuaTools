namespace LuaToolsGui.Services;

/// <summary>Legacy compatibility surface. Telemetry is permanently disabled in this fork.</summary>
public sealed class AnalyticsService
{
    public Task TrackAppLaunchAsync(CancellationToken ct = default) => Task.CompletedTask;
}
