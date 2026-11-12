using System.Net;
using System.Text;
using System.Text.Json;
using StreamHub.Host.Runtime;
using StreamHub.Service;

namespace StreamHub.Host.Runtime
{
    // In-memory IPC boundary: never opens the installed service pipe or desktop.
    public sealed class RuntimeClient
    {
        public int Calls;
        public Func<RuntimeRequest, CancellationToken, Task<RuntimeResponse>> Dispatch = (_, _) => throw new InvalidOperationException("Unexpected IPC");
        public Task<RuntimeResponse> SendAsync(RuntimeRequest request, CancellationToken token = default)
        { Calls++; return Dispatch(request, token); }
        public Task<RuntimeResponse> GetStatusAsync(CancellationToken token = default) => SendAsync(new("status"), token);
    }
    public sealed record ControlApiIdentity(byte[] Pfx, byte[] Token);
    public readonly record struct HostDisplayMode(int Width, int Height, int RefreshRate)
    { public static HostDisplayMode ReadPrimary() => throw new InvalidOperationException("Desktop access forbidden in isolated checks"); }
}

namespace MediaTrustChecks
{
    internal sealed record FixtureClient(string Uuid, string Pem, bool Enabled = true,
        bool MissingEnabled = false, object? StoredEnabled = null);

    internal sealed class Fixture : HttpMessageHandler
    {
        internal readonly string Directory;
        internal readonly ProfileStore Profiles;
        internal readonly BackendApi Backend;
        internal readonly Dictionary<string, List<FixtureClient>> Clients = new() { ["remote"] = [], ["tablet"] = [] };
        internal readonly Dictionary<string, string> StateOverrides = new();
        internal readonly Dictionary<string, string> ServerCertificates;
        internal readonly List<(string Profile, string Method, string Path, string? Body)> Requests = [];
        internal readonly List<string> Deleted = [];
        internal bool CanRepair = true, NoOpDelete, FailDelete, MissingCsrf;
        internal int GuardCalls, ListCalls;
        internal string? ApiOverride;
        internal HttpStatusCode ApiStatus = HttpStatusCode.OK;
        internal Action<Fixture, string>? BeforeList;
        internal Action<Fixture, string, string>? AfterDelete;
        internal Func<int, bool>? GuardResult;
        internal Func<string, CancellationToken, Task>? BeforeRequest;

        internal Fixture(string artifacts, string remotePem, string tabletPem)
        {
            Directory = Path.Combine(artifacts, Guid.NewGuid().ToString("N"));
            Profiles = new ProfileStore(Directory, Directory);
            ServerCertificates = new() { ["remote"] = remotePem, ["tablet"] = tabletPem };
            foreach (var mode in Clients.Keys)
            {
                System.IO.Directory.CreateDirectory(Path.Combine(Profiles.DirectoryFor(mode), "credentials"));
                File.WriteAllText(Profiles.CertificateFor(mode), ServerCertificates[mode]);
                Save(mode);
            }
            Backend = new BackendApi(Profiles, (mode, _) => Task.FromResult(new HttpClient(this, disposeHandler: false)
            { BaseAddress = new Uri("https://fixture-" + mode + ".invalid/") }));
        }

        internal void Save(string mode)
        {
            var rows = Clients[mode].Select(client =>
            {
                var row = new Dictionary<string, object?> { ["uuid"] = client.Uuid, ["cert"] = client.Pem,
                    ["name"] = "same visible name" };
                if (!client.MissingEnabled) row["enabled"] = client.StoredEnabled ?? (client.Enabled ? "true" : "false");
                return row;
            }).ToArray();
            var json = StateOverrides.GetValueOrDefault(mode) ?? JsonSerializer.Serialize(new
            {
                username = "isolated-fixture-user", password = "fixture-secret-must-not-escape",
                root = new { uniqueid = "11111111-1111-4111-8111-111111111111", named_devices = rows }
            });
            File.WriteAllText(Path.Combine(Profiles.DirectoryFor(mode), "state.json"), json);
        }

        internal Task<bool> Guard(CancellationToken _)
        { GuardCalls++; return Task.FromResult(GuardResult?.Invoke(GuardCalls) ?? CanRepair); }

        internal Task<MediaIdentityResult> Inspect(string certificate, string mode = "remote", CancellationToken token = default) =>
            Backend.GetMediaIdentityAsync(mode, certificate, Guard, token);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var mode = request.RequestUri!.Host == "fixture-remote.invalid" ? "remote" :
                request.RequestUri.Host == "fixture-tablet.invalid" ? "tablet" : throw new Exception("Wrong profile host");
            var path = request.RequestUri.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            Requests.Add((mode, request.Method.Method, path, body));
            if (BeforeRequest is not null) await BeforeRequest(path, token);
            if (path == "/api/clients/list" && request.Method == HttpMethod.Get)
            {
                ListCalls++; BeforeList?.Invoke(this, mode);
                return Json(ApiOverride ?? JsonSerializer.Serialize(new { status = true,
                    named_certs = Clients[mode].Select(x => new { name = "same visible name", uuid = x.Uuid, enabled = x.Enabled }).ToArray() }), ApiStatus);
            }
            if (path == "/api/csrf-token" && request.Method == HttpMethod.Get)
                return Json(MissingCsrf ? "{}" : "{\"csrf_token\":\"isolated-csrf\"}");
            if (path == "/api/clients/unpair" && request.Method == HttpMethod.Post)
            {
                if (!request.Headers.TryGetValues("X-CSRF-Token", out var values) || values.Single() != "isolated-csrf")
                    throw new Exception("CSRF required");
                using var input = JsonDocument.Parse(body!);
                if (input.RootElement.EnumerateObject().Count() != 1) throw new Exception("Only exact UUID may be sent");
                var uuid = input.RootElement.GetProperty("uuid").GetString()!;
                if (FailDelete) return Json("{\"status\":false}");
                Deleted.Add(uuid);
                if (!NoOpDelete) Clients[mode].RemoveAll(x => x.Uuid == uuid);
                Save(mode); AfterDelete?.Invoke(this, mode, uuid);
                return Json("{\"status\":true}");
            }
            throw new Exception("Forbidden backend operation: " + request.Method + " " + path);
        }

        private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
