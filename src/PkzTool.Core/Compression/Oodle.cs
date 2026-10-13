using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PkzTool.Core.Compression;

/// <summary>
/// P/Invoke into the user-supplied Oodle dll (Settings → Oodle dll). The dll is never bundled: it is
/// proprietary and the user supplies the build matching their game.
/// <para>
/// The single export needed for reading is <c>OodleLZ_Decompress</c>; writing additionally needs
/// <c>OodleLZ_Compress</c>, which a decompress-only dll may not export. The signature below matches
/// the Oodle 2.9 SDK ("ootl" / <c>OO_SDK_H</c>):
/// </para>
/// <code>
/// OodleLZ_Decompress(const void *compBuf, SQWORD compBufSize, void *rawBuf, SQWORD rawLen,
///                    OodleLZ_FuzzSafe fuzzSafe, OodleLZ_CheckCRC checkCRC, OodleLZ_Verbosity verbosity,
///                    void *decBufBase, SQWORD decBufSize,
///                    OodleLZ_DecompressCallback fpCallback, void *callbackUserData,
///                    void *decoderMemory, SQWORD decoderMemorySize, OodleLZ_ThreadPhase threadPhase);
/// </code>
/// </summary>
public static class Oodle
{
    private delegate long OodleLZ_DecompressFn(
        byte[] compBuf, long compBufSize, byte[] rawBuf, long rawLen,
        int fuzzSafe, int checkCRC, int verbosity,
        IntPtr decBufBase, long decBufSize,
        IntPtr fpCallback, IntPtr callbackUserData,
        IntPtr decoderMemory, long decoderMemorySize, int threadPhase);

    // OodleLZ_Compress(OodleLZ_Compressor compressor, const void *rawBuf, SINTa rawLen, void *compBuf,
    //                  OodleLZ_CompressionLevel level, const OodleLZ_CompressOptions *pOptions,
    //                  const void *dictionaryBase, const void *lrm, void *scratchMem, SINTa scratchSize);
    private delegate long OodleLZ_CompressFn(
        int compressor, byte[] rawBuf, long rawLen, byte[] compBuf,
        int level, byte[] options, IntPtr dictionaryBase, IntPtr lrm, IntPtr scratchMem, long scratchSize);

    /// <summary>Oodle compressor id for Kraken, the codec Bayonetta 3 uses.</summary>
    public const int KrakenCompressor = 8;

    /// <summary>Oodle compression levels (<c>OodleLZ_CompressionLevel</c>).</summary>
    public const int LevelNone = 0, LevelSuperFast = 1, LevelVeryFast = 2, LevelFast = 3,
        LevelNormal = 4, LevelOptimal1 = 5, LevelOptimal2 = 6, LevelOptimal3 = 7,
        LevelOptimal4 = 8, LevelOptimal5 = 9;

    /// <summary>Default level, matching PKZe and the mod manager.</summary>
    public const int DefaultKrakenLevel = LevelNormal;

    /// <summary>The file name the app looks for when auto-detecting a dll.</summary>
    public const string DllPattern = "oo2core*.dll";

    private static readonly object Gate = new();
    private static IntPtr _handle;
    private static OodleLZ_DecompressFn? _decompress;
    private static OodleLZ_CompressFn? _compress;
    private static string? _loadedPath;

    /// <summary>Path of the dll currently loaded, or null.</summary>
    public static string? LoadedPath
    {
        get { lock (Gate) return _loadedPath; }
    }

    /// <summary>
    /// Load the Oodle dll and resolve its exports. Throws with a clear message when the file is
    /// missing, fails to load, or does not export <c>OodleLZ_Decompress</c>.
    /// </summary>
    public static void EnsureLoaded(string dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
            throw new InvalidOperationException("no Oodle dll path is configured (Settings -> Oodle dll)");
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"Oodle dll not found: {dllPath}", dllPath);

