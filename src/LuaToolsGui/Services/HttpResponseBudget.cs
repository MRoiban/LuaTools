using System.Net.Http;

namespace LuaToolsGui.Services;

/// <summary>Bounds bytes actually read and elapsed response-body time, including streaming responses.</summary>
public sealed record HttpResponseBudget(long MaxBytes, TimeSpan BodyTimeout)
{
    public static readonly HttpResponseBudget Json = new(8L * 1024 * 1024, TimeSpan.FromSeconds(30));
    public static readonly HttpResponseBudget LargeList = new(64L * 1024 * 1024, TimeSpan.FromSeconds(60));
    public static readonly HttpResponseBudget Download = new(512L * 1024 * 1024, TimeSpan.FromMinutes(5));
    public static readonly HttpResponseBudget Image = new(8L * 1024 * 1024, TimeSpan.FromSeconds(20));
    public static readonly HttpResponseBudget Dns = new(64L * 1024, TimeSpan.FromSeconds(5));
    internal static readonly HttpRequestOptionsKey<HttpResponseBudget> RequestOption = new("LuaTools.ResponseBudget");

    public void Apply(HttpRequestMessage request)
    {
        Validate();
        request.Options.Set(RequestOption, this);
    }

    internal void Validate()
    {
        if (MaxBytes <= 0 || MaxBytes > Download.MaxBytes || BodyTimeout <= TimeSpan.Zero
            || BodyTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(HttpResponseBudget));
    }
}
