using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StreamHub.Host.Runtime;

public sealed record SharedFileEntry(string Name, string Path, bool Directory, long Size, DateTime ModifiedUtc);
public sealed record SharedFileListing(string Path, string RootName, SharedFileEntry[] Entries, int? NextOffset);

/// <summary>Relative paths only. Directory handles pin the tree while reading; reparse points are never followed.</summary>
internal sealed class SharedPathLease : IDisposable
{
    private readonly List<SafeFileHandle> _handles = new();
    public string FullPath { get; }
    public string RelativePath { get; }

    internal SharedPathLease(string root, string relative, bool directory)
    {
        if (!OperatingSystem.IsWindows()) throw new TransferError(503, "共享文件浏览需要 Windows。");
        if (relative is null) throw new TransferError(400, "共享路径不能为空。");
        relative = relative.Replace('\\', '/');
        if (relative.Length > 2048 || Path.IsPathRooted(relative) || relative.StartsWith('/') ||
            (relative.Length > 0 && relative.Split('/').Any(x => !FileTransferVault.ValidName(x))))
            throw new TransferError(400, "共享路径无效，请从共享文件夹开始浏览。");
        RelativePath = relative;
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        FullPath = relative.Length == 0 ? fullRoot : Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            Pin(fullRoot, true);
            var parts = relative.Length == 0 ? Array.Empty<string>() : relative.Split('/');
            var current = fullRoot;
            for (int i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                Pin(current, i < parts.Length - 1 || directory);
            }
        }
        catch { Dispose(); throw; }
    }

    private void Pin(string path, bool directory)
    {
        // OPEN_REPARSE_POINT prevents following a swapped symlink. Denying share-delete pins the ancestor.
        var handle = CreateFile(path, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new TransferError(404, "文件不存在或无法访问，请刷新共享目录。"); }
        _handles.Add(handle);
        if (!GetFileInformationByHandle(handle, out var information) ||
            (information.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            ((information.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
            throw new TransferError(403, "共享目录不允许访问链接、挂载点或类型已改变的文件。");
        ValidateFinalPath(handle, path);
    }

    internal static void ValidateFinalPath(SafeFileHandle handle, string expected)
    {
        var value = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandle(handle, value, (uint)value.Capacity, 0);
        if (count == 0 || count >= value.Capacity) throw new TransferError(403, "无法核实共享路径。");
        var actual = value.ToString();
        if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
        else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(Path.TrimEndingDirectorySeparator(actual), Path.TrimEndingDirectorySeparator(expected), StringComparison.OrdinalIgnoreCase))
            throw new TransferError(403, "共享路径发生变化，请在电脑重新选择普通文件夹。");
    }

    public void Dispose() { foreach (var handle in _handles) handle.Dispose(); _handles.Clear(); }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}

public interface ISharedFileAccess
{
    T Run<T>(string? ownerSid, Func<T> action);
    void Recycle(string path);
}
public sealed class CurrentUserFileAccess : ISharedFileAccess
{
    public T Run<T>(string? ownerSid,Func<T> action)
    {
        if(!string.IsNullOrEmpty(ownerSid) &&
            System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value != ownerSid)
            throw new TransferError(403,"请使用授权的 Windows 用户访问电脑文件。");
        return action();
    }
    public void Recycle(string path)
    {
        RecycleBin.Move(path);
    }
}
public sealed partial class FileTransferVault
{
    public ISharedFileAccess UserFiles { private get; set; } = new CurrentUserFileAccess();
    public sealed record SharedDownload(TransferItem Item, Stream Source, IDisposable PathLease) : IDisposable
    { public void Dispose() { Source.Dispose(); PathLease.Dispose(); } }

    private BrowseRoot[] RootsLocked() => _settings.BrowseRoots is {} roots ? roots :
        string.IsNullOrEmpty(_settings.ReceiveDirectory) ? [] :
        [new("legacy",Path.GetFileName(Path.TrimEndingDirectorySeparator(_settings.ReceiveDirectory)),_settings.ReceiveDirectory,"")];
    private BrowseRoot SharedRoot(string clientId,string key,string? rootId)
    {
        AuthorizeLocked(clientId,KeyHash(key));
        if(!_settings.BrowseEnabled)throw new TransferError(403,"请先在电脑“文件”页开启文件浏览。");
        var roots=RootsLocked();
        return (string.IsNullOrEmpty(rootId)?roots.FirstOrDefault():roots.FirstOrDefault(r=>r.Id==rootId))
            ?? throw new TransferError(403,"此目录已撤销授权，请刷新电脑文件。");
    }
    public object[] BrowseRoots(string clientId,string key)
    {
        lock(_gate)
        {
            AuthorizeLocked(clientId,KeyHash(key));
            if(!_settings.BrowseEnabled)return [];
            return RootsLocked().Select(r=>(object)new {r.Id,r.Name}).ToArray();
        }
    }
    public void AddBrowseRoot(string path,string ownerSid)
    {
        if(!Path.IsPathFullyQualified(path)||string.IsNullOrWhiteSpace(ownerSid))throw new TransferError(400,"请选择可访问的电脑目录。");
        var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        UserFiles.Run(ownerSid,()=>{using var lease=new SharedPathLease(full,"",true);return true;});
        lock(_gate)
        {
            var roots=RootsLocked().ToList();
            int existing=roots.FindIndex(r=>string.Equals(r.Path,full,StringComparison.OrdinalIgnoreCase));
            if(existing>=0) {
                if(roots[existing].OwnerSid==ownerSid)return;
                roots[existing]=roots[existing] with {OwnerSid=ownerSid};
                var adopted=_settings with {BrowseRoots=roots.ToArray()};Save("settings.json",adopted);_settings=adopted;return;
            }
            if(roots.Count>=16)throw new TransferError(400,"最多允许16个浏览入口。");
            var name=Path.GetFileName(full);if(string.IsNullOrEmpty(name))name=full;
            roots.Add(new(Guid.NewGuid().ToString("N"),name,full,ownerSid));
            var updated=_settings with { BrowseRoots=roots.ToArray() };Save("settings.json",updated);_settings=updated;
        }
    }
    public void RemoveBrowseRoot(string id)
    {
        lock(_gate)
        {
            var updated=_settings with {BrowseRoots=RootsLocked().Where(r=>r.Id!=id).ToArray()};
            // Empty explicit roots must not silently regrant the legacy directory.
            if(updated.BrowseRoots.Length==0)updated=updated with {BrowseEnabled=false};
            Save("settings.json",updated);_settings=updated;
        }
    }
    public void SetDevicePermissions(string clientId,bool upload,bool delete)
    {
        lock(_gate)
        {
            if(!_devices.TryGetValue(clientId,out var current))throw new TransferError(404,"请先选择文件设备。");
            var updated=current with {UploadAllowed=upload,DeleteAllowed=delete};
            Save("devices.json",_devices.Values.Select(x=>x.ClientId==clientId?updated:x).ToArray());_devices[clientId]=updated;
        }
    }
    public SharedFileListing Browse(string clientId,string key,string relative,int offset=0,string? rootId=null,string sort="name")
    {
        if(offset<0||offset>100000)throw new TransferError(400,"目录分页无效。");
        if(sort is not ("name" or "name-desc" or "modified" or "size"))throw new TransferError(400,"目录排序无效。");
        lock(_gate)
        {
            var root=SharedRoot(clientId,key,rootId);
            return UserFiles.Run(root.OwnerSid,()=>
            {
                using var lease=new SharedPathLease(root.Path,relative,true);
                var all=Directory.EnumerateFileSystemEntries(lease.FullPath).Take(10001).ToArray();
                if(all.Length>10000)throw new TransferError(413,"目录内容过多，请进入较小的子文件夹。");
                var ordered=all.Where(x=>ValidName(Path.GetFileName(x)))
                    .Where(x=>(File.GetAttributes(x)&(FileAttributes.ReparsePoint|FileAttributes.System|FileAttributes.Hidden))==0)
                    .OrderBy(x=>!Directory.Exists(x));
                ordered=sort switch {
                    "name-desc"=>ordered.ThenByDescending(Path.GetFileName,StringComparer.OrdinalIgnoreCase),
                    "modified"=>ordered.ThenByDescending(File.GetLastWriteTimeUtc).ThenBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase),
                    "size"=>ordered.ThenByDescending(x=>Directory.Exists(x)?0:new FileInfo(x).Length).ThenBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase),
                    _=>ordered.ThenBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase)
                };
                var page=ordered.Skip(offset).Take(101).ToArray();
                var entries=page.Take(100).Select(x=>new SharedFileEntry(Path.GetFileName(x),
                    (lease.RelativePath.Length==0?"":lease.RelativePath+"/")+Path.GetFileName(x),
                    Directory.Exists(x),Directory.Exists(x)?0:new FileInfo(x).Length,File.GetLastWriteTimeUtc(x))).ToArray();
                return new SharedFileListing(lease.RelativePath,root.Name,entries,page.Length>100?offset+100:null);
            });
        }
    }
    public SharedDownload OpenSharedDownload(string clientId,string key,string relative,string? rootId=null)
    {
        lock(_gate)
        {
            var root=SharedRoot(clientId,key,rootId);
            if(string.IsNullOrEmpty(relative))throw new TransferError(400,"请选择一个文件。");
            var opened = UserFiles.Run(root.OwnerSid,()=>
            {
                var lease=new SharedPathLease(root.Path,relative,false);FileStream? input=null;
                try
                {
                    input=new FileStream(lease.FullPath,FileMode.Open,FileAccess.Read,FileShare.Read,ChunkBytes,true);
                    SharedPathLease.ValidateFinalPath(input.SafeFileHandle,lease.FullPath);
                    return (Lease:lease, Input:input);
                }
                catch{input?.Dispose();lease.Dispose();throw;}
            });
            try
            {
                // The owner token authorizes the source handle only. The private
                // queue belongs to the service and is deliberately inaccessible
                // to the desktop user; persist it after reverting impersonation.
                var item=QueueOutgoing(clientId,Path.GetFileName(opened.Lease.FullPath),opened.Input.Length)
                    with {SharedSource=true,BrowseRootId=root.Id};
                SaveItem(item);_items[item.Id]=item;
                return new SharedDownload(item,opened.Input,opened.Lease);
            }
            catch{opened.Input.Dispose();opened.Lease.Dispose();throw;}
        }
    }
    public void RecycleSharedFile(string clientId,string key,string rootId,string relative)
    {
        lock(_gate)
        {
            var root=SharedRoot(clientId,key,rootId);
            if(!_devices[clientId].DeleteAllowed)throw new TransferError(403,"电脑尚未授予此设备删除权限。");
            if(string.IsNullOrEmpty(relative))throw new TransferError(400,"不能删除浏览根目录。");
            UserFiles.Run(root.OwnerSid,()=>
            {
                // Keep the ancestors pinned while the shell resolves the final item.
                string parent=relative.Replace('\\','/');int slash=parent.LastIndexOf('/');
                parent=slash<0?"":parent[..slash];
                using var ancestors=new SharedPathLease(root.Path,parent,true);
                string path;
                using(var leaf=new SharedPathLease(root.Path,relative,false))path=leaf.FullPath;
                UserFiles.Recycle(path);return true;
            });
        }
    }
}
