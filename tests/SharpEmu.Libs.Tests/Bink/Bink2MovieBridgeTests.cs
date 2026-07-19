// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.Libs.Bink;
using Xunit;

namespace SharpEmu.Libs.Tests.Bink;

[Collection("KernelFileCompatState")]
public sealed class Bink2MovieBridgeTests : IDisposable
{
    private readonly string? _previousMode;

    public Bink2MovieBridgeTests()
    {
        _previousMode = Environment.GetEnvironmentVariable("SHARPEMU_BINK_MODE");
        Environment.SetEnvironmentVariable("SHARPEMU_BINK_MODE", "skip");
        Bink2MovieBridge.ResetForTests();
    }

    public void Dispose()
    {
        Bink2MovieBridge.ResetForTests();
        Environment.SetEnvironmentVariable("SHARPEMU_BINK_MODE", _previousMode);
    }

    [Fact]
    public void TryParseMovieRangeHeader_AcceptsObservedGtaKb2jMetadata()
    {
        const long fileOffset = 14_169_088;
        const int byteLength = 59_531_504;
        var header = CreateHeader(
            "KB2j",
            byteLength,
            frameCount: 450,
            largestFrameSize: 421_496,
            width: 3840,
            height: 2160,
            framesPerSecondNumerator: 30_000,
            framesPerSecondDenominator: 1001);

        Assert.True(Bink2MovieBridge.TryParseMovieRangeHeader(
            header,
            fileOffset,
            fileOffset + byteLength,
            out var info));
        Assert.Equal("KB2j", info.Signature);
        Assert.Equal(BinkMovieFamily.Bink2, info.Family);
        Assert.Equal(byteLength, info.ByteLength);
        Assert.Equal(450U, info.FrameCount);
        Assert.Equal(421_496U, info.LargestFrameSize);
        Assert.Equal(3840U, info.Width);
        Assert.Equal(2160U, info.Height);
        Assert.Equal(30_000U, info.FramesPerSecondNumerator);
        Assert.Equal(1001U, info.FramesPerSecondDenominator);
    }

    [Fact]
    public void TryParseMovieRangeHeader_AcceptsOnlyKnownBink1AndBink2Signatures()
    {
        foreach (var signature in new[]
                 {
                     "BIKf", "BIKg", "BIKh", "BIKi", "BIKk",
                     "KB2f", "KB2g", "KB2h", "KB2i", "KB2j", "KB2k", "KB2m",
                 })
        {
            var header = CreateHeader(signature, 256);
            Assert.True(Bink2MovieBridge.TryParseMovieRangeHeader(header, 0, 256, out var info));
            Assert.Equal(signature.StartsWith("BIK", StringComparison.Ordinal)
                ? BinkMovieFamily.Bink1
                : BinkMovieFamily.Bink2, info.Family);
        }

        Assert.False(Bink2MovieBridge.TryParseMovieRangeHeader(
            CreateHeader("KB2x", 256), 0, 256, out _));
        Assert.False(Bink2MovieBridge.TryParseMovieRangeHeader(
            CreateHeader("RIFF", 256), 0, 256, out _));
    }

    [Fact]
    public void TryParseMovieRangeHeader_RejectsMalformedOrOutOfBoundsRanges()
    {
        var valid = CreateHeader("KB2j", 256);
        Assert.False(Bink2MovieBridge.TryParseMovieRangeHeader(valid[..35], 0, 256, out _));
        Assert.False(Bink2MovieBridge.TryParseMovieRangeHeader(valid, -1, 256, out _));
        Assert.False(Bink2MovieBridge.TryParseMovieRangeHeader(valid, 64, 319, out _));
        Assert.False(Bink2MovieBridge.TryParseMovieRangeHeader(
            valid, long.MaxValue - 128, long.MaxValue, out _));

        AssertInvalid(valid, 0x08, 0);
        AssertInvalid(valid, 0x0C, 0);
        AssertInvalid(valid, 0x0C, 249);
        AssertInvalid(valid, 0x14, 0);
        AssertInvalid(valid, 0x18, 16_385);
        AssertInvalid(valid, 0x1C, 0);
        AssertInvalid(valid, 0x20, 0);
        AssertInvalid(valid, 0x1C, 1001);
    }

    [Fact]
    public void ObserveGuestMovieRange_ExposesSkipModeWithoutAttachingOrCreatingAFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sharpemu-bink-observe-{Guid.NewGuid():N}.rpf");
        var header = CreateHeader("KB2j", 256);

        var result = Bink2MovieBridge.ObserveGuestMovieRange(
            path,
            hostFileLength: 512,
            fileDescriptor: 317,
            fileOffset: 128,
            requestedLength: 256,
            readLength: header.Length,
            guestDestination: 0x11C9BFF88,
            guestRip: 0x80283A98C,
            header);

        Assert.True(result.HasValue);
        Assert.Equal(BinkMovieMode.Skip, result.Value.Mode);
        Assert.Equal(BinkMovieRangeAttachment.None, result.Value.Attachment);
        Assert.Equal(128, result.Value.FileOffset);
        Assert.Equal(256, result.Value.Header.ByteLength);
        Assert.False(Bink2MovieBridge.TryDecodeNextFrame(out _, out _, out _));
        Assert.False(File.Exists(path));
    }

    private static void AssertInvalid(byte[] source, int fieldOffset, uint value)
    {
        var header = (byte[])source.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(fieldOffset, sizeof(uint)), value);
        Assert.False(Bink2MovieBridge.TryParseMovieRangeHeader(header, 0, 256, out _));
    }

    private static byte[] CreateHeader(
        string signature,
        int byteLength,
        uint frameCount = 3,
        uint largestFrameSize = 64,
        uint width = 16,
        uint height = 16,
        uint framesPerSecondNumerator = 30,
        uint framesPerSecondDenominator = 1)
    {
        var header = new byte[0x24];
        Encoding.ASCII.GetBytes(signature).CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x04, 4), checked((uint)byteLength - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x08, 4), frameCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x0C, 4), largestFrameSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x14, 4), width);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18, 4), height);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x1C, 4), framesPerSecondNumerator);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x20, 4), framesPerSecondDenominator);
        return header;
    }
}
