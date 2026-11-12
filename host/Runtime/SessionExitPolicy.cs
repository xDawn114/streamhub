namespace StreamHub.Host.Runtime;

public static class SessionExitPolicy
{
    public static bool ShouldLock(bool matchingLiveRemoteSession, string? exitReason, bool? lockOnExit) =>
        matchingLiveRemoteSession && exitReason == "user" && lockOnExit == true;
}
