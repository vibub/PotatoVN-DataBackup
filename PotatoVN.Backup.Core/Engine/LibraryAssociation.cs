using System.Text.Json.Nodes;

namespace PotatoVN.Backup.Core.Engine;

public enum LibraryKind { Folder = 1, Archive = 2, Steam = 4 }

public static class LibraryAssociation
{
    public static LibraryKind? GetKind(JsonObject source) => source["SourceType"]?.ToString() switch
    {
        null or "0" or "1" or "LocalFolder" => LibraryKind.Folder,
        "2" or "LocalZip" => LibraryKind.Archive,
        "4" or "Steam" => LibraryKind.Steam,
        _ => null
    };

    public static bool SamePath(string? first, string? second) =>
        first != null && second != null && Path.IsPathFullyQualified(first) && Path.IsPathFullyQualified(second) &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    public static bool Exists(string? path, LibraryKind kind) =>
        kind == LibraryKind.Archive ? File.Exists(path) : Directory.Exists(path);

    public static IEnumerable<JsonObject> Members(JsonObject source) =>
        (source["Galgames"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();

    public static Guid GameId(JsonObject member) =>
        Guid.TryParse(member["Galgame"]?.ToString(), out Guid id) ? id : Guid.Empty;

    public static string? ResolveMember(string originalRoot, string? originalPath, string targetRoot, LibraryKind kind)
    {
        if (string.IsNullOrWhiteSpace(originalPath) || !Path.IsPathFullyQualified(originalPath) ||
            string.IsNullOrWhiteSpace(originalRoot) || !Path.IsPathFullyQualified(originalRoot)) return null;
        string relative = Path.GetRelativePath(originalRoot, originalPath);
        if (Path.IsPathFullyQualified(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
            return null;
        string path = relative == "." ? targetRoot : BackupFiles.Within(targetRoot, relative);
        return Exists(path, kind) ? path : null;
    }
}
