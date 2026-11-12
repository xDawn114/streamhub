using System.Text.Json;

namespace StreamHub.Installer;

/// <summary>The same file transaction runs in production and in isolated failure-injection checks.</summary>
internal static class RollbackTransaction
{
    public static void Execute(string target, string source, string recovery, Action stop, Action validate, Action<bool> activate)
    {
        target=Path.GetFullPath(target);source=Path.GetFullPath(source);recovery=Path.GetFullPath(recovery);
        if(Path.GetPathRoot(target)==target || source.Equals(target,StringComparison.OrdinalIgnoreCase) ||
            recovery.Equals(source,StringComparison.OrdinalIgnoreCase) || recovery.Equals(target,StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(target+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
            recovery.StartsWith(target+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(recovery))throw new IOException("恢复事务路径无效。");
        Owned(target);Owned(source);NoLinks(target);NoLinks(source);
        for(var parent=new DirectoryInfo(Path.GetDirectoryName(recovery)!);parent is not null;parent=parent.Parent)
            if(parent.Exists&&parent.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("恢复路径不能经过链接。");
        var touched=false;
        try
        {
            stop();Copy(target,recovery);validate();NoLinks(target);NoLinks(source);
            touched=true;Directory.Delete(target,true);Copy(source,target);activate(true);
        }
        catch(Exception original)
        {
            try
            {
                if(touched){stop();NoLinks(target);if(Directory.Exists(target))Directory.Delete(target,true);Copy(recovery,target);}
                activate(false);
            }
            catch(Exception restore){throw new IOException("恢复事务与失败回退均未完成，请保留备份并检查安装日志。",new AggregateException(original,restore));}
            throw;
        }
    }
    private static void Owned(string root)
    {
        using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,".streamhub-install.json")));
        if(json.RootElement.GetProperty("productId").GetString()!="StreamHub.PC.0b1e5f94")throw new IOException("恢复源或目标不属于我控制。");
    }
    private static void Copy(string source,string destination)
    {
        NoLinks(source);Directory.CreateDirectory(destination);
        foreach(var file in Directory.EnumerateFiles(source))File.Copy(file,Path.Combine(destination,Path.GetFileName(file)),true);
        foreach(var directory in Directory.EnumerateDirectories(source))Copy(directory,Path.Combine(destination,Path.GetFileName(directory)));
    }
    private static void NoLinks(string root)
    {
        if(!Directory.Exists(root))return;
        if(File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))throw new IOException("恢复目录包含链接。");
        foreach(var entry in Directory.EnumerateFileSystemEntries(root))
        {if(File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))throw new IOException("恢复内容包含链接。");if(Directory.Exists(entry))NoLinks(entry);}
    }
}
