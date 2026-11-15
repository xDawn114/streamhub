// Compile the real partial without running the production service or using a live LocalAPI.
namespace StreamHub.Service
{
    internal sealed partial class HostRuntime
    {
        private readonly string _root = @"C:\Program Files\StreamHub";
        private readonly string _data = @"C:\NeverUsedByFirewallTests";
        private readonly bool _interactive = true;
        private readonly HarnessDaemon? _network = null;
        private readonly HarnessClient _networkClient = new();
        private readonly HarnessSettings _transportSettings = new();
        private void Log(string _) => throw new Exception("Test unexpectedly attempted service logging");
        internal NetworkAccessResult ReadForTest()
        {
            var state = ReadNetworkAccessState();
            return new(state.ConfigurationValid, state.FirewallApplied);
        }
        internal Task<StreamHub.Host.Runtime.RuntimeResponse> RefreshForTest() => RefreshNetworkAccessAsync(CancellationToken.None);
    }
    internal sealed record NetworkAccessResult(bool ConfigurationValid, bool FirewallApplied);
    internal sealed class HarnessDaemon { public bool Running => throw new Exception("No live daemon in tests"); }
    internal sealed class HarnessSettings { public string Load() => throw new Exception("No live preferences in tests"); }
    internal sealed class HarnessClient
    {
        public Task<HarnessStatus> GetStatusAsync(CancellationToken _, string __) => throw new Exception("No LocalAPI in tests");
    }
    internal sealed record HarnessStatus(bool Available, bool LoggedIn, string State, string[] Addresses);
}
namespace StreamHub.Host.Runtime
{
    public sealed record RuntimeResponse(bool Success, string Message, string? PayloadJson = null);
    public static class RuntimeProtocol
    {
        public static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);
    }
}
