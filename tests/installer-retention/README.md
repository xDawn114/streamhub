# Installer backup retention checks

Run on Windows from PowerShell 7 with .NET 8:

```powershell
./Run.ps1 -ArtifactRoot D:/Deepseek/build/retention-checks-new
```

The artifact directory must not exist. The checks create small synthetic snapshots, a real NTFS junction and a locked fixture file entirely inside that directory. They exercise the production catalog and rollback file transaction without invoking an installer, Windows services, networking or the installed product. Fixture files remain for inspection.
