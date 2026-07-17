// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

[Collection("NpUniversalDataSystem")]
public sealed class NpUniversalDataSystemExportsTests
{
    private const ulong BaseAddress = 0x3_0000_0000;
    private const ulong ParametersAddress = BaseAddress + 0x100;
    private const ulong ArrayAddress = BaseAddress + 0x200;
    private const ulong StringAddress = BaseAddress + 0x400;
    private const ulong ArrayBackingAddress = BaseAddress + 0x800;
    private const ulong ArrayNodeAddress = BaseAddress + 0x900;
    private const ulong NestedBackingAddress = BaseAddress + 0xA00;
    private const ulong NestedNodeAddress = BaseAddress + 0xB00;
    private const ulong KeyStringBackingAddress = BaseAddress + 0xC00;
    private const ulong KeyStringAddress = BaseAddress + 0xD00;

    private readonly FakeCpuMemory _memory = new(BaseAddress, 0x2000);
    private readonly CpuContext _ctx;

    public NpUniversalDataSystemExportsTests()
    {
        NpUniversalDataSystemExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void EventPropertyArraySetString_RegistersForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("4llLk7YJRTE", out var export));
        Assert.Equal("sceNpUniversalDataSystemEventPropertyArraySetString", export.Name);
        Assert.Equal("libSceNpUniversalDataSystem", export.LibraryName);
    }

    [Fact]
    public void EventPropertyArraySetString_ChecksInitializationBeforeArguments()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0;

        Assert.Equal(
            unchecked((int)0x80553117),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
    }

    [Fact]
    public void EventPropertyArraySetString_RejectsNullArrayAfterInitialization()
    {
        Initialize();
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x8055311A),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
    }

    [Fact]
    public void EventPropertyArraySetString_AppendsValidUtf8IntoArrayState()
    {
        Initialize();
        WriteEmptyArray();
        _memory.WriteCString(StringAddress, "astro-🌟");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        _memory.WriteCString(StringAddress, "second");
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.True(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringStateForTests(
            ArrayAddress,
            out var temporaryType,
            out var value));
        Assert.Equal(0x2001, temporaryType);
        Assert.Equal("second", value);
        Assert.True(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringsForTests(
            ArrayAddress,
            out var values));
        Assert.Equal(["astro-🌟", "second"], values);
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x30, out var count));
        Assert.Equal(2UL, count);
    }

    [Fact]
    public void EventPropertyArraySetString_InvalidUtf8DoesNotReplaceExistingState()
    {
        Initialize();
        WriteEmptyArray();
        _memory.WriteCString(StringAddress, "existing");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));

        Assert.True(_memory.TryWrite(StringAddress, new byte[] { 0xC0, 0x80, 0 }));
        Assert.Equal(
            unchecked((int)0x80553115),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.True(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out var value));
        Assert.Equal("existing", value);
    }

    [Fact]
    public void EventPropertyArraySetString_InvalidPropertyTypeDoesNotWriteState()
    {
        Initialize();
        WritePropertyType(0x7777);
        _memory.WriteCString(StringAddress, "ignored");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x80553115),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    [Fact]
    public void EventPropertyArraySetString_TagValidPrimitiveReachesMissingBackingError()
    {
        Initialize();
        WritePropertyType(0x1001);
        _memory.WriteCString(StringAddress, "not-an-array");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x8055BB0C),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    [Fact]
    public void EventPropertyArraySetString_NormalizesInternalSetterFailureWithoutMutation()
    {
        Initialize();
        WriteEmptyArray();
        _memory.WriteCString(StringAddress, "existing");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));

        _memory.WriteCString(StringAddress, "replacement");
        NpUniversalDataSystemExports.SetEventPropertyArrayAllocationFailureForTests(true);
        Assert.Equal(
            unchecked((int)0x80553101),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.True(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out var value));
        Assert.Equal("existing", value);
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x30, out var count));
        Assert.Equal(1UL, count);
    }

    [Fact]
    public void EventPropertyArraySetString_CountMinusOneReturnsArrayFullWithoutMutation()
    {
        Initialize();
        WriteEmptyArray();
        Assert.True(_ctx.TryWriteUInt64(ArrayBackingAddress + 0x30, ulong.MaxValue));
        _memory.WriteCString(StringAddress, "not-stored");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x8055BB09),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    [Fact]
    public void EventPropertyArraySetString_ArrayWithoutBackingReturnsDirectSetterError()
    {
        Initialize();
        WritePropertyType(0x2002);
        _memory.WriteCString(StringAddress, "not-stored");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x8055BB0C),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    [Fact]
    public void EventPropertyArraySetString_RejectsInvalidNestedArrayValue()
    {
        Initialize();
        WriteEmptyArray();
        Assert.True(_ctx.TryWriteUInt64(ArrayBackingAddress + 0x20, ArrayNodeAddress));
        Assert.True(_ctx.TryWriteUInt64(ArrayBackingAddress + 0x30, 1));
        Assert.True(_ctx.TryWriteUInt64(ArrayNodeAddress + 0x08, 0));
        WritePropertyType(ArrayNodeAddress + 0x18, 0x7777);
        _memory.WriteCString(StringAddress, "not-stored");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x80553115),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    [Fact]
    public void EventPropertyArraySetString_RejectsNestedObjectWithEmptyStringKey()
    {
        Initialize();
        WriteEmptyArray();
        Assert.True(_ctx.TryWriteUInt64(ArrayBackingAddress + 0x20, ArrayNodeAddress));
        Assert.True(_ctx.TryWriteUInt64(ArrayNodeAddress + 0x08, 0));
        WritePropertyType(ArrayNodeAddress + 0x18, 0x2003);
        Assert.True(_ctx.TryWriteUInt64(ArrayNodeAddress + 0x20, NestedBackingAddress));
        Assert.True(_ctx.TryWriteUInt64(NestedBackingAddress + 0x20, NestedNodeAddress));
        Assert.True(_ctx.TryWriteUInt64(NestedNodeAddress + 0x08, 0));
        WritePropertyType(NestedNodeAddress + 0x18, 0x2001);
        Assert.True(_ctx.TryWriteUInt64(NestedNodeAddress + 0x20, KeyStringBackingAddress));
        Assert.True(_ctx.TryWriteUInt64(KeyStringBackingAddress + 0x18, KeyStringAddress));
        _memory.WriteCString(KeyStringAddress, string.Empty);
        WritePropertyType(NestedNodeAddress + 0x28, 0x1001);
        _memory.WriteCString(StringAddress, "not-stored");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x80553115),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    [Fact]
    public void EventPropertyArraySetString_RejectsCyclicNestedArrayBacking()
    {
        Initialize();
        WriteEmptyArray();
        Assert.True(_ctx.TryWriteUInt64(ArrayBackingAddress + 0x20, ArrayNodeAddress));
        Assert.True(_ctx.TryWriteUInt64(ArrayNodeAddress + 0x08, 0));
        WritePropertyType(ArrayNodeAddress + 0x18, 0x2002);
        Assert.True(_ctx.TryWriteUInt64(ArrayNodeAddress + 0x20, ArrayBackingAddress));
        _memory.WriteCString(StringAddress, "not-stored");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x80553115),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    private void Initialize()
    {
        Assert.True(_memory.TryWrite(ParametersAddress, new byte[16]));
        _ctx[CpuRegister.Rdi] = ParametersAddress;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemInitialize(_ctx));
    }

    private void WritePropertyType(ushort type)
    {
        WritePropertyType(ArrayAddress, type);
    }

    private void WriteEmptyArray()
    {
        WritePropertyType(0x2002);
        Assert.True(_ctx.TryWriteUInt64(ArrayAddress + 0x08, ArrayBackingAddress));
        Assert.True(_memory.TryWrite(ArrayBackingAddress, new byte[0x38]));
    }

    private void WritePropertyType(ulong address, ushort type)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, type);
        Assert.True(_memory.TryWrite(address, bytes));
    }
}
