using PkzTool.Core.Archives;
using PkzTool.Core.Cli;
using PkzTool.Core.Compression;
using PkzTool.Core.Operations;
using PkzTool.Core.Settings;
using Xunit;

namespace PkzTool.Core.Tests;

/// <summary>
/// The headless surface: argument parsing, the entry selectors, and each operation end to end through
/// the real workspace.
/// </summary>
public class CliTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<string> _out = new();
    private readonly List<string> _err = new();

    private CliRunner Runner() => new(_out.Add, _err.Add, new PkzSettings());

    private string OutText => string.Join("\n", _out);
    private string ErrText => string.Join("\n", _err);

    public void Dispose() => _temp.Dispose();

    // --- parsing --------------------------------------------------------------------------

    [Fact]
    public void Parse_RecognisesEveryOperationFlagAndItsAlias()
    {
        Assert.Equal(CliOperation.List, ParseWithAlias("--list"));
        Assert.Equal(CliOperation.List, ParseWithAlias("-l"));
        Assert.Equal(CliOperation.Extract, ParseWithAlias("--extract"));
        Assert.Equal(CliOperation.Extract, ParseWithAlias("-x"));
        Assert.Equal(CliOperation.ExtractAll, ParseWithAlias("--extract-all"));
        Assert.Equal(CliOperation.ExtractAll, ParseWithAlias("-X"));
        Assert.Equal(CliOperation.Replace, ParseWithAlias("--replace"));
        Assert.Equal(CliOperation.Replace, ParseWithAlias("-r"));
        Assert.Equal(CliOperation.Recompress, ParseWithAlias("--recompress"));
        Assert.Equal(CliOperation.Recompress, ParseWithAlias("-c"));
        Assert.Equal(CliOperation.Add, ParseWithAlias("--add"));
        Assert.Equal(CliOperation.Add, ParseWithAlias("-a"));
        Assert.Equal(CliOperation.Delete, ParseWithAlias("--delete"));
        Assert.Equal(CliOperation.Delete, ParseWithAlias("-d"));
        Assert.Equal(CliOperation.Rename, ParseWithAlias("--rename"));
        Assert.Equal(CliOperation.Rename, ParseWithAlias("-n"));
        Assert.Equal(CliOperation.CreateFromFolder, ParseWithAlias("--create-from-folder"));
        Assert.Equal(CliOperation.CreateFromFolder, ParseWithAlias("-f"));
    }

    /// <summary>Parse one operation flag with whatever options that operation requires.</summary>
    private static CliOperation ParseWithAlias(string flag)
    {
        var args = new List<string> { flag };
        string bare = flag.TrimStart('-');
        args.AddRange(bare.ToLowerInvariant() switch
        {
            "l" or "list" => new[] { "a.pkz" },
            "x" or "extract" => new[] { "--entry", "p", "--out", "o", "a.pkz" },
            "X" or "extract-all" => new[] { "--out", "o", "a.pkz" },
            "r" or "replace" => new[] { "--entry", "p", "--out", "o", "a.pkz" },
            "c" or "recompress" => new[] { "--entry", "p", "--compression", "zstandard", "a.pkz" },
            "a" or "add" => new[] { "--out", "o", "a.pkz" },
            "d" or "delete" => new[] { "--entry", "p", "a.pkz" },
            "n" or "rename" => new[] { "--entry", "p", "--name", "n", "a.pkz" },
            "f" or "create-from-folder" => new[] { "--out", "o", "out.pkz" },
            _ => Array.Empty<string>(),
        });
        return CliParser.Parse(args).Operation;
    }

    [Fact]
    public void Parse_TreatsEverythingAfterTheFirstBareArgumentAsAPath()
    {
        // The options come first, the PKZ paths last, so a later "--verbose" is a file name.
        var request = CliParser.Parse(new[] { "--list", "a.pkz", "--verbose" });

        Assert.False(request.Verbose);
        Assert.Equal(new[] { "a.pkz", "--verbose" }, request.Archives);
    }

    [Fact]
    public void Parse_CollectsRepeatedEntriesAndAllOptions()
    {
        var request = CliParser.Parse(new[]
        {
            "-x", "--entry", "pl/*.dat", "--entry", "core", "--out", "C:\\out",
            "--verbose", "--dry-run", "a.pkz", "b.pkz",
        });

        Assert.Equal(CliOperation.Extract, request.Operation);
        Assert.Equal(new[] { "pl/*.dat", "core" }, request.Entries);
        Assert.Equal("C:\\out", request.Out);
        Assert.Equal(new[] { "a.pkz", "b.pkz" }, request.Archives);
        Assert.True(request.Verbose);
        Assert.True(request.DryRun);
    }

    [Fact]
    public void Parse_DefaultsCompressionToNone() =>
        Assert.Equal(EntryCompression.None,
            CliParser.Parse(new[] { "-c", "--entry", "x", "--compression", "none", "a.pkz" }).EffectiveCompression);

    [Fact]
    public void Parse_RejectsARecompressWithoutACompression()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliParser.Parse(new[] { "-c", "--entry", "x", "a.pkz" }));
        Assert.Contains("--compression", ex.Message);
    }

    [Theory]
    [InlineData("none", EntryCompression.None)]
    [InlineData("zstandard", EntryCompression.ZStandard)]
    [InlineData("zstd", EntryCompression.ZStandard)]
    [InlineData("oodle", EntryCompression.OodleKraken)]
    public void Parse_AcceptsTheCompressionNames(string text, EntryCompression expected)
    {
        var request = CliParser.Parse(new[] { "-c", "--entry", "x", "--compression", text, "a.pkz" });
        Assert.Equal(expected, request.Compression);
    }

    [Fact]
    public void Parse_RejectsTwoOperationsInOneInvocation()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliParser.Parse(new[] { "--list", "--extract", "a.pkz" }));
        Assert.Contains("only one operation", ex.Message);
    }

    [Fact]
    public void Parse_RejectsAMissingOperation() =>
        Assert.Throws<CliUsageException>(() => CliParser.Parse(new[] { "a.pkz" }));

    [Fact]
    public void Parse_RejectsAnUnknownOption() =>
        Assert.Throws<CliUsageException>(() => CliParser.Parse(new[] { "--list", "--nope", "a.pkz" }));

    [Theory]
    [InlineData("-x", "--out", "o")]
    [InlineData("-r", "--out", "o")]
    [InlineData("-d")]
    public void Parse_RejectsAMutatingOperationWithoutAnEntry(string flag, params string[] extra)
    {
        var args = new List<string> { flag };
        args.AddRange(extra);
        args.Add("a.pkz");
        var ex = Assert.Throws<CliUsageException>(() => CliParser.Parse(args));
        Assert.Contains("--entry", ex.Message);
    }

    [Fact]
    public void Parse_RejectsAnExtractWithoutAnOutputFolder()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliParser.Parse(new[] { "-x", "--entry", "x", "a.pkz" }));
        Assert.Contains("--out", ex.Message);
    }

    [Fact]
    public void Parse_RejectsARenameWithoutANewName()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliParser.Parse(new[] { "-n", "--entry", "x", "a.pkz" }));
        Assert.Contains("--name", ex.Message);
    }

    [Fact]
    public void IsHelp_DetectsTheHelpFlags()
    {
        Assert.True(CliParser.IsHelp(new[] { "--help" }));
        Assert.True(CliParser.IsHelp(new[] { "-h" }));
        Assert.False(CliParser.IsHelp(new[] { "--list", "a.pkz" }));
    }

    // --- selectors ------------------------------------------------------------------------

    [Theory]
    [InlineData("pl/pl0000.dat", true)]
    [InlineData("PL/PL0000.DAT", true)]
    [InlineData("pl/pl0001.dat", false)]
    [InlineData("other/pl0000.dat", false)]
    public void Glob_ExactPathMatchesOnlyThatPath(string selector, bool expected)
    {
        var regex = new System.Text.RegularExpressions.Regex(
            EntrySelector.GlobToRegex(selector), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.Equal(expected, regex.IsMatch("pl/pl0000.dat"));
    }

    [Theory]
    [InlineData("pl/*.dat", "pl/pl0000.dat", true)]
    [InlineData("pl/*.dat", "pl/sub/pl0000.dat", false)]  // a single star does not cross a separator
    [InlineData("pl/**/*.dat", "pl/sub/deep/pl0000.dat", true)] // a double star does
    [InlineData("*.dat", "pl0000.dat", true)]
    [InlineData("*.dat", "pl/pl0000.dat", false)]
    [InlineData("pl/pl000?.dat", "pl/pl0000.dat", true)]
    [InlineData("pl/pl000?.dat", "pl/pl00000.dat", false)]
    public void Glob_StarDoesNotCrossSeparatorsButDoubleStarDoes(string glob, string path, bool expected)
    {
        var regex = new System.Text.RegularExpressions.Regex(
            EntrySelector.GlobToRegex(glob), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.Equal(expected, regex.IsMatch(path));
    }

    // --- operations -----------------------------------------------------------------------

    [Fact]
    public void List_PrintsColumnsAndExitsZero()
    {
        string pkz = _temp.File("data.pkz");
        PkzFixture.WriteStored(pkz, ("pl/pl0000.dat", PkzFixture.Text("hello")));

        int code = Runner().Run(new[] { "-l", pkz });

        Assert.Equal(0, code);
        Assert.Contains("Path", OutText);
        Assert.Contains("Size", OutText);
        Assert.Contains("Format", OutText);
        Assert.Contains("Compression", OutText);
        Assert.Contains("pl/pl0000.dat", OutText);
        Assert.Contains("DAT", OutText);
        Assert.Equal(string.Empty, ErrText);
    }

    [Fact]
    public void List_NamesADuplicatedPathWithTheSuffix()
    {
        string first = _temp.File("data000.pkz");
        string second = _temp.File("data001.pkz");
        PkzFixture.WriteStored(first, ("pl/pl0000.dat", PkzFixture.Text("a")));
        PkzFixture.WriteStored(second, ("pl/pl0000.dat", PkzFixture.Text("b")));

        int code = Runner().Run(new[] { "-l", first, second });

        Assert.Equal(0, code);
        Assert.Contains("pl/pl0000.dat", OutText);
        Assert.Contains("pl/pl0000.dat - data001.pkz", OutText);
    }

    [Fact]
    public void List_CsvExportWritesAHeaderAndRows()
    {
        string pkz = _temp.File("data.pkz");
        string csv = _temp.File("out.csv");
        PkzFixture.WriteStored(pkz, ("pl/pl0000.dat", PkzFixture.Text("hello")));

        int code = Runner().Run(new[] { "-l", "--csv", csv, pkz });

        Assert.Equal(0, code);
        var lines = File.ReadAllLines(csv);
        Assert.Equal("Path,Size,Format,Compression", lines[0]);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("pl/pl0000.dat,", lines[1]);
        Assert.Contains("DAT", lines[1]);
    }

    /// <summary>A destination folder that does not exist yet, so a test cannot be fooled by leftovers.</summary>
    private string FreshOutDir(string name = "out") => Path.Combine(_temp.Path, name);

    [Fact]
    public void Extract_WritesTheFolderStructureUnderOut()
    {
        string pkz = _temp.File("data.pkz");
        string outDir = FreshOutDir();
        PkzFixture.WriteStored(pkz,
            ("pl/pl0000.dat", PkzFixture.Text("payload")),
            ("effect/texture/001.wta", PkzFixture.Text("other")));

        int code = Runner().Run(new[] { "-x", "--entry", "pl/*.dat", "--out", outDir, pkz });

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(outDir, "pl", "pl0000.dat")));
        // Only the selected entry was written.
        Assert.False(File.Exists(Path.Combine(outDir, "effect", "texture", "001.wta")));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(outDir, "pl", "pl0000.dat")));
    }

    [Fact]
    public void ExtractAll_WritesEveryEntry()
    {
        string pkz = _temp.File("data.pkz");
        string outDir = FreshOutDir();
        PkzFixture.WriteStored(pkz,
            ("pl/pl0000.dat", PkzFixture.Text("a")),
            ("effect/texture/001.wta", PkzFixture.Text("b")));

        int code = Runner().Run(new[] { "-X", "--out", outDir, pkz });

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(outDir, "pl", "pl0000.dat")));
        Assert.True(File.Exists(Path.Combine(outDir, "effect", "texture", "001.wta")));
    }

    [Fact]
    public void Extract_OverwritesWithoutPrompting()
    {
        string pkz = _temp.File("data.pkz");
        string outDir = FreshOutDir();
        Directory.CreateDirectory(Path.Combine(outDir, "pl"));
        File.WriteAllText(Path.Combine(outDir, "pl", "pl0000.dat"), "stale");
        PkzFixture.WriteStored(pkz, ("pl/pl0000.dat", PkzFixture.Text("fresh")));

        int code = Runner().Run(new[] { "-x", "--entry", "pl/pl0000.dat", "--out", outDir, pkz });

        Assert.Equal(0, code);
        Assert.Equal("fresh", File.ReadAllText(Path.Combine(outDir, "pl", "pl0000.dat")));
    }

    [Fact]
    public void Extract_FailsWhenAnEntryMatchesNothing()
    {
        string pkz = _temp.File("data.pkz");
        PkzFixture.WriteStored(pkz, ("pl/pl0000.dat", PkzFixture.Text("a")));

        int code = Runner().Run(new[] { "-x", "--entry", "nothing/*.dat", "--out", FreshOutDir(), pkz });

        Assert.NotEqual(0, code);
        Assert.Contains("no entry matched", ErrText);
    }

    [Fact]
    public void Delete_RemovesTheEntryAndSavesImmediately()
    {
        string pkz = _temp.File("data.pkz");
        PkzFixture.WriteStored(pkz,
            ("keep.dat", PkzFixture.Text("k")),
            ("drop.dat", PkzFixture.Text("d")));

        int code = Runner().Run(new[] { "-d", "--entry", "drop.dat", pkz });

        Assert.Equal(0, code);
        using var archive = PkzArchive.Open(pkz);
        Assert.Equal("keep.dat", Assert.Single(archive.Entries).Name);
    }

    [Fact]
    public void Rename_ChangesTheNameAndKeepsThePayload()
    {
        string pkz = _temp.File("data.pkz");
        PkzFixture.WriteStored(pkz, ("pl/pl0000.dat", PkzFixture.Text("payload")));

        int code = Runner().Run(new[] { "-n", "--entry", "pl/pl0000.dat", "--name", "pl0001.dat", pkz });

        Assert.Equal(0, code);
        using var archive = PkzArchive.Open(pkz);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("pl/pl0001.dat", entry.Name);
        Assert.Equal("payload", System.Text.Encoding.UTF8.GetString(archive.Extract(entry)));
    }

    [Fact]
    public void Rename_FailsWhenSeveralEntriesMatch()
    {
        string pkz = _temp.File("data.pkz");
        PkzFixture.WriteStored(pkz,
            ("pl/a.dat", PkzFixture.Text("a")),
            ("pl/b.dat", PkzFixture.Text("b")));

        int code = Runner().Run(new[] { "-n", "--entry", "pl/*.dat", "--name", "x.dat", pkz });

        Assert.NotEqual(0, code);
        Assert.Contains("exactly one entry", ErrText);
    }

    [Fact]
    public void Recompress_ChangesStorageAndObeysTheRule()
    {
        string pkz = _temp.File("data.pkz");
        var compressible = PkzFixture.Compressible(16384);
        var random = PkzFixture.Incompressible(4096, seed: 31);
        PkzFixture.WriteStored(pkz, ("good.dat", compressible), ("bad.dat", random));

        int code = Runner().Run(new[] { "-c", "--entry", "good.dat", "--entry", "bad.dat",
            "--compression", "zstandard", pkz });

        Assert.Equal(0, code);
        using var archive = PkzArchive.Open(pkz);
        // The compressible file takes the requested type; the random one falls back to stored.
        Assert.Equal(EntryCompression.ZStandard, archive.Find("good.dat")!.Compression);
        Assert.Equal(EntryCompression.None, archive.Find("bad.dat")!.Compression);
        Assert.Equal(compressible, archive.Extract(archive.Find("good.dat")!));
        Assert.Equal(random, archive.Extract(archive.Find("bad.dat")!));
    }

    [Fact]
    public void Recompress_ForceCompressionKeepsTheTypeOnAnIncompressibleFile()
    {
        string pkz = _temp.File("data.pkz");
        var random = PkzFixture.Incompressible(4096, seed: 33);
        PkzFixture.WriteStored(pkz, ("bad.dat", random));

        int code = Runner().Run(new[] { "-c", "--entry", "bad.dat", "--compression", "zstandard",
            "--force-compression", pkz });

        Assert.Equal(0, code);
        using var archive = PkzArchive.Open(pkz);
        var entry = archive.Find("bad.dat")!;
        Assert.Equal(EntryCompression.ZStandard, entry.Compression);
        // It still round-trips through the reader.
        Assert.Equal(random, archive.Extract(entry));
    }

    [Fact]
    public void Replace_TakesTheNewBytesFromTheMatchingPathUnderOut()
    {
        string pkz = _temp.File("data.pkz");
        PkzFixture.WriteStored(pkz, ("pl/pl0000.dat", PkzFixture.Text("original")));

        string source = _temp.WriteText("src/pl/pl0000.dat", "replacement");

        int code = Runner().Run(new[] { "-r", "--entry", "pl/pl0000.dat", "--out",
            Path.GetDirectoryName(Path.GetDirectoryName(source))!, pkz });

        Assert.Equal(0, code);
        using var archive = PkzArchive.Open(pkz);
        Assert.Equal("replacement",
            System.Text.Encoding.UTF8.GetString(archive.Extract(archive.Find("pl/pl0000.dat")!)));
    }

    [Fact]
    public void Replace_FailsWhenTheFileIsMissing()
    {
        string pkz = _temp.File("data.pkz");
        PkzFixture.WriteStored(pkz, ("pl/pl0000.dat", PkzFixture.Text("original")));
        // An empty source folder: the matching file simply is not there.
        string sourceRoot = Path.Combine(_temp.Path, "emptysrc");
        Directory.CreateDirectory(sourceRoot);

        int code = Runner().Run(new[] { "-r", "--entry", "pl/pl0000.dat", "--out", sourceRoot, pkz });

        Assert.NotEqual(0, code);
        Assert.Contains("no such file", ErrText);
    }

    [Fact]
    public void Add_TakesAFolderAndPreservesItsShape()
    {
        string pkz = _temp.File("data.pkz");
        PkzFixture.WriteStored(pkz, ("existing.dat", PkzFixture.Text("e")));
        _temp.WriteText("mod/pl/pl0000.dat", "added");
        _temp.WriteText("mod/root.dat", "rooted");

        int code = Runner().Run(new[] { "-a", "--out", _temp.File("mod"), pkz });

        Assert.Equal(0, code);
        using var archive = PkzArchive.Open(pkz);
        // The source folder's own name contributes nothing to the virtual path.
        Assert.Equal("added",
            System.Text.Encoding.UTF8.GetString(archive.Extract(archive.Find("pl/pl0000.dat")!)));
        Assert.Equal("rooted",
            System.Text.Encoding.UTF8.GetString(archive.Extract(archive.Find("root.dat")!)));
    }

    [Fact]
    public void Add_TakesASingleFileAtTheRoot()
    {
        string pkz = _temp.File("data.pkz");
        PkzFixture.WriteStored(pkz, ("existing.dat", PkzFixture.Text("e")));
        string file = _temp.WriteText("loose.dat", "loose payload");

        int code = Runner().Run(new[] { "-a", "--out", file, pkz });

        Assert.Equal(0, code);
        using var archive = PkzArchive.Open(pkz);
        Assert.Equal("loose payload",
            System.Text.Encoding.UTF8.GetString(archive.Extract(archive.Find("loose.dat")!)));
    }

    [Fact]
    public void CreateFromFolder_BuildsANewArchive()
    {
        string destination = _temp.File("new.pkz");
        _temp.WriteText("romfs/pl/pl0000.dat", "packed payload");
        _temp.WriteText("romfs/top.dat", "top");

        int code = Runner().Run(new[] { "-f", "--out", _temp.File("romfs"), destination });

        Assert.Equal(0, code);
        using var archive = PkzArchive.Open(destination);
        Assert.Equal(2, archive.Entries.Count);
        Assert.Equal("packed payload",
            System.Text.Encoding.UTF8.GetString(archive.Extract(archive.Find("pl/pl0000.dat")!)));
    }

    [Fact]
    public void CreateFromFolder_AppliesTheRuleWhenCompressionIsChosen()
    {
        string destination = _temp.File("new.pkz");
        var compressible = PkzFixture.Compressible(16384);
        var random = PkzFixture.Incompressible(4096, seed: 41);
        File.WriteAllBytes(_temp.File("romfs/good.dat"), compressible);
        File.WriteAllBytes(_temp.File("romfs/bad.dat"), random);

        int code = Runner().Run(new[] { "-f", "--out", _temp.File("romfs"),
            "--compression", "zstandard", destination });

        Assert.Equal(0, code);
        using var archive = PkzArchive.Open(destination);
        Assert.Equal(EntryCompression.ZStandard, archive.Find("good.dat")!.Compression);
        Assert.Equal(EntryCompression.None, archive.Find("bad.dat")!.Compression);
    }

    [Fact]
    public void DryRun_ChangesNothingOnDisk()
    {
        string pkz = _temp.File("data.pkz");
        PkzFixture.WriteStored(pkz, ("keep.dat", PkzFixture.Text("k")), ("drop.dat", PkzFixture.Text("d")));
        byte[] before = File.ReadAllBytes(pkz);

        int code = Runner().Run(new[] { "-d", "--entry", "drop.dat", "--dry-run", pkz });

        Assert.Equal(0, code);
        Assert.Contains("dry-run", OutText);
        Assert.Equal(before, File.ReadAllBytes(pkz));
    }

    [Fact]
    public void Run_FailsOnAMissingArchiveWithExitCodeTwo()
    {
        int code = Runner().Run(new[] { "-l", _temp.File("does-not-exist.pkz") });

        Assert.Equal(2, code);
        Assert.Contains("no such file", ErrText);
    }

    [Fact]
    public void Run_FailsWhenNoArchiveCanBeOpened()
    {
        string bad = _temp.WriteText("bad.pkz", "not an archive at all");

        int code = Runner().Run(new[] { "-l", bad });

        Assert.NotEqual(0, code);
        // The reason is reported per file, which is what makes a failed run diagnosable.
        Assert.Contains("not a pkz archive", ErrText);
    }

    [Fact]
    public void Run_ExitsTwoAndPrintsUsageOnAUsageError()
    {
        int code = Runner().Run(new[] { "--list", "--extract", "a.pkz" });

        Assert.Equal(2, code);
        Assert.Contains("usage", ErrText);
    }

    [Fact]
    public void Add_WithSeveralArchivesTargetsTheNamedOne()
    {
        string first = _temp.File("data000.pkz");
        string second = _temp.File("data001.pkz");
        PkzFixture.WriteStored(first, ("a.dat", PkzFixture.Text("a")));
        PkzFixture.WriteStored(second, ("b.dat", PkzFixture.Text("b")));
        string file = _temp.WriteText("added.dat", "x");

        int code = Runner().Run(new[] { "-a", "--out", file, "--pkz", "data001.pkz", first, second });

        Assert.Equal(0, code);
        using var firstArchive = PkzArchive.Open(first);
        using var secondArchive = PkzArchive.Open(second);
        Assert.Null(firstArchive.Find("added.dat"));
        Assert.NotNull(secondArchive.Find("added.dat"));
    }

    [Fact]
    public void Add_WithAnUnknownPkzNameFailsClearly()
    {
        string pkz = _temp.File("data.pkz");
        PkzFixture.WriteStored(pkz, ("a.dat", PkzFixture.Text("a")));
        string file = _temp.WriteText("added.dat", "x");

        int code = Runner().Run(new[] { "-a", "--out", file, "--pkz", "nope.pkz", pkz });

        Assert.NotEqual(0, code);
        Assert.Contains("no loaded pkz matches", ErrText);
    }

    /// <summary>The qualified name a selector uses is the one --list prints.</summary>
    [Fact]
    public void Delete_CanTargetOneShadowedCopyByItsQualifiedName()
    {
        string first = _temp.File("data000.pkz");
        string second = _temp.File("data001.pkz");
        PkzFixture.WriteStored(first, ("dup.dat", PkzFixture.Text("a")));
        PkzFixture.WriteStored(second, ("dup.dat", PkzFixture.Text("b")));

        int code = Runner().Run(new[] { "-d", "--entry", "dup.dat - data001.pkz", first, second });

        Assert.Equal(0, code);
        using var firstArchive = PkzArchive.Open(first);
        using var secondArchive = PkzArchive.Open(second);
        Assert.NotNull(firstArchive.Find("dup.dat"));
        Assert.Null(secondArchive.Find("dup.dat"));
    }
}
