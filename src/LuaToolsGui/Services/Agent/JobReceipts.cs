using System.Text.Json.Nodes;
using LuaTools.Agent;
namespace LuaToolsGui.Services.Agent;

/// <summary>One nested job reference for per-item acceptance; also reads older journals.</summary>
internal static class JobReceipts
{
    public static Envelope Normalize(Envelope receipt)
    {
        void Rows(JsonNode? data)
        {
            if (data?["results"] is not JsonArray rows) return;
            foreach (var row in rows.OfType<JsonObject>())
                if (row["job_id"] is { } id)
                {
                    row["job"] = new JsonObject { ["id"] = id.DeepClone(), ["state"] = row["state"]?.DeepClone() };
                    row.Remove("job_id");
                }
        }
        Rows(receipt.Data); Rows(receipt.Error?.Details);
        return receipt;
    }
    public static string[] Ids(Envelope? receipt)
    {
        var ids = new HashSet<string>();
        void Read(JsonNode? data, bool top = true)
        {
            if (data is not JsonObject) return;
            if (data["job_id"]?.ToString() is { } direct) ids.Add(direct);
            if (top && data["id"]?.ToString() is { } id) ids.Add(id);
            if (data["job"]?["id"]?.ToString() is { } nested) ids.Add(nested);
            if (data["results"] is JsonArray rows) foreach (var row in rows) Read(row, false);
        }
        Read(receipt?.Data); Read(receipt?.Error?.Details);
        return ids.ToArray();
    }
}
