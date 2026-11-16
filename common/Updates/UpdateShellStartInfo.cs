using System.Diagnostics;
using System.IO;

namespace StreamHub.Updates;

public static class UpdateShellStartInfo
{
    public static ProcessStartInfo Create(string executable, IEnumerable<string> arguments, string installRoot, IntPtr owner)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
            WindowStyle = ProcessWindowStyle.Normal,
            ErrorDialog = true,
            ErrorDialogParentHandle = owner
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--install-dir");
        start.ArgumentList.Add(Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar));
        return start;
    }
}
