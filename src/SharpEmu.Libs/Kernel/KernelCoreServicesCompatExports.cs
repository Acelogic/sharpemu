// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    private const int GuestErrnoInvalidArgument = 22;
    private const int GuestErrnoNoMemory = 12;
    private const int GuestErrnoOverflow = 84;
    private const uint OrbisKernelErrorBase = 0x80020000;

    [SysAbiExport(
        Nid = "mkgXxsoxWHg",
        ExportName = "sceKernelClearVirtualRangeName",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelClearVirtualRangeName(CpuContext ctx)
    {
        var errno = EmulateClearVirtualRangeName(
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi]);
        return ctx.SetReturn(ConvertGuestErrno(errno), typeof(long));
    }

    [SysAbiExport(
        Nid = "n1-v6FgU7MQ",
        ExportName = "sceKernelConfiguredFlexibleMemorySize",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelConfiguredFlexibleMemorySize(CpuContext ctx)
    {
        var outSizeAddress = ctx[CpuRegister.Rdi];
        if (!ctx.TryWriteUInt64(outSizeAddress, FlexibleMemorySizeBytes))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        return ctx.SetReturn(0, typeof(long));
    }

    internal static bool TryGetVirtualRangeNameForTests(ulong address, out string name)
    {
        lock (_memoryGate)
        {
            if (TryFindVirtualQueryRegionLocked(address, findNext: false, out var region) &&
                _mappedRegionNames.TryGetValue(region.Address, out var mappedName))
            {
                name = mappedName;
                return true;
            }
        }

        name = string.Empty;
        return false;
    }

    private static int EmulateClearVirtualRangeName(ulong address, ulong length)
    {
        if (address == 0 || length == 0)
        {
            return GuestErrnoInvalidArgument;
        }

        if (length > ulong.MaxValue - address)
        {
            return GuestErrnoOverflow;
        }

        lock (_memoryGate)
        {
            if (!TryFindVirtualQueryRegionLocked(address, findNext: false, out var region) ||
                address < region.Address ||
                length > region.Length ||
                length > region.Address + region.Length - address)
            {
                return GuestErrnoNoMemory;
            }

            _mappedRegionNames.Remove(region.Address);
        }

        return 0;
    }

    private static int ConvertGuestErrno(int errno) =>
        errno == 0
            ? 0
            : unchecked((int)(OrbisKernelErrorBase + (uint)errno));
}
