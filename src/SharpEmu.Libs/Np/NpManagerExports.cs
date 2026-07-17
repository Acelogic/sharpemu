// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Np;

public static class NpManagerExports
{
    private const int NpTitleIdSize = 16;
    private const int NpTitleSecretSize = 128;
    private static ulong _managerAllocatorAddress;

    [SysAbiExport(
        Nid = "fHGhS3uP52k",
        ExportName = "sceNpManagerGlobalInitializeCompat1270",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpManagerGlobalInitializeCompat1270(CpuContext ctx)
    {
        var poolSize = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        if (poolSize == 0 || nameAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (_managerAllocatorAddress == 0 &&
            !NpCommonExports.TryCreateHleAllocator(ctx, poolSize, out _managerAllocatorAddress))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // Firmware 12.70's implementation (libSceNpManager +0x14950)
        // creates the module-private NP manager pool and callback table. The
        // pool never crosses the ABI boundary, so boot only needs the observed
        // validation and successful initialized result.
        TraceNp($"manager_global_init pool=0x{poolSize:X} name=0x{nameAddress:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "ukEeOizCkIU",
        ExportName = "sceNpManagerGetAllocatorCallbacksCompat1270",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpManagerGetAllocatorCallbacksCompat1270(CpuContext ctx)
    {
        if (_managerAllocatorAddress == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // The firmware table contains malloc, realloc, free, and a null user
        // pointer. Its address is consumed throughout ShellCore as allocator
        // identity; the shared executable no-op entries keep indirect cleanup
        // calls safe while the module-private pool remains HLE-owned.
        ctx[CpuRegister.Rax] = _managerAllocatorAddress;
        TraceNp($"manager_allocator_callbacks table=0x{_managerAllocatorAddress:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "4uhgVNAqiag",
        ExportName = "sceNpManagerGlobalTerminateCompat1270",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpManagerGlobalTerminateCompat1270(CpuContext ctx)
    {
        NpCommonExports.ReleaseHleAllocator(_managerAllocatorAddress);
        _managerAllocatorAddress = 0;
        TraceNp("manager_global_term");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "QvqOkNK5ThU",
        ExportName = "sceNpExtNpHttpClientConstructorCompat1270",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpExtNpHttpClientConstructorCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        var allocatorAddress = ctx[CpuRegister.Rsi];
        if (objectAddress == 0 || allocatorAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var objectBytes = new byte[0x50];
        BinaryPrimitives.WriteUInt64LittleEndian(objectBytes.AsSpan(0x08), allocatorAddress);
        if (!ctx.Memory.TryWrite(objectAddress, objectBytes) ||
            !KernelMemoryCompatExports.TryWriteDummyVtable(ctx, objectAddress))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // Firmware installs the ExtNpHttpClient vtable before initializing its
        // embedded synchronization state. ShellCore calls virtual slot +8 when
        // rolling this stage back, even when Initialize returned an error.
        ctx[CpuRegister.Rax] = objectAddress;
        TraceNp($"ext_http_client_ctor object=0x{objectAddress:X} allocator=0x{allocatorAddress:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "CvGog64+vCk",
        ExportName = "sceNpExtNpHttpClientInitializeCompat1270",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpExtNpHttpClientInitializeCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        var mode = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (objectAddress == 0 ||
            !ctx.TryWriteUInt64(objectAddress + 0x18, objectAddress + 0x18) ||
            !ctx.Memory.TryWrite(objectAddress + 0x20, new byte[] { 1 }) ||
            !ctx.Memory.TryWrite(objectAddress + 0x28, BitConverter.GetBytes(mode)))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // The real object creates asynchronous HTTP workers here. Keep those
        // workers quiescent for the boot path while preserving initialized
        // mutex and mode fields used by ShellCore's state checks.
        TraceNp($"ext_http_client_init object=0x{objectAddress:X} mode={mode}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "S7Afe0llsL8",
        ExportName = "sceNpCallbackSlotConstructorCompat1270",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCallbackSlotConstructorCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        if (objectAddress == 0 ||
            !ctx.Memory.TryWrite(objectAddress, new byte[0x10]) ||
            !KernelMemoryCompatExports.TryWriteDummyVtable(ctx, objectAddress) ||
            !ctx.TryWriteUInt32(objectAddress + 8, uint.MaxValue))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = objectAddress;
        TraceNp($"callback_slot_ctor object=0x{objectAddress:X} id=-1");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "gQFyT9aIsOk",
        ExportName = "sceNpCallbackSlotDestructorCompat1270",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCallbackSlotDestructorCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = objectAddress;
        TraceNp($"callback_slot_dtor object=0x{objectAddress:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
    private const int NpErrorInvalidArgument = unchecked((int)0x80550003);

    [SysAbiExport(
        Nid = "3Zl8BePTh9Y",
        ExportName = "sceNpCheckCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCheckCallback(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "S7QTn72PrDw",
        ExportName = "sceNpDeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpDeleteRequest(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "JELHf4xPufo",
        ExportName = "sceNpCheckCallbackForLib",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCheckCallbackForLib(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Offline profile: the online id payload is left untouched and the call
    // reports success, matching the other offline NpManager stubs here.
    [SysAbiExport(
        Nid = "XDncXQIJUSk",
        ExportName = "sceNpGetOnlineId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetOnlineId(CpuContext ctx)
    {
        // Gen5 ABI: user ID, then output structure.
        return WriteOfflineOnlineId(ctx, ctx[CpuRegister.Rsi]);
    }

    [SysAbiExport(
        Nid = "VfRSmPmj8Q8",
        ExportName = "sceNpRegisterStateCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpRegisterStateCallback(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "qQJfO8HAiaY",
        ExportName = "sceNpRegisterStateCallbackA",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpRegisterStateCallbackA(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "0c7HbXRKUt4",
        ExportName = "sceNpRegisterStateCallbackForToolkit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManagerForToolkit")]
    public static int NpRegisterStateCallbackForToolkit(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eQH7nWPcAgc",
        ExportName = "sceNpGetState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetState(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rsi];
        if (stateAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> stateBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(stateBytes, 1);
        return ctx.Memory.TryWrite(stateAddress, stateBytes)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "rbknaUjpqWo",
        ExportName = "sceNpGetAccountIdA",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetAccountIdA(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var accountIdAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || accountIdAddress == 0)
        {
            return SetReturn(ctx, NpErrorInvalidArgument);
        }

        // The offline profile exposed by sceNpGetState is signed in. Keep the
        // account query consistent with that state: Unity's PSN integration
        // treats SIGNED_OUT as an exceptional state and retries it every frame.
        // A stable local-only id is sufficient for titles which only use the
        // value as a profile key.
        Span<byte> accountId = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(accountId, 1);
        return ctx.Memory.TryWrite(accountIdAddress, accountId)
            ? SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "JT+t00a3TxA",
        ExportName = "sceNpGetAccountCountryA",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetAccountCountryA(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var countryAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || countryAddress == 0)
        {
            return SetReturn(ctx, NpErrorInvalidArgument);
        }

        Span<byte> country = stackalloc byte[4];
        country[0] = (byte)'U';
        country[1] = (byte)'S';
        country[2] = 0;
        country[3] = 0;
        return ctx.Memory.TryWrite(countryAddress, country)
            ? SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "e-ZuhGEoeC4",
        ExportName = "sceNpGetNpReachabilityState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetNpReachabilityState(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var stateAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || stateAddress == 0)
        {
            return SetReturn(ctx, NpErrorInvalidArgument);
        }

        Span<byte> state = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(state, 0); // Unavailable while offline.
        return ctx.Memory.TryWrite(stateAddress, state)
            ? SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "Ec63y59l9tw",
        ExportName = "sceNpSetNpTitleId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpSetNpTitleId(CpuContext ctx)
    {
        var titleIdAddress = ctx[CpuRegister.Rdi];
        var titleSecretAddress = ctx[CpuRegister.Rsi];
        if (titleIdAddress == 0 || titleSecretAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> titleId = stackalloc byte[NpTitleIdSize];
        Span<byte> titleSecret = stackalloc byte[NpTitleSecretSize];
        if (!ctx.Memory.TryRead(titleIdAddress, titleId) ||
            !ctx.Memory.TryRead(titleSecretAddress, titleSecret))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNp($"set_np_title_id title='{ReadTitleId(titleId)}'");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static string ReadTitleId(ReadOnlySpan<byte> bytes)
    {
        var length = 0;
        while (length < 12 && length < bytes.Length && bytes[length] != 0)
        {
            length++;
        }

        return length == 0
            ? string.Empty
            : System.Text.Encoding.ASCII.GetString(bytes[..length]);
    }

    private static void TraceNp(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] np.{message}");
    }

    private static int WriteOfflineOnlineId(CpuContext ctx, ulong address)
    {
        if (address == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // SceNpOnlineId is a 16-byte handle plus four trailing bytes.
        Span<byte> onlineId = stackalloc byte[20];
        "Player"u8.CopyTo(onlineId);
        return ctx.Memory.TryWrite(address, onlineId)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
