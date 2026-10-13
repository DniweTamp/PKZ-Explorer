namespace PkzTool.App.Wpf.Dialogs;

/// <summary>
/// The name the create-from-folder save dialog suggests: the folder's own name with a <c>.pkz</c>
/// extension, so packing <c>...\romfs_mod</c> suggests <c>romfs_mod.pkz</c>.
/// <para>
/// A <c>z_</c> prefix is deliberately not added: the engine enumerates loose archives in name order and
/// the later one wins, so a pack meant to override the game's own archives must sort after them. That is
/// a decision for the user to make knowingly, so it is documented in the help text rather than applied
/// silently.
/// </para>
/// </summary>
public static class PkzPath
{
    /// <summary>A suggested archive file name for a source folder.</summary>
    public static string DefaultNameFor(string folderName)
    {
        var name = string.IsNullOrWhiteSpace(folderName) ? "packed" : folderName.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name + ".pkz";
    }
}
