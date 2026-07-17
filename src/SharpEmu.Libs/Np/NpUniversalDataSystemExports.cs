// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace SharpEmu.Libs.Np;

public static class NpUniversalDataSystemExports
{
    private const int NpUniversalDataSystemErrorInvalidArgument = unchecked((int)0x80553102);
    private const int NpUniversalDataSystemErrorSetTargetInvalid = unchecked((int)0x8055311A);
    private const int NpUniversalDataSystemErrorInvalidProperty = unchecked((int)0x80553115);
    private const int NpUniversalDataSystemErrorNotInitialized = unchecked((int)0x80553117);
    private const int MaximumEventPropertyStringLength = 16 * 1024;
    private const int ValidPrimitivePropertyTypeMask = 0x799;
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);
    private static readonly object _eventGate = new();
    private static readonly HashSet<int> _createdEvents = [];
    private static readonly ConcurrentDictionary<ulong, string> _eventPropertyArrayStrings = new();
    private static int _nextHandle = 1;
    private static int _nextEvent = 1;
    private static int _isInitialized;

    [SysAbiExport(
        Nid = "sjaobBgqeB4",
        ExportName = "sceNpUniversalDataSystemInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemInitialize(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> parameters = stackalloc byte[16];
        if (!ctx.Memory.TryRead(parameterAddress, parameters))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        Volatile.Write(ref _isInitialized, 1);
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "5zBnau1uIEo",
        ExportName = "sceNpUniversalDataSystemCreateContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateContext(CpuContext ctx)
    {
        var contextAddress = ctx[CpuRegister.Rdi];
        if (contextAddress == 0)
        {
            return ctx.SetReturn(0, typeof(long));
        }

        Span<byte> context = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(context, 1);
        return ctx.Memory.TryWrite(contextAddress, context)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "hT0IAEvN+M0",
        ExportName = "sceNpUniversalDataSystemCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateHandle(CpuContext ctx)
    {
        var handle = Interlocked.Increment(ref _nextHandle);
        if (ctx.TryWriteInt32(ctx[CpuRegister.Rdi], handle, checkNil: true) ||
            ctx.TryWriteInt32(ctx[CpuRegister.Rsi], handle, checkNil: true))
        {
            return ctx.SetReturn(0, typeof(long));
        }

        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "p+GcLqwpL9M",
        ExportName = "sceNpUniversalDataSystemCreateEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateEvent(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        var eventId = Interlocked.Increment(ref _nextEvent);
        lock (_eventGate)
        {
            _createdEvents.Add(eventId);
        }

        if (ctx.TryWriteInt32(ctx[CpuRegister.Rdx], eventId, checkNil: true) ||
            ctx.TryWriteInt32(ctx[CpuRegister.Rcx], eventId, checkNil: true))
        {
            return ctx.SetReturn(0, typeof(long));
        }

        lock (_eventGate)
        {
            _createdEvents.Remove(eventId);
        }

        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "wG+84pnNIuo",
        ExportName = "sceNpUniversalDataSystemDestroyEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyEvent(CpuContext ctx)
    {
        var eventId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_eventGate)
        {
            _createdEvents.Remove(eventId);
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "MfDb+4Nln64",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetString(CpuContext ctx)
    {
        var propertyObjectAddress = ctx[CpuRegister.Rsi];
        var valueAddress = ctx[CpuRegister.Rdx];
        if (propertyObjectAddress == 0 || valueAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> probe = stackalloc byte[1];
        return ctx.Memory.TryRead(propertyObjectAddress, probe) &&
               ctx.Memory.TryRead(valueAddress, probe)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "Wxbg5x3pTXA",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetArray",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetArray(CpuContext ctx)
    {
        var propertyObjectAddress = ctx[CpuRegister.Rsi];
        var valueAddress = ctx[CpuRegister.Rdx];
        if (propertyObjectAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> probe = stackalloc byte[1];
        if (!ctx.Memory.TryRead(propertyObjectAddress, probe))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        if (valueAddress != 0 && !ctx.Memory.TryRead(valueAddress, probe))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "4llLk7YJRTE",
        ExportName = "sceNpUniversalDataSystemEventPropertyArraySetString",
        Target = Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyArraySetString(CpuContext ctx)
    {
        if (Volatile.Read(ref _isInitialized) == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorNotInitialized, typeof(long));
        }

        var propertyArrayAddress = ctx[CpuRegister.Rdi];
        if (propertyArrayAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorSetTargetInvalid, typeof(long));
        }

        Span<byte> propertyTypeBytes = stackalloc byte[sizeof(ushort)];
        if (!ctx.Memory.TryRead(propertyArrayAddress, propertyTypeBytes) ||
            !IsValidEventPropertyType(BinaryPrimitives.ReadUInt16LittleEndian(propertyTypeBytes)))
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidProperty, typeof(long));
        }

        if (!TryReadStrictUtf8CString(ctx, ctx[CpuRegister.Rsi], out var value))
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidProperty, typeof(long));
        }

        _eventPropertyArrayStrings[propertyArrayAddress] = value;
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "CzkKf7ahIyU",
        ExportName = "sceNpUniversalDataSystemPostEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemPostEvent(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "tpFJ8LIKvPw",
        ExportName = "sceNpUniversalDataSystemRegisterContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemRegisterContext(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "AUIHb7jUX3I",
        ExportName = "sceNpUniversalDataSystemDestroyHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyHandle(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    internal static bool TryGetEventPropertyArrayStringForTests(ulong address, out string value) =>
        _eventPropertyArrayStrings.TryGetValue(address, out value!);

    internal static void ResetForTests()
    {
        Volatile.Write(ref _isInitialized, 0);
        _eventPropertyArrayStrings.Clear();
        lock (_eventGate)
        {
            _createdEvents.Clear();
        }

        _nextHandle = 1;
        _nextEvent = 1;
    }

    private static bool IsValidEventPropertyType(ushort type)
    {
        if (type is >= 0x1001 and <= 0x100B)
        {
            var typeBit = type - 0x1001;
            return (ValidPrimitivePropertyTypeMask & (1 << typeBit)) != 0;
        }

        return type is >= 0x2001 and <= 0x2004;
    }

    private static bool TryReadStrictUtf8CString(CpuContext ctx, ulong address, out string value)
    {
        value = string.Empty;
        if (address == 0)
        {
            return false;
        }

        var bytes = new byte[MaximumEventPropertyStringLength];
        Span<byte> current = stackalloc byte[1];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)index, current))
            {
                return false;
            }

            if (current[0] == 0)
            {
                try
                {
                    value = _strictUtf8.GetString(bytes, 0, index);
                    return true;
                }
                catch (DecoderFallbackException)
                {
                    return false;
                }
            }

            bytes[index] = current[0];
        }

        return false;
    }
}
