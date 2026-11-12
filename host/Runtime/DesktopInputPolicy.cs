namespace StreamHub.Host.Runtime;

public sealed record DesktopHitRegion(double Left, double Top, double Right, double Bottom);
public sealed record DesktopInputState(bool Known = false, bool Locked = false, bool PasswordFocused = false,
    DesktopHitRegion[]? CaptionButtons = null);
public sealed record LoginInputResult(string State, bool Filled = false, bool Submitted = false);

public static class DesktopInputPolicy
{
    public static void ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 256 || password.Any(char.IsControl))
            throw new ArgumentException("请输入1至256个字符的 Windows 密码，不能包含控制字符。");
    }

    public static bool CanFill(bool sameUser, bool locked, bool secureDesktop, bool trustedLogonProcess,
        bool passwordControl, bool focused, string? controlName) => sameUser && locked && secureDesktop &&
        trustedLogonProcess && passwordControl && focused && controlName is not null &&
        !controlName.Contains("PIN", StringComparison.OrdinalIgnoreCase) &&
        (controlName.Contains("密码", StringComparison.Ordinal) || controlName.Contains("password", StringComparison.OrdinalIgnoreCase));
}
