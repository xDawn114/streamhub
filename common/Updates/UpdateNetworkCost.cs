using System.Runtime.InteropServices;

namespace StreamHub.Updates;

public static class UpdateNetworkCost
{
    public static bool Unmetered()
    {
        object? manager = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            manager = type is null ? null : Activator.CreateInstance(type);
            return manager is INetworkCostManager cost && cost.GetCost(out var flags, IntPtr.Zero) == 0 && flags == 1;
        }
        catch (Exception e) when (e is COMException or PlatformNotSupportedException or InvalidCastException) { return false; }
        finally { if (manager is not null && Marshal.IsComObject(manager)) Marshal.ReleaseComObject(manager); }
    }
    [ComImport, Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        [PreserveSig] int GetCost(out uint cost, IntPtr destinationAddress);
    }
}
