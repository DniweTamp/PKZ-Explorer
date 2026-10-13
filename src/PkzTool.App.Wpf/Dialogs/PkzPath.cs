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
    public static string DefaultNameFor(string folderPath)
    {
        // The folder's own name, not the path it happens to live at. Sanitising the whole path turned
        // every separator into an underscore, so packing ...\OneDrive\Desktop\cloth suggested
        // "C__Users_..._cloth.pkz" instead of "cloth.pkz".
        string name = string.IsNullOrWhiteSpace(folderPath)
            ? string.Empty
            : Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        // A drive root ("C:\") has no name of its own, and a trailing separator used to leave GetFileName
        // empty, so both fall back rather than producing ".pkz" or "_.pkz".
        if (string.IsNullOrWhiteSpace(name)) name = "packed";

        // A folder name can still hold characters a file name cannot, since Windows only forbids them in
        // file names. Those are replaced rather than dropping the suggestion.
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return name + ".pkz";
    }
}
