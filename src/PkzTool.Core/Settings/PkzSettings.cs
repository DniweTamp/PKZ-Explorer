using System.Text.Json;
using System.Text.Json.Serialization;
using PkzTool.Core.Archives;
using PkzTool.Core.Compression;
using Xt1.Core.Settings;

namespace PkzTool.Core.Settings;

/// <summary>
/// Application settings, persisted to <c>settings.json</c> next to the executable so the tool stays
/// portable and its state travels with it.
/// <para>
/// The theme enum and the fallback-directory strategy come from <c>Xt1.Core</c>, which is the same
/// shape XT1 Studio uses; the PKZ-specific state (the Oodle dll, recent files, column widths) is here.
/// </para>
/// </summary>
public sealed class PkzSettings
{
    /// <summary>UI theme choice. <see cref="AppTheme.System"/> means no explicit choice has been
    /// recorded; the shell snaps it to the OS preference at startup.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Path to the user-supplied Oodle dll (<c>oo2core_*.dll</c>). Null means
    /// auto-detection, which searches the app folder and PATH.</summary>
    public string? OodleDllPath { get; set; }

    /// <summary>Last folder used by an open, save, extract or add dialog.</summary>
    public string? LastDirectory { get; set; }

    /// <summary>Last folder used by "PKZ from folder".</summary>
    public string? LastSourceFolder { get; set; }

    /// <summary>
    /// Recently opened PKZ files, newest first, each entry a single archive. Entries whose file no
    /// longer exists are pruned on load.
    /// </summary>
    public List<string>? RecentFiles { get; set; }

    /// <summary>Compression the create-from-folder dialog opens on.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EntryCompression DefaultCompression { get; set; } = EntryCompression.None;

    /// <summary>Level the create-from-folder and compression dialogs open on.</summary>
    public int DefaultLevel { get; set; } = CompressionCodec.DefaultZstdLevel;

    /// <summary>Remembered width of the filename column, in device-independent pixels.</summary>
    public double ColumnNameWidth { get; set; } = 320;

    /// <summary>Remembered width of the uncompressed-size column.</summary>
    public double ColumnSizeWidth { get; set; } = 110;

    /// <summary>Remembered width of the file-format column.</summary>
    public double ColumnFormatWidth { get; set; } = 90;

    /// <summary>Remembered user-dragged width of the compression column; 0 means auto-fill.</summary>
    public double ColumnCompressionWidth { get; set; } = 0;

    /// <summary>Whether the window opens maximised.</summary>
    public bool Maximized { get; set; }

    /// <summary>The folder holding <c>settings.json</c>: the executable's own directory, which is what
    /// makes the tool portable.</summary>
    [JsonIgnore]
    public static string SettingsDirectory => AppContext.BaseDirectory;

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>Roaming fallback, used when the executable's directory is not writable (an install
    /// under Program Files, for instance).</summary>
    [JsonIgnore]
    public static string FallbackSettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PkzTool");

    [JsonIgnore]
    public static string FallbackSettingsPath => Path.Combine(FallbackSettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Load the settings, preferring the portable file and falling back to the roaming copy.</summary>
    public static PkzSettings Load()
    {
        try
        {
            var path = File.Exists(SettingsPath) ? SettingsPath
                : File.Exists(FallbackSettingsPath) ? FallbackSettingsPath
                : null;
            if (path is null) return new PkzSettings();

            var loaded = JsonSerializer.Deserialize<PkzSettings>(File.ReadAllText(path), Options);
            if (loaded is null) return new PkzSettings();

            loaded.PruneRecent();
            return loaded;
        }
        catch
        {
            // Corrupt or unreadable settings fall back to defaults rather than blocking startup.
            return new PkzSettings();
        }
    }

    /// <summary>Persist to disk; returns false with the last error when neither location is writable.</summary>
    public bool Save(out string? error)
    {
        error = null;
        var json = JsonSerializer.Serialize(this, Options);
        foreach (var path in new[] { SettingsPath, FallbackSettingsPath })
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }
        return false;
    }

    /// <summary>Persist to disk, ignoring a failure (used for window-state updates).</summary>
    public void Save() => Save(out _);

    /// <summary>Drop recent entries whose file has gone, so the menu never offers a dead path.</summary>
    public void PruneRecent()
    {
        if (RecentFiles is null) return;
        RecentFiles.RemoveAll(p => string.IsNullOrWhiteSpace(p) || !File.Exists(p));
    }

    /// <summary>Record a file as most recently opened, keeping the list short and duplicate-free.</summary>
    public void RememberRecent(string path)
    {
        RecentFiles ??= new List<string>();
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
    }

    /// <summary>
    /// The Oodle dll to use: the configured path when it exists, otherwise the first auto-detected
    /// candidate. Returns null when nothing usable is present, which is the signal to prompt.
    /// </summary>
    public string? ResolveOodleDll() => OodleLocator.Find(OodleDllPath);

    /// <summary>The settings' compression choice, with the level clamped to that codec's range.</summary>
    public (EntryCompression Compression, int Level) ResolveCompression() =>
        (DefaultCompression, CompressionCodec.ClampLevel(DefaultCompression, DefaultLevel));
}
