using PkzTool.Core.Archives;
using PkzTool.Core.Cli;
using PkzTool.Core.Compression;
using PkzTool.Core.Settings;
using PkzTool.Core.Workspace;
using Xunit;

namespace PkzTool.Core.Tests;

/// <summary>
/// Sequences of edits applied to the same entry, and the failure paths around them.
/// <para>
/// Every bug these cover had the same shape: a single source of truth was split in two — the row the user
/// saw and the edit the save applied — so the second edit on a row silently discarded the first while the
/// view kept showing both. A test that performs only one edit per row cannot see that, which is why these
/// all do at least two.
/// </para>
/// </summary>
public class EditCompositionTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PkzWorkspaceHarness _harness = new();

    public void Dispose()
    {
        _harness.Dispose();
        _temp.Dispose();
    }

    private static byte[] Compressible(int length)
    {
        var buffer = new byte[length];
        for (int i = 0; i < length; i++) buffer[i] = (byte)('a' + (i % 7));
        return buffer;
    }

    private static byte[] Text(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static string ReadBack(PkzArchive archive, string name) =>
        System.Text.Encoding.UTF8.GetString(archive.Extract(archive.Find(name)!));

    /// <summary>
    /// Replacing an entry's bytes and then changing its storage must keep both. The recompress used to
    /// overwrite the replacement, so the archive kept its original bytes while the row still showed the
    /// new ones.
    /// </summary>
    [Fact]
    public void ReplaceThenRecompress_KeepsBothTheNewBytesAndTheNewStorage()
    {
        string pkz = _temp.File("rc.pkz");
        PkzFixture.WriteStored(pkz, ("data.dat", Text("ORIGINAL")));
        using var workspace = _harness.Open(pkz);

        var row = workspace.AllFiles.Single();
        var data = Compressible(16384);

        workspace.StageAddOrReplace(workspace.Archives[0], row.Path, data, EntryCompression.None, 0,
            allowOverwrite: true, replaceKey: row.Key);
        workspace.StageRecompress(row, EntryCompression.ZStandard, 3);
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        var entry = Assert.Single(reopened.Entries);
        Assert.Equal(data.Length, entry.DecompressedSize);
        // The payload is large and repetitive, so the 90% rule keeps the requested compression.
        Assert.Equal(EntryCompression.ZStandard, entry.Compression);
        Assert.Equal(data, reopened.Extract(entry));
    }

    /// <summary>The same in the other order: a recompress must not lose the bytes staged after it.</summary>
    [Fact]
    public void RecompressThenReplace_KeepsBoth()
    {
        string pkz = _temp.File("rc2.pkz");
        var original = Compressible(16384);
        PkzFixture.Write(pkz, ("data.dat", original, EntryCompression.None));
        using var workspace = _harness.Open(pkz);

        var row = workspace.AllFiles.Single();
        // A longer, differently-shaped payload so a mix-up with the original would be visible.
        var replacement = new byte[32768];
        for (int i = 0; i < replacement.Length; i++) replacement[i] = (byte)('A' + (i % 5));

        workspace.StageRecompress(row, EntryCompression.ZStandard, 3);
        workspace.StageAddOrReplace(workspace.Archives[0], row.Path, replacement,
            EntryCompression.ZStandard, 3, allowOverwrite: true, replaceKey: row.Key);
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        var entry = Assert.Single(reopened.Entries);
        Assert.Equal(replacement.Length, entry.DecompressedSize);
        Assert.Equal(replacement, reopened.Extract(entry));
    }

    /// <summary>
    /// Renaming an entry and then replacing its content must keep both: the new name and the new bytes.
    /// The replace used to be filed under the new path while the writer looked it up under the old one,
    /// so it was silently dropped.
    /// </summary>
    [Fact]
    public void RenameThenReplace_KeepsBothTheNewNameAndTheNewBytes()
    {
        string pkz = _temp.File("rn.pkz");
        PkzFixture.WriteStored(pkz, ("pl/pl0000.dat", Text("ORIGINAL")));
        using var workspace = _harness.Open(pkz);

        workspace.StageRename(workspace.AllFiles.Single(), "pl0009.dat");
        var renamed = workspace.AllFiles.Single(f => f.Path == "pl/pl0009.dat");

        workspace.StageAddOrReplace(workspace.Archives[0], renamed.Path, Text("REPLACED"),
            EntryCompression.None, 0, allowOverwrite: true, replaceKey: renamed.Key);
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        var entry = Assert.Single(reopened.Entries);
        Assert.Equal("pl/pl0009.dat", entry.Name);
        Assert.Equal("REPLACED", ReadBack(reopened, "pl/pl0009.dat"));
    }

    /// <summary>Replacing content and then renaming must also keep both.</summary>
    [Fact]
    public void ReplaceThenRename_KeepsBoth()
    {
        string pkz = _temp.File("rn2.pkz");
        PkzFixture.WriteStored(pkz, ("a.dat", Text("ORIGINAL")));
        using var workspace = _harness.Open(pkz);

        var row = workspace.AllFiles.Single();
        workspace.StageAddOrReplace(workspace.Archives[0], row.Path, Text("REPLACED"),
            EntryCompression.None, 0, allowOverwrite: true, replaceKey: row.Key);
        workspace.StageRename(workspace.AllFiles.Single(), "b.dat");
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        Assert.Equal("b.dat", Assert.Single(reopened.Entries).Name);
        Assert.Equal("REPLACED", ReadBack(reopened, "b.dat"));
    }

    /// <summary>
    /// Deleting an entry and adding it back must store the new bytes, not the ones the archive already
    /// had. The add used to overwrite the delete's dictionary slot and then fail to replace anything.
    /// </summary>
    [Fact]
    public void DeleteThenReAdd_StoresTheNewBytes()
    {
        string pkz = _temp.File("da.pkz");
        PkzFixture.WriteStored(pkz, ("pl/pl0000.dat", Text("ORIGINAL")));
        using var workspace = _harness.Open(pkz);

        workspace.StageDelete(new[] { workspace.AllFiles.Single() });
        workspace.StageAddOrReplace(workspace.Archives[0], "pl/pl0000.dat", Text("READDED"),
            EntryCompression.None, 0, allowOverwrite: true);
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        Assert.Equal("READDED", ReadBack(reopened, "pl/pl0000.dat"));
    }

    /// <summary>
    /// Renaming a folder and then a file inside it must apply both. The second rename used to be filed
    /// under a path the writer never consulted, so it was dropped while still reporting success.
    /// </summary>
    [Fact]
    public void FolderRenameThenFileRename_AppliesBoth()
    {
        string pkz = _temp.File("fr.pkz");
        PkzFixture.WriteStored(pkz,
            ("dir/x.dat", Text("X")),
            ("dir/y.dat", Text("Y")));
        using var workspace = _harness.Open(pkz);

        // Both files under the folder are renamed, so the count is the number of entries affected.
        Assert.Equal(2, workspace.StageRename(workspace.FindFolder("dir")!, "newdir"));

        var inner = workspace.AllFiles.Single(f => f.Path == "newdir/x.dat");
        Assert.Equal(1, workspace.StageRename(inner, "z.dat"));

        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        Assert.Equal(new[] { "newdir/y.dat", "newdir/z.dat" },
            reopened.Entries.Select(e => e.Name).OrderBy(n => n));
    }

    /// <summary>The reverse order must not split one folder into two.</summary>
    [Fact]
    public void FileRenameThenFolderRename_AppliesBoth()
    {
        string pkz = _temp.File("fr2.pkz");
        PkzFixture.WriteStored(pkz,
            ("dir/x.dat", Text("X")),
            ("dir/y.dat", Text("Y")));
        using var workspace = _harness.Open(pkz);

        Assert.Equal(1, workspace.StageRename(workspace.AllFiles.Single(f => f.Path == "dir/x.dat"), "z.dat"));
        Assert.Equal(2, workspace.StageRename(workspace.FindFolder("dir")!, "newdir"));

        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        Assert.Equal(new[] { "newdir/y.dat", "newdir/z.dat" },
            reopened.Entries.Select(e => e.Name).OrderBy(n => n));
    }

    /// <summary>Two renames of one file move it twice, ending at the last name.</summary>
    [Fact]
    public void TwoRenamesOfOneFile_EndAtTheLastName()
    {
        string pkz = _temp.File("rr.pkz");
        PkzFixture.WriteStored(pkz, ("a.dat", Text("A")));
        using var workspace = _harness.Open(pkz);

        workspace.StageRename(workspace.AllFiles.Single(), "b.dat");
        workspace.StageRename(workspace.AllFiles.Single(), "c.dat");
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        Assert.Equal("c.dat", Assert.Single(reopened.Entries).Name);
        Assert.Equal("A", ReadBack(reopened, "c.dat"));
    }

    /// <summary>A rename may change only the case of a name, which an ordinal comparison allows.</summary>
    [Fact]
    public void Rename_CanChangeOnlyTheCase()
    {
        string pkz = _temp.File("case.pkz");
        PkzFixture.WriteStored(pkz, ("Data.dat", Text("A")));
        using var workspace = _harness.Open(pkz);

        Assert.Equal(1, workspace.StageRename(workspace.AllFiles.Single(), "data.dat"));
        workspace.Save();

        using var reopened = PkzArchive.Open(pkz);
        Assert.Equal("data.dat", Assert.Single(reopened.Entries).Name);
    }

    /// <summary>A recompress to the storage an entry already has must not rewrite the archive.</summary>
    [Fact]
    public void RecompressToTheSameStorage_LeavesTheArchiveUntouched()
    {
        string pkz = _temp.File("same.pkz");
        var payload = Compressible(16384);
        PkzFixture.Write(pkz, ("data.dat", payload, EntryCompression.ZStandard));
        byte[] before = File.ReadAllBytes(pkz);

        using var workspace = _harness.Open(pkz);
        workspace.StageRecompress(workspace.AllFiles.Single(), EntryCompression.ZStandard, 3);

        Assert.False(workspace.IsModified);
        var outcome = workspace.Save();
        Assert.Equal(0, outcome.Written);
        Assert.Equal(before, File.ReadAllBytes(pkz));
    }

    /// <summary>
    /// A load in which every path fails must leave the current view — and the work staged in it — exactly
    /// as it was, rather than clearing it to replace it with nothing.
    /// </summary>
    [Fact]
    public void Load_ThatFailsCompletely_KeepsTheCurrentViewAndItsEdits()
    {
        string good = _temp.File("good.pkz");
        PkzFixture.WriteStored(good, ("keep.dat", Text("K")), ("other.dat", Text("O")));
        using var workspace = _harness.Open(good);

        workspace.StageDelete(new[] { workspace.AllFiles.Single(f => f.Path == "other.dat") });
        int filesBefore = workspace.AllFiles.Count();
        Assert.True(workspace.IsModified);

        var result = workspace.Load(new[] { _temp.File("missing-one.pkz"), _temp.File("missing-two.pkz") });

        Assert.Equal(0, result.Loaded);
        Assert.Equal(2, result.Failed);
        // The view survived intact, including the staged deletion.
        Assert.Single(workspace.Archives);
        Assert.Equal(filesBefore, workspace.AllFiles.Count());
        Assert.True(workspace.IsModified);
    }

    /// <summary>A load where some paths work still replaces the view, as the file menu promises.</summary>
    [Fact]
    public void Load_ThatPartlySucceeds_StillReplacesTheView()
    {
        string first = _temp.File("first.pkz");
        string second = _temp.File("second.pkz");
        PkzFixture.WriteStored(first, ("one.dat", Text("1")));
        PkzFixture.WriteStored(second, ("two.dat", Text("2")));

        using var workspace = _harness.Open(first);
        var result = workspace.Load(new[] { second, _temp.File("bad.pkz") });

        Assert.Equal(1, result.Loaded);
        Assert.Equal(1, result.Failed);
        Assert.Equal("two.dat", workspace.AllFiles.Single().Path);
    }

    /// <summary>
    /// A rename must not leave the old row on screen next to the new one: the row is re-filed, so the
    /// entry count is unchanged.
    /// </summary>
    [Fact]
    public void Rename_ReFilesTheRowRatherThanAddingASecondOne()
    {
        string pkz = _temp.File("rows.pkz");
        PkzFixture.WriteStored(pkz, ("a.dat", Text("A")));
        using var workspace = _harness.Open(pkz);

        workspace.StageRename(workspace.AllFiles.Single(), "b.dat");

        var only = Assert.Single(workspace.AllFiles);
        Assert.Equal("b.dat", only.Path);
        Assert.True(only.IsModified);
    }

    /// <summary>
    /// The suffixed form a listing prints must address exactly the row it names, for the first copy of a
    /// duplicated path as well as the second. The first copy's listed name is its bare path, so the
    /// archive-qualified form has to be accepted alongside it.
    /// </summary>
    [Fact]
    public void Delete_CanTargetEitherCopyOfADuplicatedPathByItsArchiveName()
    {
        string first = _temp.File("data000.pkz");
        string second = _temp.File("data001.pkz");
        PkzFixture.WriteStored(first, ("dup.dat", Text("A")));
        PkzFixture.WriteStored(second, ("dup.dat", Text("B")));

        // The first copy, addressed by its archive name.
        RunCli("-d", "--entry", "dup.dat - data000.pkz", first, second);

        using var firstArchive = PkzArchive.Open(first);
        using var secondArchive = PkzArchive.Open(second);
        Assert.Empty(firstArchive.Entries);
        Assert.Single(secondArchive.Entries);
    }

    [Fact]
    public void Delete_ByBarePath_RemovesEveryCopy()
    {
        string first = _temp.File("data000.pkz");
        string second = _temp.File("data001.pkz");
        PkzFixture.WriteStored(first, ("dup.dat", Text("A")));
        PkzFixture.WriteStored(second, ("dup.dat", Text("B")));

        RunCli("-d", "--entry", "dup.dat", first, second);

        using var firstArchive = PkzArchive.Open(first);
        using var secondArchive = PkzArchive.Open(second);
        Assert.Empty(firstArchive.Entries);
        Assert.Empty(secondArchive.Entries);
    }

    /// <summary>
    /// A single replace invocation must write each entry back into the archive it came from, so a run
    /// spanning two archives changes both rather than writing everything into the first.
    /// </summary>
    [Fact]
    public void Replace_AcrossTwoArchives_WritesEachEntryToItsOwnArchive()
    {
        string first = _temp.File("data000.pkz");
        string second = _temp.File("data001.pkz");
        PkzFixture.WriteStored(first, ("pl/a.dat", Text("A-orig")));
        PkzFixture.WriteStored(second, ("em/b.dat", Text("B-orig")));

        string source = Path.Combine(_temp.Path, "src");
        Directory.CreateDirectory(Path.Combine(source, "pl"));
        Directory.CreateDirectory(Path.Combine(source, "em"));
        File.WriteAllText(Path.Combine(source, "pl", "a.dat"), "A-NEW");
        File.WriteAllText(Path.Combine(source, "em", "b.dat"), "B-NEW");

        var log = new List<string>();
        var errors = new List<string>();
        int code = new CliRunner(log.Add, errors.Add, new PkzSettings())
            .Run(new[] { "-r", "--entry", "pl/a.dat", "--entry", "em/b.dat", "--out", source, first, second });

        Assert.Equal(0, code);
        Assert.Empty(errors);

        using var firstArchive = PkzArchive.Open(first);
        using var secondArchive = PkzArchive.Open(second);
        Assert.Equal("A-NEW", ReadBack(firstArchive, "pl/a.dat"));
        Assert.Equal("B-NEW", ReadBack(secondArchive, "em/b.dat"));
    }

    /// <summary><c>--pkz</c> narrows a replace to one archive instead of redirecting every entry.</summary>
    [Fact]
    public void Replace_WithPkz_RestrictsTheWorkToOneArchive()
    {
        string first = _temp.File("data000.pkz");
        string second = _temp.File("data001.pkz");
        PkzFixture.WriteStored(first, ("pl/a.dat", Text("A-orig")));
        PkzFixture.WriteStored(second, ("em/b.dat", Text("B-orig")));

        string source = Path.Combine(_temp.Path, "src");
        Directory.CreateDirectory(Path.Combine(source, "pl"));
        Directory.CreateDirectory(Path.Combine(source, "em"));
        File.WriteAllText(Path.Combine(source, "pl", "a.dat"), "A-NEW");
        File.WriteAllText(Path.Combine(source, "em", "b.dat"), "B-NEW");

        var log = new List<string>();
        var errors = new List<string>();
        int code = new CliRunner(log.Add, errors.Add, new PkzSettings())
            .Run(new[] { "-r", "--entry", "pl/a.dat", "--entry", "em/b.dat", "--out", source,
                "--pkz", "data000.pkz", first, second });

        Assert.Equal(0, code);

        using var firstArchive = PkzArchive.Open(first);
        using var secondArchive = PkzArchive.Open(second);
        Assert.Equal("A-NEW", ReadBack(firstArchive, "pl/a.dat"));
        // The other archive was not named, so its entry is untouched.
        Assert.Equal("B-orig", ReadBack(secondArchive, "em/b.dat"));
    }

    /// <summary>
    /// Adding many new files must stay fast enough to be usable. Each add used to rebuild the whole
    /// tree, so a bulk add cost time proportional to the archive size per file.
    /// <para>
    /// The assertion is deliberately loose (a second for 300 adds against a 4000-entry archive, where
    /// the old code needed tens of seconds) so it reports a return of the quadratic behaviour rather
    /// than a slow machine.
    /// </para>
    /// </summary>
    [Fact]
    public void StageAdd_ManyFiles_DoesNotRebuildTheTreePerFile()
    {
        string pkz = _temp.File("bulk.pkz");
        var existing = new List<(string, byte[])>(4000);
        for (int i = 0; i < 4000; i++)
            existing.Add(($"d{i % 50}/f{i}.dat", Text("x")));
        PkzFixture.WriteStored(pkz, existing.ToArray());

        using var workspace = _harness.Open(pkz);
        var archive = workspace.Archives[0];

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 300; i++)
            workspace.StageAddOrReplace(archive, $"new/f{i}.dat", Text("y"),
                EntryCompression.None, 0, allowOverwrite: true);
        stopwatch.Stop();

        Assert.Equal(4300, workspace.AllFiles.Count());
        Assert.True(stopwatch.ElapsedMilliseconds < 1000,
            $"300 adds took {stopwatch.ElapsedMilliseconds}ms, which suggests the tree is being rebuilt per file");

        // A path added in this run must be visible to a later collision test.
        Assert.True(workspace.WouldCollide(archive, "new/f0.dat"));
        Assert.False(workspace.WouldCollide(archive, "new/not-added.dat"));

        workspace.Save();
        using var reopened = PkzArchive.Open(pkz);
        Assert.Equal(4300, reopened.Entries.Count);
        Assert.Equal("y", ReadBack(reopened, "new/f299.dat"));
    }

    /// <summary>
    /// Reverting one row must undo only that row's staged changes, leaving every other change alone:
    /// discarding everything to undo one mistake would lose unrelated work.
    /// </summary>
    [Fact]
    public void RevertEdits_RestoresOneRowAndLeavesTheRestStaged()
    {
        string pkz = _temp.File("revert.pkz");
        PkzFixture.WriteStored(pkz,
            ("a.dat", Text("A")),
            ("b.dat", Text("B")));
        using var workspace = _harness.Open(pkz);

        var first = workspace.AllFiles.Single(f => f.Path == "a.dat");
        var second = workspace.AllFiles.Single(f => f.Path == "b.dat");

        workspace.StageAddOrReplace(workspace.Archives[0], first.Path, Text("A-EDITED"),
            EntryCompression.None, 0, allowOverwrite: true, replaceKey: first.Key);
        workspace.StageAddOrReplace(workspace.Archives[0], second.Path, Text("B-EDITED"),
            EntryCompression.None, 0, allowOverwrite: true, replaceKey: second.Key);

        workspace.RevertEdits(new[] { first });

        var a = workspace.AllFiles.Single(f => f.Path == "a.dat");
        var b = workspace.AllFiles.Single(f => f.Path == "b.dat");
        Assert.False(a.IsModified);
        Assert.True(b.IsModified);

        workspace.Save();
        using var reopened = PkzArchive.Open(pkz);
        // The reverted row is back to what the archive stored; the other keeps its edit.
        Assert.Equal("A", ReadBack(reopened, "a.dat"));
        Assert.Equal("B-EDITED", ReadBack(reopened, "b.dat"));
    }

    /// <summary>
    /// Reverting a row that was staged in several ways must undo all of them at once: the composed state
    /// is one thing, so it reverts as one thing.
    /// </summary>
    [Fact]
    public void RevertEdits_UndoesEveryChangeToThatRowAtOnce()
    {
        string pkz = _temp.File("revertall.pkz");
        var original = Compressible(16384);
        PkzFixture.Write(pkz, ("a.dat", original, EntryCompression.None));
        using var workspace = _harness.Open(pkz);

        var row = workspace.AllFiles.Single();
        workspace.StageRename(row, "renamed.dat");
        var moved = workspace.AllFiles.Single(f => f.Path == "renamed.dat");
        workspace.StageAddOrReplace(workspace.Archives[0], moved.Path, Text("NEW"),
            EntryCompression.None, 0, allowOverwrite: true, replaceKey: moved.Key);
        workspace.StageRecompress(workspace.AllFiles.Single(), EntryCompression.ZStandard, 3);

        workspace.RevertEdits(new[] { workspace.AllFiles.Single() });

        Assert.False(workspace.IsModified);
        var only = Assert.Single(workspace.AllFiles);
        Assert.Equal("a.dat", only.Path);
        Assert.Equal(EntryCompression.None, only.Compression);

        workspace.Save();
        using var reopened = PkzArchive.Open(pkz);
        var entry = Assert.Single(reopened.Entries);
        Assert.Equal("a.dat", entry.Name);
        Assert.Equal(EntryCompression.None, entry.Compression);
        Assert.Equal(original, reopened.Extract(entry));
    }

    private void RunCli(params string[] args)
    {
        int code = new CliRunner(_ => { }, errors => throw new Xunit.Sdk.XunitException(errors),
            new PkzSettings()).Run(args);
        Assert.Equal(0, code);
    }
}
