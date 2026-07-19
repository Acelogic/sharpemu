// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpEmu.Libs.Bink;

// Attribution: the original host-side Bink2 bridge was authored by @xnetcat:
// https://github.com/xnetcat/sharpemu/commit/23cefcc69b32980724bfa9fb015f32fa518a02a9

/// <summary>
/// Optional host-side Bink 2 bridge for games that ship a static Bink player.
///
/// The game in that case never imports libSceVideodec, so an HLE video-decoder
/// export cannot see its movie frames. Kernel file opens identify the active
/// .bk2 file and the presenter requests BGRA frames from a tiny native adapter.
/// The adapter is deliberately a separate, user-supplied library: Bink 2 is a
/// proprietary SDK and SharpEmu must neither bundle it nor depend on its ABI.
/// </summary>
internal static class Bink2MovieBridge
{
    private const int BinkHeaderSize = 0x24;
    private const uint MaxDimension = 16384;
    private const uint MaxFramesPerSecond = 1000;
    private static readonly object Gate = new();
    private static readonly HashSet<string> ObservedMovieRanges = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static NativeAdapter? _adapter;
    private static string? _activePath;
    private static long _activeOffset;
    private static long _activeLength;
    private static bool _activeIsRange;
    private static IntPtr _activeMovie;
    private static Bink2MovieInfo _activeInfo;
    private static byte[]? _frameBuffer;
    private static bool _usingDummyMovie;
    private static bool _loadAttempted;
    private static bool _availabilityReported;
    private static bool _rangeAdapterWarningReported;
    private static BinkMovieRangeResult? _lastRangeResult;

    /// <summary>
    /// Returns true only when movie skipping was explicitly requested. Without
    /// a host adapter the guest must be allowed to run the Bink implementation
    /// statically linked into its executable.
    /// </summary>
    internal static bool ShouldSkipGuestMovie(string hostPath) =>
        hostPath.EndsWith(".bk2", StringComparison.OrdinalIgnoreCase) &&
        ResolveMode() == BinkMovieMode.Skip;

    internal static void ObserveGuestMovie(string hostPath)
    {
        if (!hostPath.EndsWith(".bk2", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(hostPath))
        {
            return;
        }

        lock (Gate)
        {
            if (!_activeIsRange &&
                string.Equals(_activePath, hostPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var mode = ResolveMode();
            if (mode == BinkMovieMode.Dummy)
            {
                AttachDummyMovieLocked(hostPath);
                return;
            }

            if (mode != BinkMovieMode.Native)
            {
                return;
            }

            var adapter = GetAdapterLocked();
            if (adapter is null)
            {
                return;
            }

            CloseActiveLocked();
            if (!adapter.TryOpen(hostPath, out var movie, out var info))
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] Bink2 bridge could not open movie '" +
                    Path.GetFileName(hostPath) + "'.");
                return;
            }

            if (!IsValid(info))
            {
                adapter.Close(movie);
                Console.Error.WriteLine(
                    "[LOADER][WARN] Bink2 bridge rejected invalid movie dimensions for '" +
                    Path.GetFileName(hostPath) + "'.");
                return;
            }

            _activePath = hostPath;
            _activeOffset = 0;
            _activeLength = 0;
            _activeIsRange = false;
            _activeMovie = movie;
            _activeInfo = info;
            _frameBuffer = GC.AllocateUninitializedArray<byte>(GetFrameBufferLength(info));
            Console.Error.WriteLine(
                "[LOADER][INFO] Bink2 bridge attached: " + Path.GetFileName(hostPath) + " " +
                info.Width + "x" + info.Height + " @ " +
                info.FramesPerSecondNumerator + "/" + info.FramesPerSecondDenominator + " fps.");
        }
    }

