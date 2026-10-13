using PkzTool.Core.Archives;
using PkzTool.Core.Vfs;
using Xunit;

namespace PkzTool.Core.Tests;

/// <summary>
/// The merged virtual filesystem: one continuous tree across several archives, real folder rows, and
/// the " - name.pkz" suffix when a path exists in more than one archive.
/// </summary>
public class VfsMergeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PkzWorkspaceHarness _harness;

    public VfsMergeTests() => _harness = new PkzWorkspaceHarness();

    public void Dispose()
    {
        _harness.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void Merge_BuildsARealFolderTreeFromTheEntryPaths()
    {
        string pkz = _temp.File("data000.pkz");
        PkzFixture.WriteStored(pkz,
            ("pl/pl0000.dat", PkzFixture.Text("a")),
            ("effect/texture/001.wta", PkzFixture.Text("b")),
            ("shader2.dat", PkzFixture.Text("c")));

        using var workspace = _harness.Open(pkz);

        // Root holds one file and two top-level folders.
        Assert.Equal(new[] { "effect", "pl" }, workspace.Root.ChildFolders.Select(f => f.DisplayName).OrderBy(x => x));
        Assert.Equal(new[] { "shader2.dat" }, workspace.Root.ChildFiles.Select(f => f.DisplayName));

        var pl = workspace.FindFolder("pl")!;
        Assert.Equal("pl0000.dat", Assert.Single(pl.ChildFiles).DisplayName);

        // The nesting is real, not a flattened path in a name column.
        var texture = workspace.FindFolder("effect/texture")!;
        Assert.Equal("001.wta", Assert.Single(texture.ChildFiles).DisplayName);
        Assert.Same(workspace.FindFolder("effect"), texture.Parent);
    }

    [Fact]
    public void Merge_UsesForwardSlashesEverywhere()
    {
        string pkz = _temp.File("data000.pkz");
        PkzFixture.WriteStored(pkz, ("effect/texture/001.wta", PkzFixture.Text("x")));

        using var workspace = _harness.Open(pkz);
        var file = Assert.Single(workspace.AllFiles);
        Assert.Equal("effect/texture/001.wta", file.Path);
        Assert.DoesNotContain('\\', file.Path);
    }

    /// <summary>
    /// A path present in two archives becomes two rows in the same folder, and each row keeps its own
    /// archive so an edit can target exactly one of them.
    /// </summary>
    [Fact]
    public void Merge_GivesEveryArchiveItsOwnRowWithTheSuffix()
    {
        string first = _temp.File("data000.pkz");
        string second = _temp.File("data001.pkz");
        PkzFixture.WriteStored(first, ("pl/pl0000.dat", PkzFixture.Text("from 000")));
        PkzFixture.WriteStored(second, ("pl/pl0000.dat", PkzFixture.Text("from 001")));

        using var workspace = _harness.Open(first, second);

        var pl = workspace.FindFolder("pl")!;
        Assert.Equal(2, pl.ChildFiles.Count);

        // The first archive keeps the plain name; the second is suffixed so the rows are telling apart.
        Assert.Equal("pl0000.dat", pl.ChildFiles[0].DisplayName);
        Assert.Equal("pl0000.dat - data001.pkz", pl.ChildFiles[1].DisplayName);

        // Both rows carry the same un-suffixed virtual path, which is what gets written to an archive.
        Assert.All(pl.ChildFiles, f => Assert.Equal("pl/pl0000.dat", f.Path));

        // Each row reads from its own archive.
        Assert.Equal("from 000", System.Text.Encoding.UTF8.GetString(workspace.ReadData(pl.ChildFiles[0])));
        Assert.Equal("from 001", System.Text.Encoding.UTF8.GetString(workspace.ReadData(pl.ChildFiles[1])));

        // The keys differ, which is what lets the UI aim an operation at one row.
        Assert.NotEqual(pl.ChildFiles[0].Key, pl.ChildFiles[1].Key);
    }

    [Fact]
    public void Merge_MarksBothRowsOfADuplicatedPathAsShadowed()
    {
        string first = _temp.File("a.pkz");
        string second = _temp.File("b.pkz");
        PkzFixture.WriteStored(first, ("s.dat", PkzFixture.Text("one")));
        PkzFixture.WriteStored(second, ("s.dat", PkzFixture.Text("two")));

        using var workspace = _harness.Open(first, second);
        var rows = workspace.Root.ChildFiles;
        Assert.All(rows, r => Assert.True(r.IsShadowed));
    }

    [Fact]
    public void Merge_LeavesAUniquePathUnsuffixed()
    {
        string first = _temp.File("a.pkz");
        string second = _temp.File("b.pkz");
        PkzFixture.WriteStored(first, ("only/here.dat", PkzFixture.Text("one")));
        PkzFixture.WriteStored(second, ("elsewhere.dat", PkzFixture.Text("two")));

        using var workspace = _harness.Open(first, second);

        var here = workspace.FindFolder("only")!.ChildFiles.Single();
        Assert.Equal("here.dat", here.DisplayName);
        Assert.False(here.IsShadowed);
    }

    [Fact]
    public void Merge_FoldsFoldersFromSeveralArchivesIntoOneRow()
    {
        string first = _temp.File("a.pkz");
        string second = _temp.File("b.pkz");
        PkzFixture.WriteStored(first, ("pl/pl0000.dat", PkzFixture.Text("a")));
        PkzFixture.WriteStored(second, ("pl/pl0001.dat", PkzFixture.Text("b")));

        using var workspace = _harness.Open(first, second);

        // One "pl" folder, holding both archives' files: there is no per-archive separation.
        var pl = Assert.Single(workspace.Root.ChildFolders);
        Assert.Equal("pl", pl.DisplayName);
        Assert.Equal(new[] { "pl0000.dat", "pl0001.dat" }, pl.ChildFiles.Select(f => f.DisplayName));
    }

    [Fact]
    public void Merge_KeepsTheStoredOrderOfFilesWithinAFolder()
    {
        string pkz = _temp.File("order.pkz");
        PkzFixture.WriteStored(pkz,
            ("pl/zz.dat", PkzFixture.Text("1")),
            ("pl/aa.dat", PkzFixture.Text("2")),
            ("pl/mm.dat", PkzFixture.Text("3")));

        using var workspace = _harness.Open(pkz);
        var pl = workspace.FindFolder("pl")!;
        Assert.Equal(new[] { "zz.dat", "aa.dat", "mm.dat" }, pl.ChildFiles.Select(f => f.DisplayName));
    }

    [Fact]
    public void FileFormat_ComesFromTheExtensionOnly()
    {
        string pkz = _temp.File("formats.pkz");
        PkzFixture.WriteStored(pkz,
            ("a/b.DAT", PkzFixture.Text("1")),
            ("c/d.wta", PkzFixture.Text("2")),
            ("noextension", PkzFixture.Text("3")),
            ("trailingdot.", PkzFixture.Text("4")));

        using var workspace = _harness.Open(pkz);
        var byName = workspace.AllFiles.ToDictionary(f => f.Path, f => f.Format, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("DAT", byName["a/b.DAT"]);
        Assert.Equal("WTA", byName["c/d.wta"]);
        Assert.Equal("\u2014", byName["noextension"]);
        Assert.Equal("\u2014", byName["trailingdot."]);
    }

    [Fact]
    public void FolderSizes_AreZeroAndFilesReportUncompressedSize()
    {
        string pkz = _temp.File("sizes.pkz");
        var payload = PkzFixture.Compressible(5000);
        PkzFixture.Write(pkz, ("pl/pl0000.dat", payload, EntryCompression.ZStandard));

        using var workspace = _harness.Open(pkz);
        var file = workspace.FindFolder("pl")!.ChildFiles.Single();

        Assert.Equal(payload.Length, file.UncompressedSize);
        Assert.Equal(0, workspace.FindFolder("pl")!.UncompressedSize);
    }

    [Fact]
    public void DescendantFiles_ReturnsEverythingUnderAFolder()
    {
        string pkz = _temp.File("tree.pkz");
        PkzFixture.WriteStored(pkz,
            ("pl/pl0000.dat", PkzFixture.Text("1")),
            ("pl/sub/deep.dat", PkzFixture.Text("2")),
            ("pl/sub/more/even.dat", PkzFixture.Text("3")),
            ("other.dat", PkzFixture.Text("4")));

        using var workspace = _harness.Open(pkz);
        var pl = workspace.FindFolder("pl")!;

        Assert.Equal(3, pl.DescendantFiles().Count());
        Assert.Equal(4, workspace.Root.DescendantFiles().Count());
        // DescendantFolders excludes the node itself: it lists what is *under* pl, not pl.
        Assert.Equal(new[] { "pl/sub", "pl/sub/more" },
            pl.DescendantFolders().Select(f => f.Path));
        // From the root, every folder in the archive is a descendant.
        Assert.Equal(new[] { "pl", "pl/sub", "pl/sub/more" },
            workspace.Root.DescendantFolders().Select(f => f.Path));
    }

    /// <summary>A folder row is selectable and takes its whole subtree with it.</summary>
    [Fact]
    public void FolderSelection_CoversItsWholeSubtree()
    {
        string pkz = _temp.File("sel.pkz");
        PkzFixture.WriteStored(pkz,
            ("pl/pl0000.dat", PkzFixture.Text("1")),
            ("pl/sub/deep.dat", PkzFixture.Text("2")),
            ("other.dat", PkzFixture.Text("3")));

        using var workspace = _harness.Open(pkz);

        var selected = workspace.FindFolder("pl")!;
        var covered = selected.DescendantFiles().Select(f => f.Path).OrderBy(p => p).ToList();

        Assert.Equal(new[] { "pl/pl0000.dat", "pl/sub/deep.dat" }, covered);
    }
}
