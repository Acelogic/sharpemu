// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Acm;

public static class AcmExports
{
    private const int AcmErrorOpenFailed = unchecked((int)0x81940001);
    private const int AcmErrorOutOfMemory = unchecked((int)0x81940004);
    private const int AcmErrorTooManyOpenFiles = unchecked((int)0x81940005);
    private const int AcmErrorInvalidArgument = unchecked((int)0x81940006);

    private static readonly object StateGate = new();
    private static long _nextDescriptor = 1;
    private static Func<(int Descriptor, int Errno)>? _openDeviceForTests;

    [SysAbiExport(
        Nid = "ZIXln2K3XMk",
        ExportName = "sceAcmContextCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int ContextCreate(CpuContext ctx)
    {
        var contextAddress = ctx[CpuRegister.Rdi];
        if (contextAddress == 0)
        {
            return ctx.SetReturn(AcmErrorInvalidArgument);
        }

        // Firmware initializes the caller's slot before opening /dev/acm.
        if (!ctx.TryWriteInt32(contextAddress, -1))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        int descriptor;
        int errno;
        lock (StateGate)
        {
            if (_openDeviceForTests is not null)
            {
                (descriptor, errno) = _openDeviceForTests();
            }
            else if (_nextDescriptor <= int.MaxValue)
            {
                descriptor = (int)_nextDescriptor++;
                errno = 0;
            }
            else
            {
                descriptor = -1;
                errno = 0x18;
            }
        }

        if (descriptor < 0)
        {
            var error = errno switch
            {
                0x17 or 0x18 => AcmErrorTooManyOpenFiles,
                0x0C => AcmErrorOutOfMemory,
                _ => AcmErrorOpenFailed,
            };
            return ctx.SetReturn(error);
        }

        if (!ctx.TryWriteInt32(contextAddress, descriptor))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    internal static void SetOpenDeviceForTests(
        Func<(int Descriptor, int Errno)>? openDevice)
    {
        lock (StateGate)
        {
            _openDeviceForTests = openDevice;
        }
    }

    internal static void ResetForTests()
    {
        lock (StateGate)
        {
            _nextDescriptor = 1;
            _openDeviceForTests = null;
        }
    }

    [SysAbiExport(
        Nid = "jBgBjAj02R8",
        ExportName = "sceAcmContextDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmContextDestroy(CpuContext ctx)
    {
        _ = ctx;
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
