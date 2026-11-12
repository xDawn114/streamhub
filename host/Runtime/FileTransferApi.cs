using System.Text.Json;
using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace StreamHub.Host.Runtime;

/// <summary>Runs behind the product's existing bearer authorization and pinned HTTPS listener.</summary>
public sealed class FileTransferApi(FileTransferVault vault)
{
    private sealed record Registration(string ClientId, string Name, string Key);
    private sealed record Upload(string Name, long Size, string Sha256);
    private sealed record BrowseRequest(string Path = "", int Offset = 0, string? RootId = null, string Sort = "name");
    private sealed record CompletionResult(bool Success, string Message);
    private sealed class CompletionJob(CancellationToken stopping)
    {
        public readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        public Task<CompletionResult> Work = null!;
    }
    private readonly object _completionGate = new();
    private readonly Dictionary<string, CompletionJob> _completions = new();
    private static object Public(TransferItem item) => new { item.Id, item.ClientId, item.Direction, item.Name, item.Size, item.Sha256, item.Offset, item.State };
    public async Task HandleAsync(HttpContext context, CancellationToken token, CancellationToken stopping = default)
    {
        try
        {
            if (context.Request.Method == "POST" && context.Request.Path == "/v1/files/register")
            {
                var registration = await JsonAsync<Registration>(context, token).ConfigureAwait(false);
                var device = vault.Register(registration.ClientId, registration.Name, registration.Key);
                await Reply(context, new { success = true, device.Allowed, device.UploadAllowed, device.DeleteAllowed, code = device.KeyHash[..8], enabled = vault.Snapshot().Settings.Enabled }, token); return;
            }
            string clientId = context.Request.Headers["X-StreamHub-Client"].ToString(), key = context.Request.Headers["X-StreamHub-Key"].ToString();
            vault.Authorize(clientId, key);
            if(context.Request.Method=="GET" && context.Request.Path=="/v1/files/roots")
            { await Reply(context,new {success=true,roots=vault.BrowseRoots(clientId,key)},token);return; }
            if(context.Request.Method=="POST" && context.Request.Path=="/v1/files/recycle")
            {
                var change=await JsonAsync<BrowseRequest>(context,token);
                vault.RecycleSharedFile(clientId,key,change.RootId??"",change.Path);
                await Reply(context,new {success=true,message="文件已移到电脑回收站。"},token);return;
            }
            var segments = (context.Request.Path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (context.Request.Method == "POST" && segments is ["v1", "files", "browse"])
            {
                var request = await JsonAsync<BrowseRequest>(context, token);
                var listing = vault.Browse(clientId, key, request.Path, request.Offset, request.RootId, request.Sort);
                await Reply(context, new { success = true, listing }, token); return;
            }
            if (context.Request.Method == "POST" && segments is ["v1", "files", "fetch"])
            {
                var request = await JsonAsync<BrowseRequest>(context, token);
                var download = vault.OpenSharedDownload(clientId, key, request.Path, request.RootId);
                // Staging a large file belongs to service lifetime, never to the short HTTP request.
                _ = Task.Run(async () =>
                {
                    using (download)
                    {
                        try { await vault.StageOutgoingAsync(download.Item.Id, download.Source, stopping); }
                        catch (Exception) { /* The vault persists failed/cancelled state for the client. */ }
                    }
                });
                await Reply(context, new { success = true, task = Public(download.Item) }, token); return;
            }
            if (segments is ["v1", "files", "tasks"])
            {
                if (context.Request.Method == "GET")
                { await Reply(context, new { success = true, tasks = vault.List(clientId, key).Select(Public).ToArray(), chunkBytes = FileTransferVault.ChunkBytes }, token); return; }
                if (context.Request.Method == "POST")
                { var spec = await JsonAsync<Upload>(context, token); await Reply(context, new { success = true, task = Public(vault.CreateUpload(clientId, key, spec.Name, spec.Size, spec.Sha256)) }, token); return; }
            }
            if (segments.Length is < 4 or > 5 || segments[0] != "v1" || segments[1] != "files" || segments[2] != "tasks") throw new TransferError(404, "文件接口不存在。");
            var id = segments[3];
            if (segments.Length == 4 && context.Request.Method == "GET")
            {
                var item = vault.Get(id, clientId, key);
                var completion = ReadCompletion(id);
                if (completion is { State: not "running" }) item = vault.Get(id, clientId, key);
                await Reply(context, new { success = true, task = Public(item), completionState = completion?.State, completionMessage = completion?.Message }, token); return;
            }
            if (segments.Length == 5 && segments[4] == "cancel" && context.Request.Method == "POST")
            { vault.Get(id, clientId, key); CancelCompletion(id); await vault.CancelAsync(id, token); await Reply(context, new { success = true }, token); return; }
            if (segments.Length == 5 && segments[4] == "complete" && context.Request.Method == "POST")
            {
                var item = vault.Get(id, clientId, key);
                if (item.Direction == "download" || item.State == "completed" || context.Request.Headers["X-StreamHub-Completion"] != "async-v1")
                { await Reply(context, new { success = true, task = Public(await vault.CompleteAsync(id, clientId, key, token)) }, token); return; }
                StartCompletion(item, clientId, key, stopping);
                await Reply(context, new { success = true, task = Public(item), completionState = "running" }, token); return;
            }
            if (segments.Length != 5 || segments[4] != "data" || !long.TryParse(context.Request.Headers["X-Offset"], out var offset)) throw new TransferError(400, "分块位置无效。");
            if (context.Request.Method == "GET")
            {
                byte[] bytes = await vault.ReadChunkAsync(id, clientId, key, offset, token);
                context.Response.StatusCode = offset == 0 ? 200 : 206;
                context.Response.ContentType = "application/octet-stream"; context.Response.ContentLength = bytes.Length;
                await context.Response.Body.WriteAsync(bytes, token); return;
            }
            if (context.Request.Method == "PUT")
            {
                if (context.Request.ContentLength is not > 0 or > FileTransferVault.ChunkBytes) throw new TransferError(413, "文件块必须声明长度且不能超过256KiB。");
                var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = FileTransferVault.ChunkBytes;
                byte[] bytes = new byte[(int)context.Request.ContentLength.Value];
                await context.Request.Body.ReadExactlyAsync(bytes, token);
                if (await context.Request.Body.ReadAsync(new byte[1], token) != 0) throw new TransferError(413, "文件块长度不符。");
                await Reply(context, new { success = true, task = Public(await vault.AppendAsync(id, clientId, key, offset, bytes, token)) }, token); return;
            }
            throw new TransferError(404, "文件接口不存在。");
        }
        catch (TransferError error) { context.Response.StatusCode = error.Status; await Reply(context, new { success = false, message = error.Message }, token); }
        catch (EndOfStreamException) { context.Response.StatusCode = 400; await Reply(context, new { success = false, message = "文件块未接收完整，请继续传输。" }, token); }
        catch (UnauthorizedAccessException) { context.Response.StatusCode = 403; await Reply(context, new { success = false, message = "电脑无法读取此共享目录或文件。" }, token); }
        catch (IOException) { context.Response.StatusCode = 409; await Reply(context, new { success = false, message = "共享文件已改变或暂时不可读，请刷新后重试。" }, token); }
    }
    private void StartCompletion(TransferItem item, string clientId, string key, CancellationToken stopping)
    {
        lock (_completionGate)
        {
            if (_completions.TryGetValue(item.Id, out var existing))
            {
                if (!existing.Work.IsCompleted) return;
                existing.Cancellation.Dispose(); _completions.Remove(item.Id);
                item = vault.Get(item.Id, clientId, key);
                if (item.State == "completed") return;
            }
            if (item.State != "receiving" || item.Offset != item.Size) throw new TransferError(409, "文件尚未完整接收。");
            // A completed, unread result is no longer needed once its task has been removed from the vault.
            var live = vault.Snapshot().Items.Select(x => x.Id).ToHashSet();
            foreach (var stale in _completions.Where(x => x.Value.Work.IsCompleted && !live.Contains(x.Key)).Select(x => x.Key).ToArray())
            { _completions[stale].Cancellation.Dispose(); _completions.Remove(stale); }
            var job = new CompletionJob(stopping);
            job.Work = Task.Run(async () =>
            {
                try { await vault.CompleteAsync(item.Id, clientId, key, job.Cancellation.Token).ConfigureAwait(false); return new CompletionResult(true, "文件已保存。"); }
                catch (TransferError error) { return new CompletionResult(false, error.Message); }
                catch (OperationCanceledException) { return new CompletionResult(false, "文件保存已中断，可继续重试。"); }
                catch (IOException) { return new CompletionResult(false, "电脑保存文件失败，请检查接收目录和磁盘空间。"); }
                catch (UnauthorizedAccessException) { return new CompletionResult(false, "电脑无法写入接收目录，请检查权限。"); }
                catch { return new CompletionResult(false, "电脑保存文件失败，请重试。"); }
            });
            _completions[item.Id] = job;
        }
    }
    private (string State, string Message)? ReadCompletion(string id)
    {
        lock (_completionGate)
        {
            if (!_completions.TryGetValue(id, out var job)) return null;
            if (!job.Work.IsCompleted) return ("running", "电脑正在校验并保存文件。");
            var result = job.Work.GetAwaiter().GetResult();
            job.Cancellation.Dispose(); _completions.Remove(id);
            return (result.Success ? "completed" : "failed", result.Message);
        }
    }
    private void CancelCompletion(string id)
    {
        lock (_completionGate) if (_completions.TryGetValue(id, out var job) && !job.Work.IsCompleted) job.Cancellation.Cancel();
    }
    private static Task Reply(HttpContext context, object result, CancellationToken token) => context.Response.WriteAsJsonAsync(result, token);
    private static async Task<T> JsonAsync<T>(HttpContext context, CancellationToken token)
    {
        if (context.Request.ContentType?.Split(';')[0] != "application/json" || context.Request.ContentLength is null or < 1 or > 8192) throw new TransferError(413, "文件元数据必须是8KiB以内的JSON。");
        byte[] bytes = new byte[(int)context.Request.ContentLength.Value]; await context.Request.Body.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, RuntimeProtocol.JsonOptions) ?? throw new TransferError(400, "文件信息无效。");
    }
}
