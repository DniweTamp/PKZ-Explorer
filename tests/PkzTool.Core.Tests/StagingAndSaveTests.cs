using PkzTool.Core.Archives;
using PkzTool.Core.Compression;
using PkzTool.Core.Vfs;
using PkzTool.Core.Workspace;
using Xunit;

namespace PkzTool.Core.Tests;

/// <summary>
/// Staged editing and saving: nothing touches disk until Save, a save rewrites the archive in full, and
/// the row-level rules (a folder rename moves its whole subtree, an edit aimed at one shadowed row
/// leaves the other alone) hold.
/// </summary>
public class StagingAndSaveTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PkzWorkspaceHarness _harness = new();

    public void Dispose()
    {
        _harness.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void EditsAreStaged_AndTheFileOnDiskIsUntouchedUntilSave()
    {
        string pkz = _temp.File("data.pkz");
        PkzFixture.WriteStored(pkz, ("pl/pl0000.dat", PkzFixture.Text("original")));
        byte[] before = File.ReadAllBytes(pkz);

        using var workspace = _harness.Open(pkz);
        var row = workspace.AllFiles.Single();
        var archive = workspace.Archives[0];

        Assert.True(workspace.StageAddOrReplace(archive, row.Path, PkzFixture.Text("changed"),
            EntryCompression.None, 0, allowOverwrite: true, replaceKey: row.Key));

        // The view reflects the edit; the archive on disk does not, until Save.
        Assert.True(workspace.IsModified);
        Assert.Equal("changed", System.Text.Encoding.UTF8.GetString(workspace.ReadData(row)));
        Assert.Equal(before, File.ReadAllBytes(pkz));

        var outcome = workspace.Save();
        Assert.Equal(1, outcome.Written);
        Assert.Empty(outcome.Failures);
        Assert.False(workspace.IsModified);

        using var reopened = PkzArchive.Open(pkz);
        Assert.Equal("changed",
            System.Text.Encoding.UTF8.GetString(reopened.Extract(reopened.Find("pl/pl0000.dat")!)));
    }

    /// <summary>
    /// An untouched entry must survive a save byte for byte, and must not be re-compressed: a save is
    /// not a repack.
    /// </summary>
    [Fact]
    public void Save_CopiesUntouchedEntriesWithoutRecompressingThem()
    {
        string pkz = _temp.File("data.pkz");
        var compressible = PkzFixture.Compressible(16384);
        PkzFixture.Write(pkz,
            ("keep/keep.dat", compressible, EntryCompression.ZStandard),
            ("edit/edit.dat", PkzFixture.Text("before"), EntryCompression.None));

        long originalStored;
        using (var reader = PkzArchive.Open(pkz))
            originalStored = reader.Find("keep/keep.dat")!.CompressedSize;

        using var workspace = _harness.Open(pkz);
        var archive = workspace.Archives[0];
        var target = workspace.AllFiles.Single(f => f.Path == "edit/edit.dat");

        workspace.StageAddOrReplace(archive, target.Path, PkzFixture.Text("after"),
            EntryCompression.None, 0, allowOverwrite: true, replaceKey: target.Key);
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        var kept = reopened.Find("keep/keep.dat")!;

        Assert.Equal(EntryCompression.ZStandard, kept.Compression);
        // Same stored size means the bytes were copied through, not re-encoded.
        Assert.Equal(originalStored, kept.CompressedSize);
        Assert.Equal(compressible, reopened.Extract(kept));
    }

    [Fact]
    public void Save_PreservesTheStoredEntryOrderAndAppendsNewFiles()
    {
        string pkz = _temp.File("order.pkz");
        PkzFixture.WriteStored(pkz,
            ("zz.dat", PkzFixture.Text("1")),
            ("aa.dat", PkzFixture.Text("2")));

        using var workspace = _harness.Open(pkz);
        var archive = workspace.Archives[0];
        workspace.StageAddOrReplace(archive, "mm.dat", PkzFixture.Text("3"),
            EntryCompression.None, 0, allowOverwrite: true);
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        // The original two keep their order; the new file goes on the end.
        Assert.Equal(new[] { "zz.dat", "aa.dat", "mm.dat" }, reopened.Entries.Select(e => e.Name));
    }

    [Fact]
    public void Delete_RemovesTheEntryFromTheArchive()
    {
        string pkz = _temp.File("del.pkz");
        PkzFixture.WriteStored(pkz,
            ("keep.dat", PkzFixture.Text("keep")),
            ("drop.dat", PkzFixture.Text("drop")));

        using var workspace = _harness.Open(pkz);
        var row = workspace.AllFiles.Single(f => f.Path == "drop.dat");

        Assert.Equal(1, workspace.StageDelete(new[] { row }));
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        Assert.Equal("keep.dat", Assert.Single(reopened.Entries).Name);
    }

    /// <summary>Deleting a folder row takes everything under it.</summary>
    [Fact]
    public void Delete_OnAFolderRemovesTheWholeSubtree()
    {
        string pkz = _temp.File("deldir.pkz");
        PkzFixture.WriteStored(pkz,
            ("pl/pl0000.dat", PkzFixture.Text("1")),
            ("pl/sub/deep.dat", PkzFixture.Text("2")),
            ("other.dat", PkzFixture.Text("3")));

        using var workspace = _harness.Open(pkz);
        var folder = workspace.FindFolder("pl")!;

        Assert.Equal(2, workspace.StageDelete(new[] { folder }));
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        Assert.Equal("other.dat", Assert.Single(reopened.Entries).Name);
    }

    [Fact]
    public void Rename_OnAFileChangesOnlyItsName()
    {
        string pkz = _temp.File("ren.pkz");
        PkzFixture.WriteStored(pkz, ("pl/pl0000.dat", PkzFixture.Text("payload")));

        using var workspace = _harness.Open(pkz);
        var row = workspace.AllFiles.Single();

        Assert.Equal(1, workspace.StageRename(row, "pl0001.dat"));
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        Assert.Equal("pl/pl0001.dat", Assert.Single(reopened.Entries).Name);
        Assert.Equal("payload",
            System.Text.Encoding.UTF8.GetString(reopened.Extract(reopened.Entries[0])));
    }

    /// <summary>
    /// Renaming a folder rewrites that path segment for every file under it, keeping the rest of each
    /// path and the payloads untouched.
    /// </summary>
    [Fact]
    public void Rename_OnAFolderRewritesTheSegmentForEveryFileUnderIt()
    {
        string pkz = _temp.File("rendir.pkz");
        PkzFixture.WriteStored(pkz,
            ("pl/pl0000.dat", PkzFixture.Text("one")),
            ("pl/sub/deep.dat", PkzFixture.Text("two")),
            ("other/keep.dat", PkzFixture.Text("three")));

        using var workspace = _harness.Open(pkz);
        var folder = workspace.FindFolder("pl")!;

        Assert.Equal(2, workspace.StageRename(folder, "player"));
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        var names = reopened.Entries.Select(e => e.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "other/keep.dat", "player/pl0000.dat", "player/sub/deep.dat" }, names);
        Assert.Equal("two",
            System.Text.Encoding.UTF8.GetString(reopened.Extract(reopened.Find("player/sub/deep.dat")!)));
    }

    [Fact]
    public void Add_CreatesTheFolderPathAndTheEntry()
    {
        string pkz = _temp.File("add.pkz");
        PkzFixture.WriteStored(pkz, ("keep.dat", PkzFixture.Text("keep")));

        using var workspace = _harness.Open(pkz);
        var archive = workspace.Archives[0];

        Assert.True(workspace.StageAddOrReplace(archive, "new/deep/added.dat",
            PkzFixture.Text("added"), EntryCompression.None, 0, allowOverwrite: true));

        // The tree gained the folders as real rows.
        Assert.NotNull(workspace.FindFolder("new"));
        Assert.NotNull(workspace.FindFolder("new/deep"));
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        Assert.Equal("added",
            System.Text.Encoding.UTF8.GetString(reopened.Extract(reopened.Find("new/deep/added.dat")!)));
    }

    [Fact]
    public void Recompress_ChangesStorageButKeepsTheBytes()
    {
        string pkz = _temp.File("rc.pkz");
        var payload = PkzFixture.Compressible(16384);
        PkzFixture.Write(pkz, ("data.dat", payload, EntryCompression.None));
        using var workspace = _harness.Open(pkz);
        var row = workspace.AllFiles.Single();
        Assert.Equal(EntryCompression.None, row.Compression);

        workspace.StageRecompress(row, EntryCompression.ZStandard, 3);
        Assert.Equal("ZStandard", row.CompressionLabel);
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        var entry = Assert.Single(reopened.Entries);
        Assert.Equal(EntryCompression.ZStandard, entry.Compression);
        Assert.Equal(payload, reopened.Extract(entry));
    }

    /// <summary>A staged recompress obeys the 90% rule, falling back to stored for a bad candidate.</summary>
    [Fact]
    public void Recompress_FallsBackToStoredWhenCompressionDoesNotHelp()
    {
        string pkz = _temp.File("rcbad.pkz");
        var payload = PkzFixture.Incompressible(4096, seed: 21);
        PkzFixture.Write(pkz, ("random.bin", payload, EntryCompression.None));

        using var workspace = _harness.Open(pkz);
        var row = workspace.AllFiles.Single();
        workspace.StageRecompress(row, EntryCompression.ZStandard, 3);
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        var entry = Assert.Single(reopened.Entries);
        Assert.Equal(EntryCompression.None, entry.Compression);
        Assert.Equal(payload, reopened.Extract(entry));
    }

    [Fact]
    public void DiscardEdits_RestoresTheStoredState()
    {
        string pkz = _temp.File("discard.pkz");
        PkzFixture.WriteStored(pkz, ("a.dat", PkzFixture.Text("original")));

        using var workspace = _harness.Open(pkz);
        var archive = workspace.Archives[0];
        var row = workspace.AllFiles.Single();
        workspace.StageAddOrReplace(archive, row.Path, PkzFixture.Text("staged"),
            EntryCompression.None, 0, allowOverwrite: true, replaceKey: row.Key);
        Assert.True(workspace.IsModified);

        workspace.DiscardEdits();

        Assert.False(workspace.IsModified);
        Assert.Equal("original", System.Text.Encoding.UTF8.GetString(workspace.ReadData(workspace.AllFiles.Single())));
    }

    /// <summary>
    /// The core per-row rule: with the same path in two archives, an edit aimed at the suffixed row
    /// must leave the other archive's entry alone.
    /// </summary>
    [Fact]
    public void EditingOneShadowedRow_LeavesTheOtherArchiveAlone()
    {
        string first = _temp.File("data000.pkz");
        string second = _temp.File("data001.pkz");
        PkzFixture.WriteStored(first, ("pl/pl0000.dat", PkzFixture.Text("from 000")));
        PkzFixture.WriteStored(second, ("pl/pl0000.dat", PkzFixture.Text("from 001")));

        using var workspace = _harness.Open(first, second);
        var rows = workspace.FindFolder("pl")!.ChildFiles;

        // Row 1 is the suffixed one, owned by data001.pkz.
        var suffixed = rows.Single(r => r.DisplayName.EndsWith("data001.pkz"));
        Assert.Equal("pl0000.dat - data001.pkz", suffixed.DisplayName);

        var owner = workspace.Archives.Single(a => a.FileName == "data001.pkz");
        workspace.StageAddOrReplace(owner, suffixed.Path, PkzFixture.Text("EDITED"),
            EntryCompression.None, 0, allowOverwrite: true, replaceKey: suffixed.Key);
        workspace.Save();

        using var reopenedFirst = PkzArchive.Open(first);
        using var reopenedSecond = PkzArchive.Open(second);

        Assert.Equal("from 000",
            System.Text.Encoding.UTF8.GetString(reopenedFirst.Extract(reopenedFirst.Find("pl/pl0000.dat")!)));
        Assert.Equal("EDITED",
            System.Text.Encoding.UTF8.GetString(reopenedSecond.Extract(reopenedSecond.Find("pl/pl0000.dat")!)));
    }

    [Fact]
    public void Delete_OnOneShadowedRowOnlyRemovesThatRow()
    {
        string first = _temp.File("data000.pkz");
        string second = _temp.File("data001.pkz");
        PkzFixture.WriteStored(first, ("dup.dat", PkzFixture.Text("a")));
        PkzFixture.WriteStored(second, ("dup.dat", PkzFixture.Text("b")));

        using var workspace = _harness.Open(first, second);
        var suffixed = workspace.Root.ChildFiles.Single(r => r.DisplayName.Contains("data001.pkz"));

        Assert.Equal(1, workspace.StageDelete(new[] { suffixed }));
        workspace.Save();

        using var reopenedFirst = PkzArchive.Open(first);
        using var reopenedSecond = PkzArchive.Open(second);

        Assert.Single(reopenedFirst.Entries);   // data000 keeps its entry
        Assert.Empty(reopenedSecond.Entries);   // data001 lost only its own
    }

    [Fact]
    public void SaveAs_WritesToTheNewPathAndLeavesTheOriginal()
    {
        string original = _temp.File("original.pkz");
        string copy = _temp.File("copy.pkz");
        PkzFixture.WriteStored(original, ("a.dat", PkzFixture.Text("original")));

        using var workspace = _harness.Open(original);
        var archive = workspace.Archives[0];
        workspace.StageAddOrReplace(archive, "a.dat", PkzFixture.Text("edited"),
            EntryCompression.None, 0, allowOverwrite: true, replaceKey: workspace.AllFiles.Single().Key);

        var outcome = workspace.SaveAs(new Dictionary<LoadedArchive, string> { [archive] = copy });

        Assert.Equal(1, outcome.Written);
        using var reopenedCopy = PkzArchive.Open(copy);
        Assert.Equal("edited",
            System.Text.Encoding.UTF8.GetString(reopenedCopy.Extract(reopenedCopy.Find("a.dat")!)));

        using var reopenedOriginal = PkzArchive.Open(original);
        Assert.Equal("original",
            System.Text.Encoding.UTF8.GetString(reopenedOriginal.Extract(reopenedOriginal.Find("a.dat")!)));
    }

    /// <summary>
    /// An archive whose write fails must keep its staged edits so the save can be retried. Discarding
    /// them would destroy the very work the save was unable to write — the failure would be reported and
    /// then the edits would silently vanish.
    /// </summary>
    [Fact]
    public void Save_KeepsTheEditsOfAnArchiveThatCouldNotBeWritten()
    {
        string first = _temp.File("a.pkz");
        string second = _temp.File("b.pkz");
        PkzFixture.WriteStored(first, ("a.dat", PkzFixture.Text("a")));
        PkzFixture.WriteStored(second, ("b.dat", PkzFixture.Text("b")));

        using var workspace = _harness.Open(first, second);
        foreach (var archive in workspace.Archives)
        {
            var row = workspace.AllFiles.First(f => ReferenceEquals(f.Owner, archive.Archive));
            workspace.StageAddOrReplace(archive, row.Path, PkzFixture.Text("edited"),
                EntryCompression.None, 0, allowOverwrite: true, replaceKey: row.Key);
        }

        PkzWorkspace.SaveOutcome outcome;
        // FileShare.Read lets the workspace keep its own shared handle but denies the replace, which is
        // exactly the "another program has the file open" case a user hits.
        using (var hold = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            outcome = workspace.Save();
        }

        Assert.Equal(1, outcome.Written);
        Assert.Single(outcome.Failures);

        // The written archive is clean; the failed one still carries its edit.
        var writtenArchive = workspace.Archives.Single(a => a.FileName == "a.pkz");
        var failedArchive = workspace.Archives.Single(a => a.FileName == "b.pkz");
        Assert.False(writtenArchive.IsModified);
        Assert.True(failedArchive.IsModified);
        Assert.True(workspace.IsModified);

        // And the rewrite actually lands once the obstruction is gone.
        var retry = workspace.Save();
        Assert.Equal(1, retry.Written);
        Assert.Empty(retry.Failures);
        Assert.False(workspace.IsModified);

        using var reopened = PkzArchive.Open(second);
        Assert.Equal("edited",
            System.Text.Encoding.UTF8.GetString(reopened.Extract(reopened.Find("b.dat")!)));
    }

    /// <summary>A save that fails must not leave its scratch file beside the archive.</summary>
    [Fact]
    public void Save_LeavesNoTempFileBehindWhenAWriteFails()
    {
        string pkz = _temp.File("locked.pkz");
        PkzFixture.WriteStored(pkz, ("a.dat", PkzFixture.Text("a")));

        using var workspace = _harness.Open(pkz);
        var row = workspace.AllFiles.Single();
        workspace.StageAddOrReplace(workspace.Archives[0], row.Path, PkzFixture.Text("edited"),
            EntryCompression.None, 0, allowOverwrite: true, replaceKey: row.Key);

        using (var hold = new FileStream(pkz, FileMode.Open, FileAccess.Read, FileShare.Read))
            workspace.Save();

        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp-*"));
    }

    /// <summary>
    /// A Save As that fails leaves the archive pointing at its original file, so a later plain Save
    /// writes back there rather than to the destination that could not be written.
    /// </summary>
    [Fact]
    public void SaveAs_OnFailure_KeepsTheOriginalTarget()
    {
        string original = _temp.File("original.pkz");
        PkzFixture.WriteStored(original, ("a.dat", PkzFixture.Text("a")));

        using var workspace = _harness.Open(original);
        var archive = workspace.Archives[0];
        var row = workspace.AllFiles.Single();
        workspace.StageAddOrReplace(archive, row.Path, PkzFixture.Text("edited"),
            EntryCompression.None, 0, allowOverwrite: true, replaceKey: row.Key);

        // A destination whose parent directory exists but whose path cannot be written (a directory
        // sitting where the file should go) makes the write fail after the target was repointed.
        string blocked = Path.Combine(_temp.Path, "blocked.pkz");
        Directory.CreateDirectory(blocked);

        var outcome = workspace.SaveAs(new Dictionary<LoadedArchive, string> { [archive] = blocked });

        Assert.Single(outcome.Failures);
        Assert.True(workspace.IsModified);
        Assert.Equal(original, archive.TargetPath);

        // The retry writes back to the original, which is the file that still exists.
        var retry = workspace.Save();
        Assert.Equal(1, retry.Written);
        using var reopened = PkzArchive.Open(original);
        Assert.Equal("edited",
            System.Text.Encoding.UTF8.GetString(reopened.Extract(reopened.Find("a.dat")!)));
    }

    [Fact]
    public void Save_SkipsArchivesWithNoEdits()    {
        string first = _temp.File("a.pkz");
        string second = _temp.File("b.pkz");
        PkzFixture.WriteStored(first, ("a.dat", PkzFixture.Text("a")));
        PkzFixture.WriteStored(second, ("b.dat", PkzFixture.Text("b")));

        DateTime untouched = File.GetLastWriteTimeUtc(second);

        using var workspace = _harness.Open(first, second);
        var firstArchive = workspace.Archives.Single(a => a.FileName == "a.pkz");
        var row = workspace.AllFiles.Single(f => f.Path == "a.dat");
        workspace.StageAddOrReplace(firstArchive, row.Path, PkzFixture.Text("edited"),
            EntryCompression.None, 0, allowOverwrite: true, replaceKey: row.Key);

        var outcome = workspace.Save();

        Assert.Equal(1, outcome.Written);
        // The unedited archive keeps its timestamp, so a save is not a rewrite of everything loaded.
        Assert.Equal(untouched, File.GetLastWriteTimeUtc(second));
    }

    [Fact]
    public void Load_ReplacesEverythingRatherThanAddingToTheView()
    {
        string first = _temp.File("first.pkz");
        string second = _temp.File("second.pkz");
        PkzFixture.WriteStored(first, ("one.dat", PkzFixture.Text("1")));
        PkzFixture.WriteStored(second, ("two.dat", PkzFixture.Text("2")));

        using var workspace = _harness.Open(first);
        Assert.Single(workspace.AllFiles);

        workspace.Load(new[] { second });

        // The second load replaced the view: "one.dat" is gone, not merged.
        Assert.Equal(new[] { "two.dat" }, workspace.AllFiles.Select(f => f.Path));
        Assert.Single(workspace.Archives);
    }

    [Fact]
    public void Load_SkipsABadArchiveAndKeepsTheRest()
    {
        string good = _temp.File("good.pkz");
        string bad = _temp.WriteText("bad.pkz", "not an archive");
        PkzFixture.WriteStored(good, ("good.dat", PkzFixture.Text("ok")));

        var (workspace, result) = _harness.OpenExpectingFailure(good, bad);

        Assert.Equal(1, result.Loaded);
        Assert.Equal(1, result.Failed);
        // The failure names the offending file and says why, so the shell can show it.
        Assert.Equal(bad, result.Failures[0].Path);
        Assert.Contains("not a pkz archive", result.Failures[0].Error);
        // The good archive is fully usable despite the bad one.
        Assert.Equal("good.dat", workspace.AllFiles.Single().Path);
    }

    [Fact]
    public void WouldCollide_DetectsANameAlreadyInThatArchive()
    {
        string first = _temp.File("a.pkz");
        string second = _temp.File("b.pkz");
        PkzFixture.WriteStored(first, ("shared.dat", PkzFixture.Text("a")));
        PkzFixture.WriteStored(second, ("other.dat", PkzFixture.Text("b")));

        using var workspace = _harness.Open(first, second);
        var firstArchive = workspace.Archives.Single(a => a.FileName == "a.pkz");
        var secondArchive = workspace.Archives.Single(a => a.FileName == "b.pkz");

        Assert.True(workspace.WouldCollide(firstArchive, "shared.dat"));
        // A name in another archive is not a collision for this one.
        Assert.False(workspace.WouldCollide(secondArchive, "shared.dat"));
        Assert.False(workspace.WouldCollide(firstArchive, "brand-new.dat"));
    }

    /// <summary>An add declined by the caller must not change anything.</summary>
    [Fact]
    public void StageAddOrReplace_RefusesWithoutPermissionToOverwrite()
    {
        string pkz = _temp.File("n.pkz");
        PkzFixture.WriteStored(pkz, ("taken.dat", PkzFixture.Text("original")));

        using var workspace = _harness.Open(pkz);
        var archive = workspace.Archives[0];

        bool staged = workspace.StageAddOrReplace(archive, "taken.dat", PkzFixture.Text("new"),
            EntryCompression.None, 0, allowOverwrite: false);

        Assert.False(staged);
        Assert.False(workspace.IsModified);
    }

    [Fact]
    public void StageRename_RejectsAnInvalidName()
    {
        string pkz = _temp.File("v.pkz");
        PkzFixture.WriteStored(pkz, ("a.dat", PkzFixture.Text("a")));

        using var workspace = _harness.Open(pkz);
        var row = workspace.AllFiles.Single();

        Assert.Throws<ArgumentException>(() => workspace.StageRename(row, "bad/name.dat"));
        Assert.Throws<ArgumentException>(() => workspace.StageRename(row, "bad\\name.dat"));
        Assert.Throws<ArgumentException>(() => workspace.StageRename(row, "  "));
    }

    [Fact]
    public void AddTargetFolder_PicksTheSelectedFolderOrTheFilesParent()
    {
        string pkz = _temp.File("t.pkz");
        PkzFixture.WriteStored(pkz,
            ("pl/pl0000.dat", PkzFixture.Text("1")),
            ("effect/001.wta", PkzFixture.Text("2")));

        using var workspace = _harness.Open(pkz);

        // Nothing selected: the root.
        Assert.Equal(string.Empty, workspace.AddTargetFolder(Array.Empty<VfsNode>()).Path);

        // A folder row is the target itself.
        Assert.Equal("pl", workspace.AddTargetFolder(new[] { workspace.FindFolder("pl")! }).Path);

        // A file row targets its own folder.
        var file = workspace.AllFiles.Single(f => f.Path == "effect/001.wta");
        Assert.Equal("effect", workspace.AddTargetFolder(new[] { file }).Path);
    }
}
