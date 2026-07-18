// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelSemaphoreCompatExportsTests
{
    [Fact]
    public void KernelCreateSema_WritesHandleToNativeMappedGuestMemory()
    {
        var nameBytes = Encoding.UTF8.GetBytes("AstroContentExport\0");
        var context = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        var handleAddress = AllocateTracked(context, sizeof(uint));
        var nameAddress = AllocateTracked(context, nameBytes.Length);
        try
        {
            Marshal.WriteInt32(unchecked((nint)handleAddress), 0);
            Marshal.Copy(nameBytes, 0, unchecked((nint)nameAddress), nameBytes.Length);

            context[CpuRegister.Rdi] = handleAddress;
            context[CpuRegister.Rsi] = nameAddress;
            context[CpuRegister.Rdx] = 1;
            context[CpuRegister.Rcx] = 0;
            context[CpuRegister.R8] = 1;
            context[CpuRegister.R9] = 0;

            Assert.Equal(0, KernelSemaphoreCompatExports.KernelCreateSema(context));
            var handle = unchecked((uint)Marshal.ReadInt32(unchecked((nint)handleAddress)));
            Assert.NotEqual(0U, handle);

            context[CpuRegister.Rdi] = handle;
            Assert.Equal(0, KernelSemaphoreCompatExports.KernelDeleteSema(context));
        }
        finally
        {
            FreeTracked(context, nameAddress);
            FreeTracked(context, handleAddress);
        }
    }

    private static ulong AllocateTracked(CpuContext context, int length)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)length);
        Assert.Equal(0, KernelMemoryCompatExports.Malloc(context));
        Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        return context[CpuRegister.Rax];
    }

    private static void FreeTracked(CpuContext context, ulong address)
    {
        context[CpuRegister.Rdi] = address;
        Assert.Equal(0, KernelMemoryCompatExports.Free(context));
    }
}
