// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    [SysAbiExport(
        Nid = "mkgXxsoxWHg",
        ExportName = "sceKernelClearVirtualRangeName",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelClearVirtualRangeName(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var length = ctx[CpuRegister.Rsi];
        lock (_memoryGate)
        {
            if (length == 0 ||
                !TryFindVirtualQueryRegionLocked(address, findNext: false, out var region) ||
                address < region.Address ||
                length > region.Length ||
                length > region.Address + region.Length - address)
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, typeof(long));
            }

            _mappedRegionNames.Remove(region.Address);
        }

        return ctx.SetReturn(0, typeof(long));
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
}
