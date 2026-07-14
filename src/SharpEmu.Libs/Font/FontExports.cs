// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Font;

public static class FontExports
{
    [SysAbiExport(
        Nid = "whrS4oksXc4",
        ExportName = "sceFontMemoryInit",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int MemoryInit(CpuContext ctx) => ctx.SetReturn(0);
}
