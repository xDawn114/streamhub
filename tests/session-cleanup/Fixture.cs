using System.Net;
using System.Reflection;
using System.Text;
using StreamHub.Host.Runtime;
using StreamHub.Service;

internal sealed class Fixture : HttpMessageHandler
{
    internal const string Lease = "cleanup-lease-owner", Client = "cleanup-client-owner";
    internal readonly LiveSessionState Session = new();
    internal readonly RemoteBackendRelease Release;
    internal readonly BackendApi Backend;
    internal object Engine = new();
    internal bool Alive = true, Free = true, LostResponse;
    internal string Csrf = "{\"csrf_token\":\"fixture-csrf\"}", Ack = "{\"status\":true}";
    internal HttpStatusCode CloseStatus = HttpStatusCode.OK;
    internal int Posts, Probes;
    internal Func<CancellationToken, Task>? BeforeClose;
    internal Action? AtProbe;

    internal Fixture(string root, bool ready = true, string? receiptPath = null)
    {
        Release = new(receiptPath);
        var profiles = new ProfileStore(root, root);
        Backend = new BackendApi(profiles, releaseClientFactory: (profile, _) =>
        {
            if (profile != "remote") throw new Exception("Wrong backend profile");
            var http = new HttpClient(this, false) { BaseAddress = new Uri("https://fixture.invalid/") };
            http.DefaultRequestHeaders.Authorization = new("Basic", "Zml4dHVyZTpzZWNyZXQ=");
            http.DefaultRequestHeaders.Add("Origin", "https://fixture.invalid");
            return Task.FromResult(http);
        });
        Session.Prepare(Lease, Client, DateTimeOffset.UtcNow);
        if (ready) Session.Heartbeat(Lease, Client, true, DateTimeOffset.UtcNow);
    }

    internal Task<BackendReleaseResult> End(string lease = Lease, string client = Client, bool shouldLock = false,
        CancellationToken token = default) => Release.EndAsync(Session, lease, client, Engine, () => Alive,
        (sent, completed, operation) => Backend.CloseAppAsync("remote", sent, completed, operation),
        _ => { Probes++; AtProbe?.Invoke(); return Task.FromResult(Free); }, token, shouldLock,
        (_, _) => Task.CompletedTask);

    internal void Expire() => typeof(LiveSessionState).GetField("_expires", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(Session, DateTimeOffset.UtcNow.AddSeconds(-1));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri!.Host != "fixture.invalid") throw new Exception("Real network is forbidden");
        if (request.RequestUri.AbsolutePath == "/api/csrf-token" && request.Method == HttpMethod.Get) return Json(Csrf);
        if (request.RequestUri.AbsolutePath != "/api/apps/close" || request.Method != HttpMethod.Post)
            throw new Exception("Unexpected backend operation");
        if (!request.Headers.TryGetValues("X-CSRF-Token", out var csrf) || csrf.Single() != "fixture-csrf" ||
            request.Headers.Authorization?.Scheme != "Basic" || !request.Headers.Contains("Origin") ||
            await request.Content!.ReadAsStringAsync(token) != "{}") throw new Exception("Invalid close authentication/body");
        Posts++;
        if (BeforeClose is not null) await BeforeClose(token);
        if (LostResponse) throw new HttpRequestException("synthetic secret must not escape");
        return Json(Ack, CloseStatus);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
