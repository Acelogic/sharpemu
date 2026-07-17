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
    public void EventPropertyArraySetString_CopiesValidUtf8IntoArrayState()
    {
        Initialize();
        WritePropertyType(0x2002);
        _memory.WriteCString(StringAddress, "astro-🌟");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.True(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out var value));
        Assert.Equal("astro-🌟", value);
    }

    [Fact]
    public void EventPropertyArraySetString_InvalidUtf8DoesNotReplaceExistingState()
    {
        Initialize();
        WritePropertyType(0x2002);
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

    private void Initialize()
    {
        Assert.True(_memory.TryWrite(ParametersAddress, new byte[16]));
        _ctx[CpuRegister.Rdi] = ParametersAddress;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemInitialize(_ctx));
    }

    private void WritePropertyType(ushort type)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, type);
        Assert.True(_memory.TryWrite(ArrayAddress, bytes));
    }
}
