// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
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

    [Fact]
    public void AtomicFetchAddAndSub_UpdateNativeMappedGuestMemory()
    {
        var context = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = sizeof(uint);
        Assert.Equal(0, KernelMemoryCompatExports.Malloc(context));
        var valueAddress = context[CpuRegister.Rax];
        try
        {
            Marshal.WriteInt32(unchecked((nint)valueAddress), 7);
            context[CpuRegister.Rdi] = valueAddress;
            context[CpuRegister.Rsi] = 3;
            Assert.Equal(0, LibcInternalExports.AtomicFetchAdd32Compat1270(context));
            Assert.Equal(7UL, context[CpuRegister.Rax]);
            Assert.Equal(10, Marshal.ReadInt32(unchecked((nint)valueAddress)));

            context[CpuRegister.Rdi] = valueAddress;
            context[CpuRegister.Rsi] = 4;
            Assert.Equal(0, LibcInternalExports.AtomicFetchSub32Compat1270(context));
            Assert.Equal(10UL, context[CpuRegister.Rax]);
            Assert.Equal(6, Marshal.ReadInt32(unchecked((nint)valueAddress)));
        }
        finally
        {
            context[CpuRegister.Rdi] = valueAddress;
            Assert.Equal(0, KernelMemoryCompatExports.Free(context));
        }
    }
}
