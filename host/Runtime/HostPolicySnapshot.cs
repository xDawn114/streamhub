namespace StreamHub.Host.Runtime;

/// <summary>Non-secret, service-owned policy. Safe to return to an authenticated receiver.</summary>
public sealed record HostPolicySnapshot(long Revision = 0, bool PrivacyScreenEnabled = false,
    bool LockOnExit = false, bool AutomaticLoginEnabled = false, bool AutomaticLoginSubmit = false,
    bool PasswordConfigured = false, string PreventSleepMode = "streaming", bool AutomaticLoginAvailable = false);
