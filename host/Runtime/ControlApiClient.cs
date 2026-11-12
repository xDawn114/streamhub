using System.Security.Cryptography;
using System.Text.Json;

namespace StreamHub.Host.Runtime;

/// <summary>Owner-only local management of the service-owned device API.</summary>
public sealed class ControlApiClient(RuntimeClient runtime)
{
    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    private string? _invitation;

    public string GetInvitation() => IsRunning && _invitation is not null ? _invitation :
        throw new InvalidOperationException(LastError ?? "设备绑定接口尚未启动。");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var status = await ReadAsync(cancellationToken);
        if (status?.IdentityPresent == false) await ImportIdentityAsync(cancellationToken);
        else if (status is null && !IsRunning) { LastError ??= "后台服务尚未连接。"; return; }
        await ReadAsync(cancellationToken);
        if (!IsRunning) throw new InvalidOperationException(LastError ?? "设备绑定接口尚未启动。");
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var status = await ReadAsync(cancellationToken);
        // A service that started before the desktop shell does not yet have a
        // CurrentUser DPAPI identity. Retry the one-time migration on a later UI
        // refresh instead of requiring the user to restart the product.
        if (status?.IdentityPresent == false) await ImportIdentityAsync(cancellationToken);
        await ReadAsync(cancellationToken);
    }

    private async Task ImportIdentityAsync(CancellationToken cancellationToken)
    {
        // CurrentUser DPAPI must be decrypted by the owner, never by SYSTEM.
        // This pipe already restricts callers to the registered owner/SYSTEM/Admin.
        var identity = ControlApiIdentity.LoadOrCreateUser();
        var plain = JsonSerializer.SerializeToUtf8Bytes(identity, RuntimeProtocol.JsonOptions);
        try
        {
            var response = await runtime.SendAsync(new("control-import", ControlIdentity: Convert.ToBase64String(plain)), cancellationToken);
            if (!response.Success) throw new InvalidOperationException(response.Message);
        }
        finally { identity.Clear(); CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task<ControlApiStatus?> ReadAsync(CancellationToken cancellationToken)
    {
        var response = await runtime.SendAsync(new("control-info"), cancellationToken);
        var status = response.Success && response.PayloadJson is not null
            ? JsonSerializer.Deserialize<ControlApiStatus>(response.PayloadJson, RuntimeProtocol.JsonOptions) : null;
        IsRunning = status?.Running == true;
        _invitation = status?.Invitation;
        LastError = IsRunning ? null : status?.LastError ?? response.Message;
        return status;
    }
}
