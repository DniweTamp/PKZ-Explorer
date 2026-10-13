namespace PkzTool.Core.Archives;

/// <summary>
/// Per-entry compression in a PKZ archive. The names match the <c>comp</c> strings the game and PKZe
/// write into the archive's string area, so they are the archive's own vocabulary, not a UI choice.
/// </summary>
public enum EntryCompression
{
    /// <summary>Stored as-is ("None").</summary>
    None,

    /// <summary>Zstandard ("ZStandard").</summary>
    ZStandard,

    /// <summary>Oodle Kraken ("OodleKraken"); needs a user-supplied <c>oo2core_*.dll</c>.</summary>
    OodleKraken,
}
