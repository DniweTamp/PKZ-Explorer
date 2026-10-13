using System.Buffers.Binary;
using PkzTool.Core.Archives;
using PkzTool.Core.Compression;
using Xunit;

namespace PkzTool.Core.Tests;

/// <summary>
/// The container format itself: header layout, the entry table, the string area, and that a write
/// followed by a read gives back exactly what went in.
/// </summary>
public class PkzFormatTests
{
    [Fact]
    public void Build_WritesTheHeaderTheGameExpects()
    {
        using var temp = new TempDirectory();
        string path = temp.File("header.pkz");
        PkzFixture.WriteStored(path,
            ("aa_first.dat", PkzFixture.Text("first")),
            ("zz_last.dat", PkzFixture.Text("second")));

        byte[] bytes = File.ReadAllBytes(path);

        Assert.Equal(0x6C7A6B70u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));           // "pkzl"
        Assert.Equal(0x00020000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x04)));
        Assert.Equal((ulong)bytes.Length, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x08)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x10)));
        Assert.Equal(0x20u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x14)));
        Assert.Equal(0x1000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x1C)));

        // The file ends 64-byte aligned, and so does the start of the data area.
        Assert.Equal(0, bytes.Length % 64);
        long firstData = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x20 + 0x10));
        Assert.Equal(0, firstData % 64);
    }

    /// <summary>
    /// The writer preserves the caller's entry order. This is what makes the list view's default
    /// "PKZ order" sort meaningful and guarantees a save does not reshuffle an archive.
    /// </summary>
    [Fact]
    public void Build_PreservesEntryOrderRatherThanSorting()
    {
        using var temp = new TempDirectory();
        string path = temp.File("order.pkz");

        // Deliberately not alphabetical: zz before aa.
        PkzFixture.WriteStored(path,
            ("zz_last.dat", PkzFixture.Text("z")),
            ("aa_first.dat", PkzFixture.Text("a")),
            ("mm_mid.dat", PkzFixture.Text("m")));

        using var archive = PkzArchive.Open(path);
        Assert.Equal(new[] { "zz_last.dat", "aa_first.dat", "mm_mid.dat" },
            archive.Entries.Select(e => e.Name));
    }

    [Theory]
    [InlineData(EntryCompression.None)]
    [InlineData(EntryCompression.ZStandard)]
    public void RoundTrip_ReturnsTheOriginalBytes(EntryCompression compression)
    {
        using var temp = new TempDirectory();
        string path = temp.File($"round_{compression}.pkz");

        // The compressible payload gets the requested kind; the random one will be stored as None by
        // the worthwhile rule, which is asserted separately.
        var compressible = PkzFixture.Compressible(8192);
        var random = PkzFixture.Incompressible(512, seed: 3);
        PkzFixture.Write(path,
            ("pl/pl0000.dat", compressible, compression),
            ("random.bin", random, compression),
            ("nested/deep/dir/file.wta", PkzFixture.Text("nested payload"), compression));

        using var archive = PkzArchive.Open(path);
        Assert.Equal(3, archive.Entries.Count);

        Assert.Equal(compressible, archive.Extract(archive.Find("pl/pl0000.dat")!));
        Assert.Equal(random, archive.Extract(archive.Find("random.bin")!));
        Assert.Equal(PkzFixture.Text("nested payload"),
            archive.Extract(archive.Find("nested/deep/dir/file.wta")!));
    }

    [Fact]
    public void RoundTrip_PreservesInternalForwardSlashes()
    {
        using var temp = new TempDirectory();
        string path = temp.File("paths.pkz");
        PkzFixture.WriteStored(path, ("effect/texture/001.wta", PkzFixture.Text("x")));

        using var archive = PkzArchive.Open(path);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("effect/texture/001.wta", entry.Name);
        Assert.DoesNotContain('\\', entry.Name);
    }

    [Fact]
    public void Find_IsCaseInsensitiveLikeTheEngine()
    {
        using var temp = new TempDirectory();
        string path = temp.File("case.pkz");
        PkzFixture.WriteStored(path, ("Pl/Pl0000.DAT", PkzFixture.Text("payload")));

        using var archive = PkzArchive.Open(path);
        Assert.NotNull(archive.Find("pl/pl0000.dat"));
        Assert.NotNull(archive.Find("PL/PL0000.DAT"));
        // The stored spelling is preserved for writing back.
        Assert.Equal("Pl/Pl0000.DAT", archive.Entries[0].Name);
    }

    [Fact]
    public void Open_RejectsAFileThatIsNotAPkz()
    {
        using var temp = new TempDirectory();
        string path = temp.WriteText("not.pkz", "this is plainly not an archive at all");

        var ex = Assert.Throws<InvalidDataException>(() => PkzArchive.Open(path));
        Assert.Contains("pkzl", ex.Message);
    }

    [Fact]
    public void Open_RejectsATruncatedArchiveWithAClearMessage()
    {
        using var temp = new TempDirectory();
        string path = temp.File("truncated.pkz");
        PkzFixture.WriteStored(path, ("pl/pl0000.dat", PkzFixture.Compressible(4096)));

        // Cut the file inside the data area so the entry range no longer fits.
        byte[] full = File.ReadAllBytes(path);
        File.WriteAllBytes(path, full[..(full.Length - 2048)]);

        var ex = Assert.Throws<InvalidDataException>(() => PkzArchive.Open(path));
        Assert.Contains("outside the file", ex.Message);
    }

    [Fact]
    public void HasMagic_DistinguishesAnArchiveFromAnythingElse()
    {
        using var temp = new TempDirectory();
        string pkz = temp.File("real.pkz");
        string other = temp.WriteText("other.bin", "nope");

        PkzFixture.WriteStored(pkz, ("a.dat", PkzFixture.Text("a")));
        Assert.True(PkzArchive.HasMagic(pkz));
        Assert.False(PkzArchive.HasMagic(other));
        Assert.False(PkzArchive.HasMagic(temp.File("missing.pkz")));
    }

    /// <summary>The compression label written to the archive matches the stored payload.</summary>
    [Theory]
    [InlineData(EntryCompression.None, "None")]
    [InlineData(EntryCompression.ZStandard, "ZStandard")]
    [InlineData(EntryCompression.OodleKraken, "OodleKraken")]
    public void CompressionName_MatchesTheSpecStrings(EntryCompression kind, string expected) =>
        Assert.Equal(expected, CompressionCodec.CompressionName(kind));

    [Theory]
    [InlineData("None", EntryCompression.None)]
    [InlineData("none", EntryCompression.None)]
    [InlineData("ZStandard", EntryCompression.ZStandard)]
    [InlineData("zstd", EntryCompression.ZStandard)]
    [InlineData("OodleKraken", EntryCompression.OodleKraken)]
    [InlineData("kraken", EntryCompression.OodleKraken)]
    public void ParseCompression_AcceptsTheSpellingsInTheWild(string text, EntryCompression expected) =>
        Assert.Equal(expected, CompressionCodec.ParseCompression(text));

    [Fact]
    public void ParseCompression_RejectsAnUnknownKind() =>
        Assert.Throws<InvalidDataException>(() => CompressionCodec.ParseCompression("LZ4"));

    /// <summary>A written archive is readable by the reader and vice versa under a byte-level check.</summary>
    [Fact]
    public void WriteFile_ReplacesTheDestinationWithoutLeavingATempFile()
    {
        using var temp = new TempDirectory();
        string path = temp.WriteText("target.pkz", "stale content that must go away");

        PkzFixture.WriteStored(path, ("a.dat", PkzFixture.Text("fresh")));

        using var archive = PkzArchive.Open(path);
        Assert.Equal("a.dat", Assert.Single(archive.Entries).Name);

        // The temp-and-move strategy must not leave its scratch file behind.
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }
}
