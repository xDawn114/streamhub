using System.Text.Json;

namespace StreamHub.Host.Runtime;

/// <summary>One request on the authorized user's STA desktop; never starts the main window.</summary>
public sealed class ClipboardAgent(RuntimeClient runtime)
{
    public static bool ValidJobId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigit);

    public async Task<int> ExecuteAsync(string id, Func<ClipboardPayload> read, Action<ClipboardPayload> write, CancellationToken token)
    {
        if (!ValidJobId(id)) return 1;
        var response = await runtime.SendAsync(new("clipboard-take", RequestId: id), token);
        if (!response.Success || string.IsNullOrEmpty(response.PayloadJson)) return 1;
        ClipboardJob? job;
        try { job = JsonSerializer.Deserialize<ClipboardJob>(response.PayloadJson, RuntimeProtocol.JsonOptions); }
        catch (JsonException) { return 1; }
        if (job is null || job.Id != id || job.ExpiresAt <= DateTimeOffset.UtcNow) return 1;
        var claim = await runtime.SendAsync(new("clipboard-claim", RequestId: id), token);
        if (!claim.Success || token.IsCancellationRequested || job.ExpiresAt <= DateTimeOffset.UtcNow) return 1;
        ClipboardPayload? value = null;
        string? error = null;
        try
        {
            if (job.Action == "get") value = read();
            else if (job.Action == "set" && job.Payload is { } incoming) { _ = ClipboardPolicy.Decode(incoming); write(incoming); }
            else throw new InvalidOperationException("电脑剪贴板请求无效。");
        }
        catch (InvalidOperationException ex) { error = ex.Message; }
        catch (Exception) { error = "电脑剪贴板暂时不可用，请解锁桌面后重试。"; }
        var complete = await runtime.SendAsync(new("clipboard-complete", RequestId: id, ClipboardSuccess: error is null,
            ClipboardFormat: value?.Format, ClipboardData: value?.Data, ClipboardError: error), token);
        return complete.Success && error is null ? 0 : 1;
    }
}