        lock (Gate)
        {
            if (_decompress is not null &&
                string.Equals(_loadedPath, Path.GetFullPath(dllPath), StringComparison.OrdinalIgnoreCase))
                return;

            if (_handle != IntPtr.Zero)
            {
                NativeLibrary.Free(_handle);
                _handle = IntPtr.Zero;
                _decompress = null;
                _compress = null;
                _loadedPath = null;
            }

            IntPtr handle;
            try
            {
                handle = NativeLibrary.Load(dllPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to load the Oodle dll '{dllPath}'. Check the path and that it is the " +
                    "64-bit oo2core_*.dll matching this build.", ex);
            }

            if (!NativeLibrary.TryGetExport(handle, "OodleLZ_Decompress", out var symbol))
            {
                NativeLibrary.Free(handle);
                throw new InvalidOperationException(
                    $"The dll '{dllPath}' does not export OodleLZ_Decompress; it is not a usable Oodle dll.");
            }

            _handle = handle;
            _decompress = Marshal.GetDelegateForFunctionPointer<OodleLZ_DecompressFn>(symbol);
            // Compression is optional: a dll that only decompresses still reads archives fine.
            _compress = NativeLibrary.TryGetExport(handle, "OodleLZ_Compress", out var compressSymbol)
                ? Marshal.GetDelegateForFunctionPointer<OodleLZ_CompressFn>(compressSymbol)
                : null;
            _loadedPath = Path.GetFullPath(dllPath);
        }
    }

    /// <summary>True when the loaded dll exports <c>OodleLZ_Compress</c>.</summary>
    public static bool CanCompress
    {
        get { lock (Gate) return _compress is not null; }
    }

    /// <summary>True when the dll at <paramref name="dllPath"/> loads and can decompress.</summary>
    public static bool TryValidate(string? dllPath, out string? error)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
        {
            error = "no path is set";
            return false;
        }

        try
        {
            EnsureLoaded(dllPath);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Decompress a Kraken/Leviathan/Mermaid block into <paramref name="rawLength"/> bytes.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> compressed, int rawLength, string dllPath)
    {
        EnsureLoaded(dllPath);
        if (rawLength < 0) throw new ArgumentOutOfRangeException(nameof(rawLength));
        if (rawLength == 0) return Array.Empty<byte>();

        var comp = compressed.ToArray();
        var raw = new byte[rawLength];
        long written;
        lock (Gate)
        {
            written = _decompress!(comp, comp.Length, raw, rawLength,
                fuzzSafe: 1, checkCRC: 0, verbosity: 0,
                IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 0);
        }

        if (written < 0)
            throw new InvalidDataException($"Oodle decompression failed (code {written}).");
        if (written != rawLength)
        {
            // Some builds report the exact size; trust it rather than the requested buffer.
            if (written > raw.Length)
                throw new InvalidDataException($"Oodle wrote {written} bytes into a {rawLength}-byte buffer.");
            Array.Resize(ref raw, (int)written);
        }
        return raw;
    }

    /// <summary>
    /// Oodle-compress <paramref name="data"/> with Kraken at <paramref name="level"/>. Mirrors PKZe
    /// and <c>pkz_tool.py</c>: a 100-byte options blob (seekChunkReset 1, seekChunkLen 0x40000) and a
    /// destination buffer of <c>len + 65536</c>. Returns the compressed bytes — an incompressible
    /// block can come back larger than the input, which the caller handles.
    /// </summary>
    public static byte[] Compress(byte[] data, int level, string dllPath)
    {
        if (data.Length == 0) return data;
        EnsureLoaded(dllPath);

        var options = new byte[100];
        BinaryPrimitives.WriteInt32LittleEndian(options.AsSpan(0), 0);
        BinaryPrimitives.WriteInt32LittleEndian(options.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(options.AsSpan(8), 1);        // seekChunkReset
        BinaryPrimitives.WriteInt32LittleEndian(options.AsSpan(12), 0x40000); // seekChunkLen

        var destination = new byte[data.Length + 65536];
        long written;
        lock (Gate)
        {
            if (_compress is null)
                throw new InvalidOperationException(
                    $"the Oodle dll '{_loadedPath}' does not export OodleLZ_Compress, so it cannot compress");
            written = _compress(KrakenCompressor, data, data.Length, destination, level, options,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0);
        }

        if (written <= 0)
            throw new InvalidDataException($"Oodle compression failed (code {written}).");
        if (written > destination.Length)
            throw new InvalidDataException($"Oodle returned an impossible size {written}.");
        if (written == destination.Length) return destination;

        var result = new byte[written];
        Array.Copy(destination, result, written);
        return result;
    }
}