    /// <summary>
    /// Observes a positional read that begins with an embedded Bink movie. The
    /// returned result deliberately separates detection from policy: callers can
    /// inspect <see cref="BinkMovieRangeResult.Mode"/>, but pread itself must not
    /// turn a validated movie into EOF or an error without caller-contract evidence.
    /// </summary>
    internal static BinkMovieRangeResult? ObserveGuestMovieRange(
        string hostPath,
        long hostFileLength,
        int fileDescriptor,
        long fileOffset,
        int requestedLength,
        int readLength,
        ulong guestDestination,
        ulong guestRip,
        ReadOnlySpan<byte> bytes)
    {
        if (string.IsNullOrWhiteSpace(hostPath) ||
            requestedLength < 0 ||
            readLength < 0 ||
            readLength > requestedLength ||
            readLength > bytes.Length ||
            !TryParseMovieRangeHeader(
                bytes[..readLength],
                fileOffset,
                hostFileLength,
                out var header))
        {
            return null;
        }

        lock (Gate)
        {
            var mode = ResolveMode();
            var attachment = BinkMovieRangeAttachment.None;

            if (IsActiveRangeLocked(hostPath, fileOffset, header.ByteLength))
            {
                attachment = _usingDummyMovie
                    ? BinkMovieRangeAttachment.Dummy
                    : _activeMovie != IntPtr.Zero
                        ? BinkMovieRangeAttachment.Native
                        : BinkMovieRangeAttachment.None;
            }
            else if (mode == BinkMovieMode.Dummy)
            {
                AttachDummyMovieLocked(hostPath, fileOffset, header);
                attachment = _usingDummyMovie && IsActiveRangeLocked(hostPath, fileOffset, header.ByteLength)
                    ? BinkMovieRangeAttachment.Dummy
                    : BinkMovieRangeAttachment.None;
            }
            else if (mode == BinkMovieMode.Native)
            {
                attachment = TryAttachNativeMovieRangeLocked(hostPath, fileOffset, header);
            }

            var result = new BinkMovieRangeResult(
                hostPath,
                fileDescriptor,
                fileOffset,
                requestedLength,
                readLength,
                guestDestination,
                guestRip,
                Environment.CurrentManagedThreadId,
                Thread.CurrentThread.Name,
                header,
                mode,
                attachment);
            RecordMovieRangeLocked(result);
            return result;
        }
    }

    internal static bool TryDecodeNextFrame(
        out byte[] pixels,
        out uint width,
        out uint height)
    {
        lock (Gate)
        {
            pixels = [];
            width = 0;
            height = 0;
            if (_adapter is null || _activeMovie == IntPtr.Zero || _frameBuffer is null)
            {
                if (_usingDummyMovie && _frameBuffer is not null)
                {
                    pixels = _frameBuffer;
                    width = _activeInfo.Width;
                    height = _activeInfo.Height;
                    return true;
                }

                return false;
            }

            unsafe
            {
                fixed (byte* destination = _frameBuffer)
                {
                    if (!_adapter.DecodeNextBgra(
                            _activeMovie,
                            (IntPtr)destination,
                            _activeInfo.Width * 4,
                            (uint)_frameBuffer.Length))
                    {
                        return false;
                    }
                }
            }

            pixels = _frameBuffer;
            width = _activeInfo.Width;
            height = _activeInfo.Height;
            return true;
        }
    }

    private static bool IsValid(Bink2MovieInfo info) =>
        info.Width > 0 && info.Height > 0 &&
        info.Width <= MaxDimension && info.Height <= MaxDimension &&
        (ulong)info.Width * info.Height * 4 <= int.MaxValue;

    private static int GetFrameBufferLength(Bink2MovieInfo info) =>
        checked((int)((ulong)info.Width * info.Height * 4));

    private static BinkMovieMode ResolveMode()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_BINK_MODE");
        if (string.Equals(configured, "dummy", StringComparison.OrdinalIgnoreCase))
        {
            return BinkMovieMode.Dummy;
        }

        if (string.Equals(configured, "native", StringComparison.OrdinalIgnoreCase))
        {
            return BinkMovieMode.Native;
        }

        if (string.Equals(configured, "skip", StringComparison.OrdinalIgnoreCase))
        {
            return BinkMovieMode.Skip;
        }

