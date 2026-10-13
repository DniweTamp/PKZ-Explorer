namespace PkzTool.Core.Settings;

/// <summary>
/// Finds the user-supplied Oodle dll. The dll is never bundled (it is proprietary), so a configured
/// path wins and auto-detection covers the common case of dropping it beside the exe.
/// </summary>
public static class OodleLocator
{
    /// <summary>The file-name pattern the game's dll uses, e.g. <c>oo2core_9_win64.dll</c>.</summary>
    public const string Pattern = "oo2core*.dll";

    /// <summary>
    /// Resolve the dll: an explicit path that exists, then the executable's own folder, then every
    /// directory on PATH. Returns null when nothing matches, which the caller treats as "prompt me".
    /// </summary>
    public static string? Find(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        foreach (var directory in CandidateDirectories())
        {
            var found = FirstMatch(directory);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// Every dll matching the pattern in the usual places, most likely first. Used by the settings
    /// dialog so the user can pick from what is actually on the machine.
    /// </summary>
    public static IReadOnlyList<string> All()
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in CandidateDirectories())
            foreach (var candidate in Matches(directory))
                if (seen.Add(candidate))
                    results.Add(candidate);
        return results;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        yield return AppContext.BaseDirectory;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length > 0) yield return trimmed;
        }
    }

    private static string? FirstMatch(string directory) => Matches(directory).FirstOrDefault();

    private static IEnumerable<string> Matches(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) yield break;
        string[] found;
        try
        {
            if (!Directory.Exists(directory)) yield break;
            found = Directory.GetFiles(directory, Pattern);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (ArgumentException)
        {
            yield break;
        }

        // A stable order so the same machine always picks the same candidate.
        Array.Sort(found, StringComparer.OrdinalIgnoreCase);
        foreach (var file in found) yield return file;
    }
}
