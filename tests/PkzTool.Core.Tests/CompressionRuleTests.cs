using PkzTool.Core.Archives;
using PkzTool.Core.Compression;
using Xunit;

namespace PkzTool.Core.Tests;

/// <summary>
/// The compression rule: a file is stored uncompressed when the compressed form is 90% or more of the
/// original, and <c>--force-compression</c> skips that test.
/// </summary>
public class CompressionRuleTests
{
    [Fact]
    public void Pack_KeepsCompressionWhenItShrinksWellBelowTheThreshold()
    {
        var data = PkzFixture.Compressible(16384);

        var (packed, kind) = CompressionCodec.Pack(data, EntryCompression.ZStandard,
            CompressionCodec.DefaultZstdLevel, oodleDllPath: null);

        Assert.Equal(EntryCompression.ZStandard, kind);
        Assert.True(packed.Length < data.Length * CompressionCodec.CompressionWorthwhileThreshold);
        // And it really is readable back.
        Assert.Equal(data, CompressionCodec.ZstdDecompress(packed, data.Length));
    }

    [Fact]
    public void Pack_StoresUncompressedWhenCompressionDoesNotReach90Percent()
    {
        // Random bytes do not compress at all, so the compressed form is larger than the original and
        // the rule must override the requested type.
        var data = PkzFixture.Incompressible(4096, seed: 11);

        var (packed, kind) = CompressionCodec.Pack(data, EntryCompression.ZStandard,
            CompressionCodec.DefaultZstdLevel, oodleDllPath: null);

        Assert.Equal(EntryCompression.None, kind);
        Assert.Same(data, packed);
    }

    /// <summary>
    /// The boundary itself: a compression is kept only strictly below 90%. The payloads are searched
    /// for at runtime rather than hard-coded, so the test asserts the rule's comparison instead of a
    /// hand-computed zstd size that would drift with the codec version.
    /// </summary>
    [Fact]
    public void Pack_KeepsCompressionOnlyStrictlyBelowNinetyPercent()
    {
        var below = FindPayload(ratio => ratio < CompressionCodec.CompressionWorthwhileThreshold);
        var atOrAbove = FindPayload(ratio => ratio >= CompressionCodec.CompressionWorthwhileThreshold);

        Assert.True(below.Found, "expected to find a payload that compresses below the threshold");
        Assert.True(atOrAbove.Found, "expected to find a payload that compresses at or above the threshold");

        var (keptBytes, keptKind) = CompressionCodec.Pack(below.Data, EntryCompression.ZStandard, 3, null);
        Assert.Equal(EntryCompression.ZStandard, keptKind);
        Assert.True(keptBytes.Length < below.Data.Length * CompressionCodec.CompressionWorthwhileThreshold);

        var (storedBytes, storedKind) = CompressionCodec.Pack(atOrAbove.Data, EntryCompression.ZStandard, 3, null);
        Assert.Equal(EntryCompression.None, storedKind);
        Assert.Same(atOrAbove.Data, storedBytes);

        // And the forced form keeps the type even on the payload that fails the rule.
        var (forcedBytes, forcedKind) = CompressionCodec.Pack(atOrAbove.Data, EntryCompression.ZStandard, 3,
            null, forceCompression: true);
        Assert.Equal(EntryCompression.ZStandard, forcedKind);
        Assert.Equal(atOrAbove.Data, CompressionCodec.ZstdDecompress(forcedBytes, atOrAbove.Data.Length));
    }

    /// <summary>
    /// Search lengths for a payload whose zstd ratio falls in the requested band. Run lengths near the
    /// zstd frame overhead sit at roughly 100%, while longer runs compress well, so scanning lengths
    /// reliably finds both sides of the threshold.
    /// </summary>
    private static (byte[] Data, bool Found) FindPayload(Func<double, bool> accept)
    {
        for (int length = 16; length <= 8192; length += 8)
        {
            var data = PkzFixture.Text(new string('q', length));
            double ratio = CompressionCodec.ZstdCompress(data, 3).Length / (double)data.Length;
            if (accept(ratio)) return (data, true);
        }

        return (Array.Empty<byte>(), false);
    }

    [Fact]
    public void Pack_ForceCompressionKeepsTheRequestedTypeEvenWhenItIsBigger()
    {
        var data = PkzFixture.Incompressible(4096, seed: 5);

        var (packed, kind) = CompressionCodec.Pack(data, EntryCompression.ZStandard,
            CompressionCodec.DefaultZstdLevel, oodleDllPath: null, forceCompression: true);

        Assert.Equal(EntryCompression.ZStandard, kind);
        Assert.True(packed.Length > 0);
        // A forced compression still has to round-trip, however badly it compresses.
        Assert.Equal(data, CompressionCodec.ZstdDecompress(packed, data.Length));
    }

    [Fact]
    public void Pack_StoresAnEmptyFileUncompressed()
    {
        var (packed, kind) = CompressionCodec.Pack(Array.Empty<byte>(), EntryCompression.ZStandard, 3, null);

        Assert.Equal(EntryCompression.None, kind);
        Assert.Empty(packed);
    }

    [Fact]
    public void Pack_StoresUncompressedWhenNoneWasRequested()
    {
        var data = PkzFixture.Compressible(4096);
        var (packed, kind) = CompressionCodec.Pack(data, EntryCompression.None, 0, null);

        Assert.Equal(EntryCompression.None, kind);
        Assert.Same(data, packed);
    }

    /// <summary>The rule is applied per file, so one incompressible member cannot change another's.</summary>
    [Fact]
    public void Build_AppliesTheRulePerEntry()
    {
        using var temp = new TempDirectory();
        string path = temp.File("mixed.pkz");

        byte[] good = PkzFixture.Compressible(16384);
        byte[] bad = PkzFixture.Incompressible(2048, seed: 7);

        PkzFixture.Write(path,
            ("good.dat", good, EntryCompression.ZStandard),
            ("bad.dat", bad, EntryCompression.ZStandard));

        using var archive = PkzArchive.Open(temp.File("mixed.pkz"));

        var goodEntry = archive.Find("good.dat")!;
        var badEntry = archive.Find("bad.dat")!;

        Assert.Equal(EntryCompression.ZStandard, goodEntry.Compression);
        Assert.Equal(EntryCompression.None, badEntry.Compression);
        // The stored size equals the uncompressed size exactly when the entry was stored as-is.
        Assert.Equal(badEntry.DecompressedSize, badEntry.CompressedSize);
        Assert.Equal(good, archive.Extract(goodEntry));
        Assert.Equal(bad, archive.Extract(badEntry));
    }

    [Theory]
    [InlineData(EntryCompression.ZStandard, 0, 1)]
    [InlineData(EntryCompression.ZStandard, 99, CompressionCodec.MaxZstdLevel)]
    [InlineData(EntryCompression.OodleKraken, 0, CompressionCodec.MinOodleLevel)]
    [InlineData(EntryCompression.OodleKraken, 99, CompressionCodec.MaxOodleLevel)]
    public void ClampLevel_KeepsTheLevelInsideTheCodecsRange(EntryCompression kind, int requested, int expected) =>
        Assert.Equal(expected, CompressionCodec.ClampLevel(kind, requested));

    [Fact]
    public void OodlePack_WithoutADllPath_FailsWithAnActionableMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CompressionCodec.Pack(PkzFixture.Compressible(1024), EntryCompression.OodleKraken,
                Oodle.DefaultKrakenLevel, oodleDllPath: null));
        Assert.Contains("Oodle", ex.Message);
    }
}
