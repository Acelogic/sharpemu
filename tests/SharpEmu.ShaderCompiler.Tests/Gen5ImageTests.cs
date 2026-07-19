// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ImageTests
{
    private const ulong ShaderAddress = 0x1_0000_C000;
    private const uint SEndpgm = 0xBF810000;

    [Fact]
    public void BvhIntersectRayUsesSplitMimgOpcodeHighBit()
    {
        uint[] words =
        [
            0xF1989F07,
            0x00040303,
            0x43440D3F,
            0x46424140,
            0x00004847,
            SEndpgm,
        ];
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader[(index * sizeof(uint))..],
                words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, shader));
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var error),
            error);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "ImageBvhIntersectRay");
        Assert.Equal(Gen5ShaderEncoding.Mimg, instruction.Encoding);
        Assert.Equal(5, instruction.Words.Count);
        Assert.Equal(
            new[]
            {
                Gen5Operand.Vector(3),
                Gen5Operand.Vector(4),
                Gen5Operand.Vector(5),
                Gen5Operand.Vector(6),
            },
            instruction.Destinations);
        var control = Assert.IsType<Gen5ImageControl>(instruction.Control);
        Assert.Equal(
            new uint[] { 3, 63, 13, 68, 67, 64, 65, 66, 70, 71, 72 },
            control.AddressRegisters);
        Assert.Equal(16U, control.ScalarResource);
        Assert.Equal(0U, control.ScalarSampler);
        Assert.Equal(0xFU, control.Dmask);
        Assert.Equal(12, instruction.Sources.Count);
    }

    [Fact]
    public void ImageStoreDmaskPreservesMaskedChannels()
    {
        var maskedOpcodes = CompileImageStoreAndReadSpirvOpcodes(0x7);
        var fullOpcodes = CompileImageStoreAndReadSpirvOpcodes(0xF);

        Assert.Equal(
            fullOpcodes.Count(opcode => opcode == (ushort)SpirvOp.ImageRead) + 1,
            maskedOpcodes.Count(opcode => opcode == (ushort)SpirvOp.ImageRead));
        Assert.Equal(
            fullOpcodes.Count(opcode => opcode == (ushort)SpirvOp.CompositeInsert) + 3,
            maskedOpcodes.Count(opcode => opcode == (ushort)SpirvOp.CompositeInsert));
        Assert.Equal(
            fullOpcodes.Count(opcode => opcode == (ushort)SpirvOp.ImageWrite),
            maskedOpcodes.Count(opcode => opcode == (ushort)SpirvOp.ImageWrite));
    }

    [Fact]
    public void ImageLoadSharesStorageImageWhenOnlyWritePolicyBitsDiffer()
    {
        var control = new Gen5ImageControl(
            Dmask: 0xF,
            VectorAddress: 0,
            AddressRegisters: [0, 1],
            VectorData: 4,
            ScalarResource: 8,
            ScalarSampler: 0,
            Dimension: 1,
            IsArray: false,
            Glc: false,
            Slc: false,
            A16: false,
            D16: false);
        uint[] loadDescriptor =
        [
            0x053C4200,
            0xC4700000,
            0x0155C25F,
            0x91B00FAC,
            0,
            0,
            0xC07B0000,
            0x00057056,
        ];
        var storeDescriptor = (uint[])loadDescriptor.Clone();
        storeDescriptor[5] = 0x0070_0000;
        var load = new Gen5ImageBinding(
            0x80,
            "ImageLoad",
            control,
            loadDescriptor,
            [],
            null);
        var store = new Gen5ImageBinding(
            0x198,
            "ImageStore",
            control,
            storeDescriptor,
            [],
            null);

        Assert.True(Gen5ShaderTranslator.RequiresStorageImage(load, [load, store]));

        var otherDescriptor = (uint[])storeDescriptor.Clone();
        otherDescriptor[0]++;
        var otherStore = store with { ResourceDescriptor = otherDescriptor };
        Assert.True(
            Gen5ShaderTranslator.RequiresStorageImage(load, [load, otherStore]));

        var compressedDescriptor = (uint[])loadDescriptor.Clone();
        compressedDescriptor[1] = 169u << 20; // BC1_UNORM
        var compressedLoad = load with
        {
            ResourceDescriptor = compressedDescriptor,
        };
        Assert.False(
            Gen5ShaderTranslator.RequiresStorageImage(
                compressedLoad,
                [compressedLoad, otherStore]));
    }

    private static IReadOnlyList<ushort> CompileImageStoreAndReadSpirvOpcodes(
        uint dmask)
    {
        var control = new Gen5ImageControl(
            dmask,
            VectorAddress: 0,
            AddressRegisters: [0, 1],
            VectorData: 4,
            ScalarResource: 8,
            ScalarSampler: 0,
            Dimension: 1,
            IsArray: false,
            Glc: false,
            Slc: false,
            A16: false,
            D16: false);
        var store = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mimg,
            "ImageStore",
            [],
            [],
            [],
            control);
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [SEndpgm],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(ShaderAddress, [store, end]),
            [],
            null);
        var scalarRegisters = new uint[256];
        var descriptor = new uint[8];
        descriptor[1] = 71u << 20; // FORMAT_16_16_16_16_FLOAT
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [
                new Gen5ImageBinding(
                    store.Pc,
                    store.Opcode,
                    control,
                    descriptor,
                    [],
                    null),
            ],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out var error),
            error);
        return ReadSpirvOpcodes(shader.Spirv);
    }

    private static IReadOnlyList<ushort> ReadSpirvOpcodes(byte[] spirv)
    {
        var opcodes = new List<ushort>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            opcodes.Add((ushort)instruction);
            offset += wordCount * sizeof(uint);
        }

        return opcodes;
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < baseAddress ||
                address - baseAddress > (ulong)_storage.Length ||
                destination.Length > _storage.Length - (int)(address - baseAddress))
            {
                return false;
            }

            _storage.AsSpan((int)(address - baseAddress), destination.Length)
                .CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (address < baseAddress ||
                address - baseAddress > (ulong)_storage.Length ||
                source.Length > _storage.Length - (int)(address - baseAddress))
            {
                return false;
            }

            source.CopyTo(
                _storage.AsSpan((int)(address - baseAddress), source.Length));
            return true;
        }
    }
}
