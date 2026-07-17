// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Acm;

public static class AcmExports
{
    private const int AcmErrorInvalidArgument = unchecked((int)0x81940006);
    private const int EmulatedAcmDescriptor = 1;

    [SysAbiExport(
        Nid = "ZIXln2K3XMk",
        ExportName = "sceAcmContextCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int ContextCreate(CpuContext ctx)
    {
        var contextAddress = ctx[CpuRegister.Rdi];
        if (contextAddress == 0)
        {
            return ctx.SetReturn(AcmErrorInvalidArgument);
        }

        // Firmware initializes the caller's slot before opening /dev/acm. Keep
        // that observable write order even though the emulated descriptor cannot
        // fail to open.
        if (!ctx.TryWriteInt32(contextAddress, -1) ||
            !ctx.TryWriteInt32(contextAddress, EmulatedAcmDescriptor))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }
}
