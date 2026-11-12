using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace StreamHub.Host.Runtime;

public sealed class RuntimeClient
{
    private readonly Func<RuntimeRequest, CancellationToken, Task<RuntimeResponse>>? _transport;
    public RuntimeClient() { }
    // A local harness can render the actual desktop window against isolated fixture data.
    public RuntimeClient(Func<RuntimeRequest, CancellationToken, Task<RuntimeResponse>> transport) => _transport = transport;
    public async Task<RuntimeResponse> SendAsync(RuntimeRequest request, CancellationToken cancellationToken = default)
    {
        if (_transport is not null) return await _transport(request, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.Command == "status" ? 5 : 60));
        try
        {
            using var pipe = new NamedPipeClientStream(".", RuntimeProtocol.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Impersonation);
            await pipe.ConnectAsync(2500, timeout.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var options = request.Command.StartsWith("clipboard-", StringComparison.Ordinal) ? ClipboardPolicy.WireJsonOptions : RuntimeProtocol.JsonOptions;
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, options).AsMemory(), timeout.Token);
            var response = await reader.ReadLineAsync(timeout.Token);
            return response is null
                ? new RuntimeResponse(false, "后台服务没有返回结果，请重新连接。")
                : JsonSerializer.Deserialize<RuntimeResponse>(response, RuntimeProtocol.JsonOptions)
                  ?? new RuntimeResponse(false, "后台服务返回的数据不完整。");
        }
        catch (TimeoutException) { return new(false, "尚未连接后台服务，请安装或修复我控制。"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, "后台处理超时，请刷新状态后重试。"); }
        catch (IOException ex) { return new(false, $"后台连接中断：{ex.Message}"); }
        catch (UnauthorizedAccessException) { return new(false, "当前账户没有访问我控制后台的权限。"); }
        catch (JsonException) { return new(false, "后台服务版本不兼容，请修复安装。"); }
    }

    public Task<RuntimeResponse> GetStatusAsync(CancellationToken cancellationToken = default) => SendAsync(new("status"), cancellationToken);
}
