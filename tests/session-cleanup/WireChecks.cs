using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using StreamHub.Host.Runtime;

internal static class WireChecks
{
    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
        var requests = new List<RuntimeRequest>();
        var devices = new ControlDeviceRegistry();
        var owner = devices.Issue("cleanup-client-owner", "fixture device");
        var other = devices.Issue("other-device-client", "other fixture");
        var status = new RuntimeStatus("fixture-pc", "fixture", new("remote", true, true, 42, 48989, true, "pending", BackendReleasePending:true),
            new("tablet", false, false, null, 49089, false, "stopped"), new(false, false, null, "stopped"), "fixture", "fixture", DateTimeOffset.UtcNow);
        int releaseStatus = 200;
        bool obsolete = false;
        var ipc = new RuntimeClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(request.EndBackend == true
                ? new RuntimeResponse(releaseStatus == 200, "fixture result", PayloadJson: JsonSerializer.Serialize(new {
                    leaseId=request.LeaseId,clientId=request.ClientId,completed=!obsolete,obsolete,alreadyCompleted=!obsolete }), ErrorStatus:releaseStatus)
                : new RuntimeResponse(true, "fixture result", status));
        });
        await using var api = new ControlApiServer(ipc, (_, _, _) => throw new Exception("Desktop forbidden"),
            new ControlApiIdentity([], []), X509KeyStorageFlags.EphemeralKeySet, deviceRegistry:devices);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ControlApiServer).GetField("_token", flags)!.SetValue(api, RandomNumberGenerator.GetBytes(32));
        var handle = typeof(ControlApiServer).GetMethod("HandleAsync", flags)!;
        async Task<(int Status, string Body)> Send(string path, object? body, byte[]? credential = null)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.129");
            context.Request.Method = body is null ? "GET" : "POST";
            context.Request.Path = path;
            context.Request.Headers.Authorization = "Bearer " + Convert.ToHexString(credential ?? owner.Token);
            if (body is not null)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = bytes.Length;
                context.Request.Body = new MemoryStream(bytes);
            }
            context.Response.Body = new MemoryStream();
            await (Task)handle.Invoke(api, [context])!;
            return (context.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        }
        var info = await Send("/v1/info", null);
        using (var json = JsonDocument.Parse(info.Body))
            Check(info.Status == 200 && json.RootElement.GetProperty("capabilities").GetProperty("remoteBackendRelease").GetBoolean() &&
                json.RootElement.GetProperty("remote").GetProperty("backendReleasePending").GetBoolean(), "info negotiates capability and exposes pending state");
        requests.Clear();
        var body = new {mode="remote",leaseId=Fixture.Lease,clientId=Fixture.Client,endBackend=true};
        var ended = await Send("/v1/session/end", body);
        using (var json = JsonDocument.Parse(ended.Body))
        {
            var receipt = json.RootElement.GetProperty("backendRelease");
            Check(ended.Status == 200 && receipt.GetProperty("leaseId").GetString() == Fixture.Lease &&
                receipt.GetProperty("clientId").GetString() == Fixture.Client && receipt.GetProperty("completed").GetBoolean() &&
                !receipt.GetProperty("obsolete").GetBoolean(),
                "HTTP end returns exact completion receipt without a current-idle claim");
        }
        Check(requests.Single().EndBackend == true && requests[0].ControllerCredentialId == owner.Principal.Id,
            "flagged end forwards authenticated device identity");
        requests.Clear();
        obsolete = true;
        var old = await Send("/v1/session/end", body);
        using (var json = JsonDocument.Parse(old.Body))
        {
            var receipt = json.RootElement.GetProperty("backendRelease");
            Check(old.Status == 200 && receipt.GetProperty("leaseId").GetString() == Fixture.Lease &&
                receipt.GetProperty("clientId").GetString() == Fixture.Client && !receipt.GetProperty("completed").GetBoolean() &&
                receipt.GetProperty("obsolete").GetBoolean(), "HTTP carries exact boolean obsolete/completed alternatives");
            Check(!json.RootElement.TryGetProperty("remote", out _) && !json.RootElement.TryGetProperty("status", out _),
                "obsolete response contains no current backend idle claim");
        }
        obsolete = false;
        requests.Clear();
        foreach (var invalid in new[] {
            ("/v1/session/heartbeat", "remote", true), ("/v1/session/heartbeat", "remote", false),
            ("/v1/session/end", "tablet", true), ("/v1/session/end", "tablet", false) })
            Check((await Send(invalid.Item1, new {mode=invalid.Item2,leaseId=Fixture.Lease,clientId=Fixture.Client,endBackend=invalid.Item3})).Status == 400,
                "endBackend is restricted to remote session/end");
        Check(requests.Count == 0, "invalid flags never reach runtime");
        Check((await Send("/v1/session/end", body, other.Token)).Status == 403 && requests.Count == 0,
            "other credential cannot claim the original client");
        var legacy = await Send("/v1/session/end", new {mode="remote",leaseId=Fixture.Lease,clientId=Fixture.Client});
        Check(legacy.Status == 200 && requests.Single().EndBackend is null, "legacy/preparation cancellation remains unflagged");
        requests.Clear();
        releaseStatus = 504;
        Check((await Send("/v1/session/end", body)).Status == 504, "cleanup timeout status reaches Android truthfully");
        return checks;
    }
}
