// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpWebApi2Exports
{
    private const int NpWebApi2ErrorInvalidArgument = unchecked((int)0x80553402);

    private static int _initialized;
    private static int _nextInternalContextId;

    [SysAbiExport(
        Nid = "+o9816YQhqQ",
        ExportName = "sceNpWebApi2Initialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Initialize(CpuContext ctx)
    {
        var httpContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var poolSize = ctx[CpuRegister.Rsi];

        if (httpContextId <= 0 || poolSize == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("init", httpContextId, poolSize);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "bEvXpcEk200",
        ExportName = "sceNpWebApi2Terminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Terminate(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        Interlocked.Exchange(ref _initialized, 0);
        TraceNpWebApi2("term", libraryContextId, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "zXaFo7euxsQ",
        ExportName = "sceNpWebApi2IntInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2IntInitialize(CpuContext ctx)
    {
        var argsAddress = ctx[CpuRegister.Rdi];
        if (argsAddress == 0 ||
            !ctx.TryReadInt32(argsAddress, out var httpContextId) ||
            !ctx.TryReadUInt64(argsAddress + 8, out var poolSize) ||
            !ctx.TryReadUInt64(argsAddress + 0x18, out var structSize) ||
            structSize < 0x20)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var libraryContextId = Interlocked.Increment(ref _nextInternalContextId);
        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("int_init", httpContextId, poolSize);
        ctx[CpuRegister.Rax] = unchecked((ulong)libraryContextId);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static void TraceNpWebApi2(string operation, int id, ulong arg0)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP_WEB_API2"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] npwebapi2.{operation} id={id} arg0=0x{arg0:X16} initialized={Volatile.Read(ref _initialized)}");
    }
}
