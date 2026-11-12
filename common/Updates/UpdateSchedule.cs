using System.IO;
using System.Text.Json;

namespace StreamHub.Updates;

public sealed record UpdatePreferences(bool AutoCheck = true, bool AutoDownload = false,
    DateTimeOffset? LastCheckedAt = null, DateTimeOffset? NextCheckAt = null, int Failures = 0)
{
    public bool Due(DateTimeOffset now) => AutoCheck && (NextCheckAt is null ||
        NextCheckAt <= now || NextCheckAt > now.AddHours(12));
    public UpdatePreferences Succeeded(DateTimeOffset now) => this with
    { LastCheckedAt = now, NextCheckAt = now.AddHours(12), Failures = 0 };
    public UpdatePreferences Failed(DateTimeOffset now) => this with
    { Failures = Math.Min(4, Failures + 1), NextCheckAt = now.AddMinutes(Failures switch { 0 => 5, 1 => 15, 2 => 60, _ => 720 }) };
}

/// <summary>Separate from appearance and binding data; failed writes preserve the previous settings.</summary>
public static class UpdatePreferencesFile
{
    public static UpdatePreferences Read(string path)
    {
        try { return JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(path)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public static void Write(string path, UpdatePreferences value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, path, true);
    }
}
