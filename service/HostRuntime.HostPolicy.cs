using System.Text.Json;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

internal sealed partial class HostRuntime
{
    private IHostLoginSecretStore? _hostLoginSecrets;
    internal Func<string?, bool, CancellationToken, Task<(DesktopInputState Desktop, LoginInputResult Login)>>? DesktopInputRequest { get; set; }
    internal Func<CancellationToken, Task>? LockDesktopRequest { get; set; }
    private bool _lockOnExitEnabled, _automaticLoginEnabled, _automaticLoginSubmit;
    private long _policyRevision;
    private string? _privacySuppressedLease;

    private HostPolicySnapshot HostPolicy() => new(_policyRevision, _privacyScreenEnabled, _lockOnExitEnabled,
        _automaticLoginEnabled && _hostLoginSecrets?.Configured == true, _automaticLoginSubmit,
        _hostLoginSecrets?.Configured == true, _preventSleepMode, _hostLoginSecrets is not null);

    // These commands have no HTTP routes. The runtime pipe authenticates the installer owner,
    // SYSTEM or a local administrator; HTTP receivers can only read HostPolicy().
    private async Task<RuntimeResponse> HandleHostPolicyAsync(RuntimeRequest request, CancellationToken token)
    {
        if (request.Command == "host-policy-status")
            return new(true, "电脑策略已读取。", PayloadJson: JsonSerializer.Serialize(HostPolicy(), RuntimeProtocol.JsonOptions));
        if (request.PolicyRevision != _policyRevision)
            return new(false, "电脑设置已更新，请重新读取后保存。", PayloadJson: JsonSerializer.Serialize(HostPolicy(), RuntimeProtocol.JsonOptions));
        if (request.Command == "host-policy-save")
        {
            bool privacy = request.PrivacyEnabled ?? _privacyScreenEnabled;
            bool automatic = request.AutomaticLoginEnabled ?? _automaticLoginEnabled;
            if (automatic && _hostLoginSecrets?.Configured != true)
                return new(false, "请先在这台电脑保存 Windows 密码。");
            var previous = (_privacyScreenEnabled, _lockOnExitEnabled, _automaticLoginEnabled, _automaticLoginSubmit);
            _privacyScreenEnabled = privacy;
            _lockOnExitEnabled = request.LockOnExit ?? _lockOnExitEnabled;
            _automaticLoginEnabled = automatic;
            _automaticLoginSubmit = request.LoginSubmit ?? _automaticLoginSubmit;
            try { await SaveDesiredAsync(token); }
            catch { (_privacyScreenEnabled, _lockOnExitEnabled, _automaticLoginEnabled, _automaticLoginSubmit) = previous; throw; }
            if (request.PrivacyEnabled is not null)
            {
                _privacyScreenFailures = 0; _privacyScreenRetryAt = default;
                _privacySuppressedLease = null;
            }
            await ReconcilePrivacyScreenAsync(token);
        }
        else if (request.Command == "host-policy-password-save")
        {
            if (_hostLoginSecrets is null) return new(false, "保存密码需要已安装的 Windows 后台服务。");
            _hostLoginSecrets.Save(request.LoginSecret ?? "");
            await SaveDesiredAsync(token);
        }
        else if (request.Command == "host-policy-password-clear")
        {
            var previous = (_automaticLoginEnabled, _automaticLoginSubmit);
            _automaticLoginEnabled = _automaticLoginSubmit = false;
            try { await SaveDesiredAsync(token); }
            catch { (_automaticLoginEnabled, _automaticLoginSubmit) = previous; throw; }
            _hostLoginSecrets?.Clear();
        }
        else return new(false, "无法识别此电脑设置操作。");
        return new(true, "电脑设置已保存。", PayloadJson: JsonSerializer.Serialize(HostPolicy(), RuntimeProtocol.JsonOptions));
    }

    private async Task<(DesktopInputState Desktop, LoginInputResult Login)> ReadHostDesktopAsync(
        string lease, string client, bool requestFill, CancellationToken token)
    {
        if (!_remoteSession.IsStreaming(lease, client, DateTimeOffset.UtcNow))
            throw new InvalidOperationException("当前远控会话已结束。");
        var login = new LoginInputResult("status");
        if (DateTimeOffset.UtcNow - _desktopInputReadAt > TimeSpan.FromMilliseconds(600))
        {
            var observed = await RequestDesktopInputAsync(null, false, token);
            _desktopInputState = observed.Desktop; _desktopInputReadAt = DateTimeOffset.UtcNow;
        }
        if (!_automaticLoginEnabled || _hostLoginSecrets?.Configured != true)
            return (_desktopInputState, new(requestFill ? "disabled-on-host" : "status"));
        if (!_desktopInputState.Known || !_desktopInputState.Locked || !_desktopInputState.PasswordFocused)
            return (_desktopInputState, new(requestFill ? "not-ready" : "status"));
        if (!_remoteSession.IsStreaming(lease, client, DateTimeOffset.UtcNow))
            throw new InvalidOperationException("当前远控会话已结束。");
        if (!_loginInputUsed.Add(lease)) return (_desktopInputState, new("already-attempted"));
        try
        {
            // Read only after the live lease and Windows password control were verified.
            // The existing helper rechecks owner SID, LogonUI, secure desktop and focus
            // before each input batch. Never send the password back to the receiver.
            var observed = await RequestDesktopInputAsync(_hostLoginSecrets.Read(), _automaticLoginSubmit, token);
            _desktopInputState = observed.Desktop; _desktopInputReadAt = DateTimeOffset.UtcNow;
            login = observed.Login;
            if (login.State == "not-ready") _loginInputUsed.Remove(lease);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { login = new("unavailable"); } // Uncertain input stays consumed for this lease; no retry or secret logging.
        return (_desktopInputState, login);
    }

    private Task<(DesktopInputState Desktop, LoginInputResult Login)> RequestDesktopInputAsync(
        string? password, bool submit, CancellationToken token) => DesktopInputRequest is not null
        ? DesktopInputRequest(password, submit, token)
        : DesktopInputProbe.RequestAsync(_root, _interactive,
            _ownerSid?.Value ?? throw new InvalidOperationException("授权的 Windows 用户不可用。"), password, submit, token);
}
