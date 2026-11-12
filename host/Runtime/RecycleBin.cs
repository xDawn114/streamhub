using System.Runtime.InteropServices;
using System.Security.Principal;
using System.IO;
using System.Threading;

namespace StreamHub.Host.Runtime;

/// <summary>Shell recycle only. No DeleteFile/SHFileOperation permanent-delete fallback.</summary>
public static class RecycleBin
{
    public static void Move(string path)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2) || WindowsIdentity.GetCurrent().IsSystem ||
            !Path.IsPathFullyQualified(path) || new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed ||
            (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new TransferError(403, "此位置不支持安全回收，已取消删除。");
        var bin = new RecycleInformation { Size = (uint)Marshal.SizeOf<RecycleInformation>() };
        if (SHQueryRecycleBin(Path.GetPathRoot(path)!, ref bin) < 0)
            throw new TransferError(403, "当前用户无法访问此磁盘的回收站，已保留源文件。");
        Exception? failure = null;
        using var identity = WindowsIdentity.GetCurrent();
        var thread = new Thread(() => { try { WindowsIdentity.RunImpersonated(identity.AccessToken, () => MoveOnSta(path)); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new TransferError(409, "无法移入电脑回收站，未尝试永久删除。", failure);
    }

    private static void MoveOnSta(string path)
    {
        IShellItem? item = null;
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(
            new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
        try
        {
            var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
            // Windows 8+ RECYCLEONDELETE, EARLYFAILURE, NOERRORUI, SILENT,
            // NOCONFIRMATION and ADDUNDORECORD. RECYCLEONDELETE is the Windows
            // 8+ recycle request; no retry with weaker flags is allowed.
            operation.SetOperationFlags(0x00080000 | 0x00100000 | 0x400 | 4 | 0x10 | 0x40 | 0x2000 | 0x20000000);
            var sink = new RecycleOnlySink();
            operation.Advise(sink, out uint cookie);
            operation.DeleteItem(item, null);
            try { operation.PerformOperations(); }
            catch (COMException error) { throw new IOException("Shell recycle failed: " + sink.Refusal, error); }
            operation.GetAnyOperationsAborted(out bool aborted);
            operation.Unadvise(cookie);
            if (aborted || !sink.Recycled) throw new IOException("Shell did not confirm recycling.");
            GC.KeepAlive(sink);
        }
        finally
        {
            if (item is not null) Marshal.ReleaseComObject(item);
            Marshal.ReleaseComObject(operation);
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class RecycleOnlySink : IFileOperationProgressSink
    {
        public bool Recycled { get; private set; }
        public string Refusal { get; private set; } = "none";
        private int Refuse(string operation, uint flags) { Refusal = operation + " flags=" + flags.ToString("X"); return unchecked((int)0x80004004); }
        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint f, object i, string n) => Refuse("rename", f);
        public int PostRenameItem(uint f, object i, string n, int result, object? created) => 0;
        public int PreMoveItem(uint f, object i, object d, string n) => Refuse("move", f);
        public int PostMoveItem(uint f, object i, object d, string n, int result, object? created) => 0;
        public int PreCopyItem(uint f, object i, object d, string n) => Refuse("copy", f);
        public int PostCopyItem(uint f, object i, object d, string n, int result, object? created) => 0;
        // Refuse when Shell cannot establish a recyclable operation. Do not
        // relax this check or retry permanently when a location lacks a bin.
        public int PreDeleteItem(uint flags, IShellItem item) => (flags & 0x80) != 0 ? 0 : Refuse("delete", flags);
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created)
        {
            // Shell can report an intermediate DONT_PROCESS_CHILDREN result
            // before completing the actual recycled item. Preserve that final
            // item's confirmation instead of cancelling the intermediate step.
            if (result >= 0 && created is not null) Recycled = true;
            if (result < 0) { Refusal = "delete result=" + result.ToString("X"); return result; }
            return 0;
        }
        public int PreNewItem(uint f, object d, string n) => unchecked((int)0x80004004);
        public int PostNewItem(uint f, object d, string n, string t, uint a, int result, object? created) => 0;
        public int UpdateProgress(uint total, uint complete) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IFileOperationProgressSink sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog([MarshalAs(UnmanagedType.Interface)] object dialog);
        void SetProperties([MarshalAs(UnmanagedType.Interface)] object properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] object item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.Interface)] object items);
        void RenameItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        void RenameItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        void MoveItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.Interface)] object destination);
        void CopyItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        void CopyItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.Interface)] object destination);
        void DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
        void DeleteItems([MarshalAs(UnmanagedType.Interface)] object items);
        void NewItem([MarshalAs(UnmanagedType.Interface)] object destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IFileOperationProgressSink sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, [MarshalAs(UnmanagedType.Interface)] object? created);
        [PreserveSig] int PreMoveItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, [MarshalAs(UnmanagedType.Interface)] object? created);
        [PreserveSig] int PreCopyItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, [MarshalAs(UnmanagedType.Interface)] object? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
        [PreserveSig] int PreNewItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, [MarshalAs(UnmanagedType.Interface)] object? created);
        [PreserveSig] int UpdateProgress(uint total, uint complete);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    // An IUnknown pointer is not an IShellItem pointer. Explicit interface
    // marshaling is required because IFileOperation calls the item's vtable.
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleInformation { public uint Size; public long Bytes, Items; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string root, ref RecycleInformation information);
}
