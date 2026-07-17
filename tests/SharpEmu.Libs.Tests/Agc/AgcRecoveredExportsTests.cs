// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcRecoveredExportsTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int IncompatiblePair = unchecked((int)0x8A6C0008);
    private const int DescriptorSize = 0x60;

    [Fact]
    public void GetIsTrinityMode_WritesOneZeroByteAndPreservesRax()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x100);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Span<byte> sentinel = stackalloc byte[] { 0xAA, 0xBB };
        Assert.True(memory.TryWrite(BaseAddress + 0x20, sentinel));
        ctx[CpuRegister.Rdi] = BaseAddress + 0x20;
        ctx[CpuRegister.Rax] = 0x1122_3344_5566_7788;
        ctx.ClearRaxWriteFlag();

        Assert.Equal(0, AgcExports.GetIsTrinityMode(ctx));

        Span<byte> actual = stackalloc byte[2];
        Assert.True(memory.TryRead(BaseAddress + 0x20, actual));
        Assert.Equal(new byte[] { 0, 0xBB }, actual.ToArray());
        Assert.True(ctx.WasRaxWritten);
        Assert.Equal(0x1122_3344_5566_7788UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void GetIsTrinityMode_ModuleDispatchPreservesIncomingRax()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x100);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        ctx[CpuRegister.Rdi] = BaseAddress + 0x20;
        ctx[CpuRegister.Rax] = 0x8877_6655_4433_2211;

        Assert.True(
            manager.TryDispatch("BfBDZGbti7A", ctx, out var result));

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0x8877_6655_4433_2211UL, ctx[CpuRegister.Rax]);
        Assert.True(ctx.WasRaxWritten);
        Assert.Equal(0, ReadByte(memory, BaseAddress + 0x20));
    }

    [Theory]
    [InlineData(4, 6)]
    [InlineData(5, 7)]
    public void UnknownStorageSize_AcceptsBothRecoveredPairs(
        byte firstType,
        byte secondType)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var output = BaseAddress + 0x100;
        var first = BaseAddress + 0x200;
        var second = BaseAddress + 0x300;
        WriteByte(memory, first + 0x5A, firstType);
        WriteByte(memory, second + 0x5A, secondType);
        WriteByte(memory, second + 0x5C, 9);
        ctx[CpuRegister.Rdi] = output;
        ctx[CpuRegister.Rsi] = first;
        ctx[CpuRegister.Rdx] = second;

        Assert.Equal(
            0,
            AgcExports.UnknownGetCombinedShaderRegisterStorageSize(ctx));
        Assert.Equal(72UL, ReadUInt64(memory, output));
        Assert.Equal(4UL, ReadUInt64(memory, output + 8));
    }

    [Theory]
    [InlineData(3, 6)]
    [InlineData(4, 7)]
    [InlineData(5, 6)]
    public void UnknownStorageSize_InvalidPairLeavesOutputUntouched(
        byte firstType,
        byte secondType)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var output = BaseAddress + 0x100;
        var first = BaseAddress + 0x200;
        var second = BaseAddress + 0x300;
        Span<byte> sentinel = stackalloc byte[16];
        sentinel.Fill(0x5A);
        Assert.True(memory.TryWrite(output, sentinel));
        WriteByte(memory, first + 0x5A, firstType);
        WriteByte(memory, second + 0x5A, secondType);
        ctx[CpuRegister.Rdi] = output;
        ctx[CpuRegister.Rsi] = first;
        ctx[CpuRegister.Rdx] = second;

        Assert.Equal(
            IncompatiblePair,
            AgcExports.UnknownGetCombinedShaderRegisterStorageSize(ctx));
        AssertBytes(memory, output, sentinel);
    }

    [Fact]
    public void UnknownCreateCombinedShader_CompatibilityFailureIsNotAtomic()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x3000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var output = BaseAddress + 0x100;
        var first = BaseAddress + 0x300;
        var second = BaseAddress + 0x500;
        var firstSpecials = BaseAddress + 0x800;
        var secondSpecials = BaseAddress + 0x900;
        Span<byte> descriptor = stackalloc byte[DescriptorSize];
        descriptor.Fill(0x3C);
        descriptor[0x5A] = 6;
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x28..], secondSpecials);
        Assert.True(memory.TryWrite(second, descriptor));
        WriteByte(memory, first + 0x5A, 4);
        WriteUInt64(memory, first + 0x28, firstSpecials);
        WriteUInt64(memory, firstSpecials + 8, 0);
        WriteUInt64(memory, secondSpecials + 8, 1UL << 54);
        ctx[CpuRegister.Rdi] = output;
        ctx[CpuRegister.Rsi] = first;
        ctx[CpuRegister.Rdx] = second;

        Assert.Equal(
            IncompatiblePair,
            AgcExports.UnknownCreateCombinedShader(ctx));

        descriptor[0x5A] = 2;
        AssertBytes(memory, output, descriptor);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UnknownCreateCombinedShader_ReconcilesBothRecoveredPairs(
        bool hullLocalPair,
        bool useOptionalRegisterBuffer)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x5000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var output = BaseAddress + 0x100;
        var first = BaseAddress + 0x300;
        var second = BaseAddress + 0x500;
        var firstRegisters = BaseAddress + 0x1000;
        var secondRegisters = BaseAddress + 0x1400;
        var optionalRegisters = useOptionalRegisterBuffer
            ? BaseAddress + 0x1800
            : 0;
        var firstSpecials = BaseAddress + 0x1C00;
        var secondSpecials = BaseAddress + 0x1D00;
        var internalRegister = hullLocalPair ? 0x100u : 0x80u;
        var resource1Register = hullLocalPair ? 0x10Au : 0x8Au;
        var resource2Register = hullLocalPair ? 0x10Bu : 0x8Bu;
        var programLoRegister = hullLocalPair ? 0x148u : 0xC8u;
        var programHiRegister = programLoRegister + 1;
        var firstResource1 = hullLocalPair ? 0x3000_0003u : 0x4000_0003u;
        const uint firstResource2 = 0xF002_003E;
        var secondResource1 = hullLocalPair ? 0x1000_0001u : 0x2000_0001u;
        const uint secondResource2 = 0x0001_0001;
        var expectedResource1 = hullLocalPair ? 0x3000_0003u : 0x4000_0003u;
        var expectedResource2 = hullLocalPair ? 0x2001_003Fu : 0x2002_003Fu;
        const ulong codeAddress = 0x0000_12AB_CDEF_1200;

        WriteDescriptor(
            memory,
            first,
            hullLocalPair ? (byte)5 : (byte)4,
            firstRegisters,
            4,
            firstSpecials,
            codeAddress,
            secondQword: 0x1111);
        WriteDescriptor(
            memory,
            second,
            hullLocalPair ? (byte)7 : (byte)6,
            secondRegisters,
            6,
            secondSpecials,
            codeAddress: 0x2000,
            secondQword: 0xFFFF_FFFF_FFFF_FFFF);
        WriteRegisters(
            memory,
            firstRegisters,
            (internalRegister, 0x1111_1111),
            (internalRegister, 0x2222_2222),
            (resource1Register, firstResource1),
            (resource2Register, firstResource2));
        WriteRegisters(
            memory,
            secondRegisters,
            (internalRegister, 0x3333_3333),
            (internalRegister, 0x4444_4444),
            (resource1Register, secondResource1),
            (resource2Register, secondResource2),
            (programLoRegister, 0),
            (programHiRegister, 0xAABB_CC00));
        WriteUInt64(memory, firstSpecials + 8, 0);
        WriteUInt64(memory, secondSpecials + 8, 0);
        ctx[CpuRegister.Rdi] = output;
        ctx[CpuRegister.Rsi] = first;
        ctx[CpuRegister.Rdx] = second;
        ctx[CpuRegister.Rcx] = optionalRegisters;

        Assert.Equal(0, AgcExports.UnknownCreateCombinedShader(ctx));

        Assert.Equal(0UL, ReadUInt64(memory, output + 8));
        Assert.Equal(
            hullLocalPair ? (byte)3 : (byte)2,
            ReadByte(memory, output + 0x5A));
        var targetRegisters = useOptionalRegisterBuffer
            ? optionalRegisters
            : secondRegisters;
        Assert.Equal(targetRegisters, ReadUInt64(memory, output + 0x20));
        AssertRegisterValue(memory, targetRegisters, 0, 0x1111_1111);
        AssertRegisterValue(memory, targetRegisters, 1, 0x2222_2222);
        AssertRegisterValue(memory, targetRegisters, 2, expectedResource1);
        AssertRegisterValue(memory, targetRegisters, 3, expectedResource2);
        AssertRegisterValue(memory, targetRegisters, 4, 0xABCD_EF12);
        AssertRegisterValue(memory, targetRegisters, 5, 0xAABB_CC12);
        if (useOptionalRegisterBuffer)
        {
            AssertRegisterValue(memory, secondRegisters, 0, 0x3333_3333);
            AssertRegisterValue(memory, secondRegisters, 1, 0x4444_4444);
        }
    }

    [Fact]
    public void RecoveredAgcNids_RegisterWithExpectedIdentity()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        AssertExport(
            manager,
            "BfBDZGbti7A",
            "sceAgcGetIsTrinityMode");
        AssertExport(
            manager,
            "dolOmWH+huQ",
            "unknown_dolOmWH_huQ");
        AssertExport(
            manager,
            "fd5Bp5tGTgo",
            "unknown_fd5Bp5tGTgo");
    }

    private static void AssertExport(
        ModuleManager manager,
        string nid,
        string name)
    {
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceAgc", export.LibraryName);
    }

    private static void WriteDescriptor(
        FakeCpuMemory memory,
        ulong address,
        byte type,
        ulong registers,
        byte registerCount,
        ulong specials,
        ulong codeAddress,
        ulong secondQword)
    {
        Span<byte> descriptor = stackalloc byte[DescriptorSize];
        descriptor.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x08..], secondQword);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x10..], codeAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x20..], registers);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x28..], specials);
        descriptor[0x5A] = type;
        descriptor[0x5C] = registerCount;
        Assert.True(memory.TryWrite(address, descriptor));
    }

    private static void WriteRegisters(
        FakeCpuMemory memory,
        ulong address,
        params (uint Register, uint Value)[] registers)
    {
        Span<byte> record = stackalloc byte[8];
        for (var index = 0; index < registers.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                record,
                registers[index].Register);
            BinaryPrimitives.WriteUInt32LittleEndian(
                record[4..],
                registers[index].Value);
            Assert.True(memory.TryWrite(address + ((ulong)index * 8), record));
        }
    }

    private static void AssertRegisterValue(
        FakeCpuMemory memory,
        ulong address,
        int index,
        uint expected)
    {
        Span<byte> value = stackalloc byte[4];
        Assert.True(memory.TryRead(address + ((ulong)index * 8) + 4, value));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(value));
    }

    private static void WriteByte(FakeCpuMemory memory, ulong address, byte value)
    {
        Span<byte> bytes = stackalloc byte[] { value };
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static byte ReadByte(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[1];
        Assert.True(memory.TryRead(address, bytes));
        return bytes[0];
    }

    private static void WriteUInt64(
        FakeCpuMemory memory,
        ulong address,
        ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[8];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void AssertBytes(
        FakeCpuMemory memory,
        ulong address,
        ReadOnlySpan<byte> expected)
    {
        Span<byte> actual = stackalloc byte[expected.Length];
        Assert.True(memory.TryRead(address, actual));
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }
}
