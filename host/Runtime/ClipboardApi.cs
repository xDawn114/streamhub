using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace StreamHub.Host.Runtime;

/// <summary>The caller supplies the existing binding and live-session checks.</summary>
public sealed class ClipboardApi(
    Func<string, string, string, CancellationToken, Task> authorize,
    Func<string, string, string, string, ClipboardPayload?, CancellationToken, Task<ClipboardPayload?>> request)
{
    private sealed record ClipboardRequest(string Mode, string LeaseId, string ClientId, string Action, ClipboardPayload? Payload = null);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task HandleAsync(HttpContext context, CancellationToken token)
    {
        if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("剪贴板请求必须使用 JSON。");
        if (context.Request.ContentLength > ClipboardPolicy.MaximumEnvelopeBytes) throw new InvalidOperationException("剪贴板请求过大。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var body = new MemoryStream();
        byte[] buffer = new byte[8192]; int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
        {
            if (body.Length + count > ClipboardPolicy.MaximumEnvelopeBytes) throw new InvalidOperationException("剪贴板请求过大。");
            body.Write(buffer, 0, count);
        }
        var input = JsonSerializer.Deserialize<ClipboardRequest>(body.GetBuffer().AsSpan(0, (int)body.Length), Json)
            ?? throw new InvalidOperationException("剪贴板请求无效。");
        if (input.Mode is not ("remote" or "tablet") || !ValidId(input.LeaseId) || !ValidId(input.ClientId))
            throw new InvalidOperationException("剪贴板会话无效。");
        await authorize(input.Mode, input.LeaseId, input.ClientId, token).ConfigureAwait(false);
        var payload = await request(input.Mode, input.LeaseId, input.ClientId, input.Action, input.Payload, token).ConfigureAwait(false);
        await context.Response.WriteAsJsonAsync(new { success = true, payload }, token).ConfigureAwait(false);
    }

    private static bool ValidId(string? value) => value is { Length: >= 8 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
