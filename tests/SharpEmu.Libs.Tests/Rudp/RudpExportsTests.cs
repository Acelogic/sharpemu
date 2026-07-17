// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Rudp;
using Xunit;

namespace SharpEmu.Libs.Tests.Rudp;

public sealed class RudpExportsTests : IDisposable
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int AlreadyInitialized = unchecked((int)0x80770002);
    private const int InvalidArgument = unchecked((int)0x80770004);
    private const int OutOfMemory = unchecked((int)0x80770007);

    public RudpExportsTests() => RudpExports.ResetForTests();

    public void Dispose() => RudpExports.ResetForTests();

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 0x1000)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void Init_RejectsNullOrNonPositiveBuffer(long addressSelector, int size)
    {
        var ctx = CreateContext();
        ctx[CpuRegister.Rdi] = addressSelector == 0 ? 0 : BaseAddress;
        ctx[CpuRegister.Rsi] = unchecked((ulong)size);

        Assert.Equal(InvalidArgument, RudpExports.Init(ctx));
        Assert.False(RudpExports.GetStateForTests().Initialized);
    }

    [Fact]
    public void Init_ReportsAllocatorFailureForPositiveUndersizedBuffer()
    {
        var ctx = CreateContext();
        ctx[CpuRegister.Rdi] = BaseAddress;
        ctx[CpuRegister.Rsi] = 1;

        Assert.Equal(OutOfMemory, RudpExports.Init(ctx));
        Assert.False(RudpExports.GetStateForTests().Initialized);
    }

    [Fact]
    public void Init_RetainsCallerBufferAndChecksAlreadyInitializedFirst()
    {
        var ctx = CreateContext();
        ctx[CpuRegister.Rdi] = BaseAddress + 0x100;
        ctx[CpuRegister.Rsi] = 0x1000;

        Assert.Equal(0, RudpExports.Init(ctx));
        Assert.Equal(
            (true, BaseAddress + 0x100, 0x1000),
            RudpExports.GetStateForTests());

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(AlreadyInitialized, RudpExports.Init(ctx));
        Assert.Equal(
            (true, BaseAddress + 0x100, 0x1000),
            RudpExports.GetStateForTests());
    }

    [Fact]
    public void InitNid_RegistersWithRudpIdentity()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("amuBfI-AQc4", out var export));
        Assert.Equal("sceRudpInit", export.Name);
        Assert.Equal("libSceRudp", export.LibraryName);
    }

    private static CpuContext CreateContext() =>
        new(new FakeCpuMemory(BaseAddress, 0x4000), Generation.Gen5);
}
