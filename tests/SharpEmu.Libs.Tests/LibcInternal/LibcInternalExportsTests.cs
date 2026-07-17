// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.LibcInternal;
using Xunit;

namespace SharpEmu.Libs.Tests.LibcInternal;

public sealed class LibcInternalExportsTests
{
    [Fact]
    public void SetJmp_RegistersAndReturnsZeroForInitialCall()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        var context = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 0x1_0000_0100;
        context[CpuRegister.Rax] = ulong.MaxValue;

        Assert.True(manager.TryGetExport("gNQ1V2vfXDE", out var export));
        Assert.Equal("setjmp", export.Name);
        Assert.Equal("libSceLibcInternal", export.LibraryName);
        Assert.True(manager.TryDispatch("gNQ1V2vfXDE", context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void SetJmp_DirectCallReturnsInitialZero()
    {
        var context = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 0x1_0000_0100;

        var result = LibcInternalExports.SetJmpInitialReturnCompat(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }
}
