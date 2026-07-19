// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioOut2ExportsTests
{
    [Theory]
    [InlineData(0U, 0U, 0x2E0UL)]
    [InlineData(0U, 1U, 0xAE00UL)]
    [InlineData(1U, 0U, 0x860UL)]
    [InlineData(1U, 1U, 0xB380UL)]
    public void GetSpeakerArrayMemorySize_MatchesFirmware1270ForEightSpeakers(
        uint useObjectLayout,
        uint includeCoefficients,
        ulong expectedSize)
    {
        var ctx = new CpuContext(new NullMemory(), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 8;
        ctx[CpuRegister.Rsi] = useObjectLayout;
        ctx[CpuRegister.Rdx] = includeCoefficients;

        Assert.Equal(0, AudioOut2Exports.AudioOut2GetSpeakerArrayMemorySize(ctx));
        Assert.Equal(expectedSize, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void GetSpeakerArrayMemorySize_RegistersExactGen5NidAsLlePreferred()
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "G1YOKDJYX2Y");

        Assert.Equal("sceAudioOut2GetSpeakerArrayMemorySize", export.Name);
        Assert.Equal("libSceAudioOut2", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
        Assert.True(export.PreferLle);
    }

    [Fact]
    public void ContextResetParam_MatchesFirmware1270DefaultBlock()
    {
        const ulong paramAddress = 0x1020;
        var memory = new global::SharpEmu.Libs.Tests.FakeCpuMemory(0x1000, 0x200);
        Span<byte> dirty = stackalloc byte[0x40];
        dirty.Fill(0xCC);
        Assert.True(memory.TryWrite(paramAddress, dirty));

        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = paramAddress;

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextResetParam(ctx));

        Span<byte> actual = stackalloc byte[0x40];
        Assert.True(memory.TryRead(paramAddress, actual));
        Span<byte> expected = stackalloc byte[0x40];
        expected.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(expected[0x00..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(expected[0x0C..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(expected[0x10..], 0x100);
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }

    [Fact]
    public void ContextQueryMemory_WritesSingleQwordWithoutClobberingGtaCanary()
    {
        const ulong paramAddress = 0x1020;
        const ulong sizeAddress = 0x1100;
        const ulong canary = 0xC0DEC0DECAFEBA00;
        var memory = new global::SharpEmu.Libs.Tests.FakeCpuMemory(0x1000, 0x200);

        Span<byte> param = stackalloc byte[0x40];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x00..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x10..], 0x100);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x14..], 1);
        Assert.True(memory.TryWrite(paramAddress, param));

        Span<byte> outputAndCanary = stackalloc byte[0x10];
        outputAndCanary.Fill(0xA5);
        BinaryPrimitives.WriteUInt64LittleEndian(outputAndCanary[0x08..], canary);
        Assert.True(memory.TryWrite(sizeAddress, outputAndCanary));

        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = paramAddress;
        ctx[CpuRegister.Rsi] = sizeAddress;

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextQueryMemory(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        Assert.True(memory.TryRead(sizeAddress, outputAndCanary));
        Assert.Equal(0xFA6CUL, BinaryPrimitives.ReadUInt64LittleEndian(outputAndCanary));
        Assert.Equal(canary, BinaryPrimitives.ReadUInt64LittleEndian(outputAndCanary[0x08..]));
    }

    [Fact]
    public void ContextGetQueueLevel_WritesTwoDwordsWithoutClobberingGtaCanary()
    {
        const ulong firstOutputAddress = 0x1100;
        const ulong secondOutputAddress = 0x1140;
        const ulong canary = 0xC0DEC0DECAFEBA00;
        var memory = new global::SharpEmu.Libs.Tests.FakeCpuMemory(0x1000, 0x200);

        Span<byte> firstOutputAndCanary = stackalloc byte[0x0C];
        firstOutputAndCanary.Fill(0xA5);
        BinaryPrimitives.WriteUInt64LittleEndian(firstOutputAndCanary[0x04..], canary);
        Assert.True(memory.TryWrite(firstOutputAddress, firstOutputAndCanary));

        Span<byte> secondOutput = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(secondOutput, 0xA5A5A5A5);
        Assert.True(memory.TryWrite(secondOutputAddress, secondOutput));

        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 3;
        ctx[CpuRegister.Rsi] = firstOutputAddress;
        ctx[CpuRegister.Rdx] = secondOutputAddress;

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        Assert.True(memory.TryRead(firstOutputAddress, firstOutputAndCanary));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(firstOutputAndCanary));
        Assert.Equal(canary, BinaryPrimitives.ReadUInt64LittleEndian(firstOutputAndCanary[0x04..]));
        Assert.True(memory.TryRead(secondOutputAddress, secondOutput));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(secondOutput));
    }

    private sealed class NullMemory : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }
}