        // Prefer the optional host adapter when one is supplied. Otherwise let
        // the game's statically linked Bink implementation consume the file.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SHARPEMU_BINK2_BRIDGE")) ||
            EnumerateAdapterCandidates().Any(File.Exists))
        {
            return BinkMovieMode.Native;
        }

        return BinkMovieMode.Guest;
    }

    private static void AttachDummyMovieLocked(string hostPath)
    {
        if (!TryReadBinkHeader(hostPath, out var header))
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Bink dummy could not read movie header '" +
                Path.GetFileName(hostPath) + "'.");
            return;
        }

        AttachDummyMovieLocked(hostPath, 0, header, isRange: false);
    }

    private static void AttachDummyMovieLocked(
        string hostPath,
        long fileOffset,
        BinkMovieHeaderInfo header,
        bool isRange = true)
    {
        var info = ToMovieInfo(header);
        CloseActiveLocked();
        _activePath = hostPath;
        _activeOffset = fileOffset;
        _activeLength = header.ByteLength;
        _activeIsRange = isRange;
        _activeInfo = info;
        _frameBuffer = GC.AllocateUninitializedArray<byte>(GetFrameBufferLength(info));
        FillDummyFrame(_frameBuffer, info.Width, info.Height);
        _usingDummyMovie = true;
        Console.Error.WriteLine(
            "[LOADER][INFO] Bink dummy attached: " + Path.GetFileName(hostPath) +
            (isRange ? " offset=" + fileOffset + " length=" + header.ByteLength : string.Empty) +
            " " + info.Width + "x" + info.Height + ".");
    }

    private static bool TryReadBinkHeader(string path, out BinkMovieHeaderInfo header)
    {
        header = default;
        Span<byte> bytes = stackalloc byte[BinkHeaderSize];
        try
        {
            using var stream = File.OpenRead(path);
            stream.ReadExactly(bytes);
            return TryParseMovieRangeHeader(bytes, 0, stream.Length, out header);
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal static bool TryParseMovieRangeHeader(
        ReadOnlySpan<byte> bytes,
        long fileOffset,
        long hostFileLength,
        out BinkMovieHeaderInfo info)
    {
        info = default;
        if (bytes.Length < BinkHeaderSize ||
            fileOffset < 0 ||
            hostFileLength < 0 ||
            !TryGetMovieFamily(bytes[..4], out var family))
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0x04, 4));
        var byteLength = (long)payloadLength + 8;
        if (byteLength < BinkHeaderSize ||
            fileOffset > hostFileLength ||
            byteLength > hostFileLength - fileOffset)
        {
            return false;
        }

        var frameCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0x08, 4));
        var largestFrameSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0x0C, 4));
        var width = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0x14, 4));
        var height = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0x18, 4));
        var framesPerSecondNumerator = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0x1C, 4));
        var framesPerSecondDenominator = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0x20, 4));
        var frameInfo = new Bink2MovieInfo(
            width,
            height,
            framesPerSecondNumerator,
            framesPerSecondDenominator);
        var minimumFrameIndexBytes = ((ulong)frameCount + 1) * sizeof(uint);

        if (frameCount == 0 ||
            largestFrameSize == 0 ||
            largestFrameSize > payloadLength ||
            (ulong)byteLength < BinkHeaderSize + minimumFrameIndexBytes ||
            framesPerSecondNumerator == 0 ||
            framesPerSecondDenominator == 0 ||
            framesPerSecondNumerator > (ulong)framesPerSecondDenominator * MaxFramesPerSecond ||
            !IsValid(frameInfo))
        {
            return false;
        }

        info = new BinkMovieHeaderInfo(
            Encoding.ASCII.GetString(bytes[..4]),
            family,
            byteLength,
            frameCount,
            largestFrameSize,
            width,
            height,
            framesPerSecondNumerator,
            framesPerSecondDenominator);
        return true;
    }

    private static bool TryGetMovieFamily(ReadOnlySpan<byte> signature, out BinkMovieFamily family)
    {
        family = default;
        if (signature.Length < 4)
        {
            return false;
        }

        var version = signature[3];
        if (signature[0] == (byte)'B' &&
            signature[1] == (byte)'I' &&
            signature[2] == (byte)'K' &&
            IsBink1Version(version))
        {
            family = BinkMovieFamily.Bink1;
            return true;
        }

        if (signature[0] == (byte)'K' &&
            signature[1] == (byte)'B' &&
            signature[2] == (byte)'2' &&
            IsBink2Version(version))
        {
            family = BinkMovieFamily.Bink2;
            return true;
        }

        return false;
    }

    private static bool IsBink1Version(byte version) =>
        version is (byte)'f' or (byte)'g' or (byte)'h' or (byte)'i' or (byte)'k';

    private static bool IsBink2Version(byte version) =>
        version is (byte)'f' or (byte)'g' or (byte)'h' or (byte)'i' or (byte)'j' or (byte)'k' or (byte)'m';

    private static Bink2MovieInfo ToMovieInfo(BinkMovieHeaderInfo header) =>
        new(
            header.Width,
            header.Height,
            header.FramesPerSecondNumerator,
            header.FramesPerSecondDenominator);

    private static void FillDummyFrame(byte[] pixels, uint width, uint height)
    {
        for (var y = 0u; y < height; y++)
        {
            for (var x = 0u; x < width; x++)
            {
                var offset = checked((int)(((ulong)y * width + x) * 4));
                var band = ((x / 96) + (y / 96)) & 1;
                pixels[offset] = band == 0 ? (byte)0x28 : (byte)0x18;
                pixels[offset + 1] = band == 0 ? (byte)0x18 : (byte)0x28;
                pixels[offset + 2] = 0x10;
                pixels[offset + 3] = 0xFF;
            }
        }
    }

    private static bool IsActiveRangeLocked(string hostPath, long fileOffset, long byteLength) =>
        _activeIsRange &&
        _activeOffset == fileOffset &&
        _activeLength == byteLength &&
        string.Equals(_activePath, hostPath, StringComparison.OrdinalIgnoreCase);

    private static BinkMovieRangeAttachment TryAttachNativeMovieRangeLocked(
        string hostPath,
        long fileOffset,
        BinkMovieHeaderInfo header)
    {
        var adapter = GetAdapterLocked();
        if (adapter is null)
        {
            return BinkMovieRangeAttachment.None;
        }

        if (!adapter.SupportsRangeOpen)
        {
            if (!_rangeAdapterWarningReported)
            {
                _rangeAdapterWarningReported = true;
                Console.Error.WriteLine(
                    "[LOADER][INFO] Bink2 bridge has no range entry point; embedded movies remain guest-decoded.");
            }

            return BinkMovieRangeAttachment.None;
        }

        CloseActiveLocked();
        if (!adapter.TryOpenRange(hostPath, fileOffset, header.ByteLength, out var movie, out var info))
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Bink2 bridge could not open embedded movie '" +
                Path.GetFileName(hostPath) + "' offset=" + fileOffset +
                " length=" + header.ByteLength + ".");
            return BinkMovieRangeAttachment.None;
        }

        var expected = ToMovieInfo(header);
        if (!IsValid(info) ||
            info.Width != expected.Width ||
            info.Height != expected.Height ||
            info.FramesPerSecondNumerator != expected.FramesPerSecondNumerator ||
            info.FramesPerSecondDenominator != expected.FramesPerSecondDenominator)
        {
            adapter.Close(movie);
            Console.Error.WriteLine(
                "[LOADER][WARN] Bink2 bridge rejected mismatched embedded movie metadata for '" +
                Path.GetFileName(hostPath) + "'.");
            return BinkMovieRangeAttachment.None;
        }

        _activePath = hostPath;
        _activeOffset = fileOffset;
        _activeLength = header.ByteLength;
        _activeIsRange = true;
        _activeMovie = movie;
        _activeInfo = info;
        _frameBuffer = GC.AllocateUninitializedArray<byte>(GetFrameBufferLength(info));
        Console.Error.WriteLine(
            "[LOADER][INFO] Bink2 bridge attached embedded movie: " +
            Path.GetFileName(hostPath) + " offset=" + fileOffset +
            " length=" + header.ByteLength + " " +
            info.Width + "x" + info.Height + ".");
        return BinkMovieRangeAttachment.Native;
    }

    private static void RecordMovieRangeLocked(BinkMovieRangeResult result)
    {
        _lastRangeResult = result;
        var pathKey = result.HostPath + "\0" + result.FileOffset + "\0" + result.Header.ByteLength;
        if (!ObservedMovieRanges.Add(pathKey))
        {
            return;
        }

        Console.Error.WriteLine(
            "[LOADER][INFO] bink.range" +
            " mode=" + result.Mode.ToString().ToLowerInvariant() +
            " attachment=" + result.Attachment.ToString().ToLowerInvariant() +
            " family=" + result.Header.Family.ToString().ToLowerInvariant() +
            " format=" + result.Header.Signature +
            " fd=" + result.FileDescriptor +
            " offset=" + result.FileOffset +
            " length=" + result.Header.ByteLength +
            " requested=" + result.RequestedLength +
            " read=" + result.ReadLength +
            " guest=0x" + result.GuestDestination.ToString("X16") +
            " rip=0x" + result.GuestRip.ToString("X16") +
            " thread=" + result.ManagedThreadId +
            " thread_name='" + (result.ManagedThreadName ?? string.Empty).Replace("'", "''") + "'" +
            " frames=" + result.Header.FrameCount +
            " largest_frame=" + result.Header.LargestFrameSize +
            " width=" + result.Header.Width +
            " height=" + result.Header.Height +
            " fps=" + result.Header.FramesPerSecondNumerator + "/" + result.Header.FramesPerSecondDenominator +
            " path='" + result.HostPath.Replace("'", "''") + "'.");
    }

    /// <summary>
    /// Most recently validated embedded movie range. This is metadata only; it
    /// never owns or persists the range bytes.
    /// </summary>
    internal static BinkMovieRangeResult? LastObservedMovieRange
    {
        get
        {
            lock (Gate)
            {
                return _lastRangeResult;
            }
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            CloseActiveLocked();
            ObservedMovieRanges.Clear();
            _lastRangeResult = null;
            _rangeAdapterWarningReported = false;
        }
    }

    private static NativeAdapter? GetAdapterLocked()
    {
        if (_loadAttempted)
        {
            return _adapter;
        }

        _loadAttempted = true;
        foreach (var candidate in EnumerateAdapterCandidates())
        {
            if (!NativeLibrary.TryLoad(candidate, out var library))
            {
                continue;
            }

            if (NativeAdapter.TryCreate(library, out var adapter))
            {
                _adapter = adapter;
                Console.Error.WriteLine("[LOADER][INFO] Bink2 bridge loaded: " + candidate);
                return adapter;
            }

            NativeLibrary.Free(library);
        }

        if (!_availabilityReported)
        {
            _availabilityReported = true;
            Console.Error.WriteLine(
                "[LOADER][INFO] Bink2 bridge unavailable; install the licensed adapter and set SHARPEMU_BINK2_BRIDGE.");
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAdapterCandidates()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_BINK2_BRIDGE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        var baseDirectory = AppContext.BaseDirectory;
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(baseDirectory, "libsharpemu_bink2_bridge.dylib");
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(baseDirectory, "sharpemu_bink2_bridge.dll");
        }
        else
        {
            yield return Path.Combine(baseDirectory, "libsharpemu_bink2_bridge.so");
        }
    }

    private static void CloseActiveLocked()
    {
        if (_activeMovie != IntPtr.Zero)
        {
            _adapter?.Close(_activeMovie);
        }

        _activePath = null;
        _activeOffset = 0;
        _activeLength = 0;
        _activeIsRange = false;
        _activeMovie = IntPtr.Zero;
        _activeInfo = default;
        _frameBuffer = null;
        _usingDummyMovie = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Bink2MovieInfo
    {
        public readonly uint Width;
        public readonly uint Height;
        public readonly uint FramesPerSecondNumerator;
        public readonly uint FramesPerSecondDenominator;

        internal Bink2MovieInfo(
            uint width,
            uint height,
            uint framesPerSecondNumerator,
            uint framesPerSecondDenominator)
        {
            Width = width;
            Height = height;
            FramesPerSecondNumerator = framesPerSecondNumerator;
            FramesPerSecondDenominator = framesPerSecondDenominator;
        }
    }

    private sealed class NativeAdapter
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int OpenUtf8Delegate(IntPtr pathUtf8, out IntPtr movie, out Bink2MovieInfo info);

        // Optional adapter ABI:
        // sharpemu_bink2_open_range_utf8(path, offset, length, movie, info).
        // The adapter reads directly from the bounded host-file range; SharpEmu
        // does not materialize a temporary standalone movie.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int OpenRangeUtf8Delegate(
            IntPtr pathUtf8,
            ulong fileOffset,
            ulong byteLength,
            out IntPtr movie,
            out Bink2MovieInfo info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DecodeNextBgraDelegate(IntPtr movie, IntPtr destination, uint stride, uint destinationBytes);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CloseDelegate(IntPtr movie);

        private readonly OpenUtf8Delegate _openUtf8;
        private readonly OpenRangeUtf8Delegate? _openRangeUtf8;
        private readonly DecodeNextBgraDelegate _decodeNextBgra;
        private readonly CloseDelegate _close;

        private NativeAdapter(
            OpenUtf8Delegate openUtf8,
            OpenRangeUtf8Delegate? openRangeUtf8,
            DecodeNextBgraDelegate decodeNextBgra,
            CloseDelegate close)
        {
            _openUtf8 = openUtf8;
            _openRangeUtf8 = openRangeUtf8;
            _decodeNextBgra = decodeNextBgra;
            _close = close;
        }

        internal bool SupportsRangeOpen => _openRangeUtf8 is not null;

        internal static bool TryCreate(IntPtr library, out NativeAdapter? adapter)
        {
            adapter = null;
            if (!NativeLibrary.TryGetExport(library, "sharpemu_bink2_open_utf8", out var open) ||
                !NativeLibrary.TryGetExport(library, "sharpemu_bink2_decode_next_bgra", out var decode) ||
                !NativeLibrary.TryGetExport(library, "sharpemu_bink2_close", out var close))
            {
                return false;
            }

            OpenRangeUtf8Delegate? openRangeUtf8 = null;
            if (NativeLibrary.TryGetExport(library, "sharpemu_bink2_open_range_utf8", out var openRange))
            {
                openRangeUtf8 = Marshal.GetDelegateForFunctionPointer<OpenRangeUtf8Delegate>(openRange);
            }

            adapter = new NativeAdapter(
                Marshal.GetDelegateForFunctionPointer<OpenUtf8Delegate>(open),
                openRangeUtf8,
                Marshal.GetDelegateForFunctionPointer<DecodeNextBgraDelegate>(decode),
                Marshal.GetDelegateForFunctionPointer<CloseDelegate>(close));
            return true;
        }

        internal bool TryOpen(string path, out IntPtr movie, out Bink2MovieInfo info)
        {
            var utf8 = Marshal.StringToCoTaskMemUTF8(path);
            try
            {
                return _openUtf8(utf8, out movie, out info) != 0 && movie != IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8);
            }
        }

        internal bool TryOpenRange(
            string path,
            long fileOffset,
            long byteLength,
            out IntPtr movie,
            out Bink2MovieInfo info)
        {
            movie = IntPtr.Zero;
            info = default;
            if (_openRangeUtf8 is null || fileOffset < 0 || byteLength <= 0)
            {
                return false;
            }

            var utf8 = Marshal.StringToCoTaskMemUTF8(path);
            try
            {
                return _openRangeUtf8(
                    utf8,
                    unchecked((ulong)fileOffset),
                    unchecked((ulong)byteLength),
                    out movie,
                    out info) != 0 && movie != IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8);
            }
        }

        internal bool DecodeNextBgra(IntPtr movie, IntPtr destination, uint stride, uint destinationBytes) =>
            _decodeNextBgra(movie, destination, stride, destinationBytes) != 0;

        internal void Close(IntPtr movie) => _close(movie);
    }
}

internal enum BinkMovieFamily
{
    Bink1,
    Bink2,
}

internal enum BinkMovieMode
{
    Guest,
    Skip,
    Dummy,
    Native,
}

internal enum BinkMovieRangeAttachment
{
    None,
    Dummy,
    Native,
}

internal readonly record struct BinkMovieHeaderInfo(
    string Signature,
    BinkMovieFamily Family,
    long ByteLength,
    uint FrameCount,
    uint LargestFrameSize,
    uint Width,
    uint Height,
    uint FramesPerSecondNumerator,
    uint FramesPerSecondDenominator);

internal readonly record struct BinkMovieRangeResult(
    string HostPath,
    int FileDescriptor,
    long FileOffset,
    int RequestedLength,
    int ReadLength,
    ulong GuestDestination,
    ulong GuestRip,
    int ManagedThreadId,
    string? ManagedThreadName,
    BinkMovieHeaderInfo Header,
    BinkMovieMode Mode,
    BinkMovieRangeAttachment Attachment);
