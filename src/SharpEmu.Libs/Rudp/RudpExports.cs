// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Rudp;

public static class RudpExports
{
    private const int RudpErrorAlreadyInitialized = unchecked((int)0x80770002);
    private const int RudpErrorInvalidArgument = unchecked((int)0x80770004);
    private const int RudpErrorOutOfMemory = unchecked((int)0x80770007);
    private const int MinimumAllocatorStorageSize = 0xF8 + 0x2D8;
    private const int GuestBufferProbeSize = 4096;

    private static readonly object StateGate = new();
    private static bool _initialized;
    private static ulong _retainedBufferAddress;
    private static int _retainedBufferSize;

    [SysAbiExport(
        Nid = "amuBfI-AQc4",
        ExportName = "sceRudpInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRudp")]
    public static int Init(CpuContext ctx)
    {
        var bufferAddress = ctx[CpuRegister.Rdi];
        var bufferSize = unchecked((int)ctx[CpuRegister.Rsi]);

        lock (StateGate)
        {
            if (_initialized)
            {
                return ctx.SetReturn(RudpErrorAlreadyInitialized);
            }

            ClearRetainedState();
            if (bufferAddress == 0 || bufferSize < 1)
            {
                return ctx.SetReturn(RudpErrorInvalidArgument);
            }

            if (bufferSize < MinimumAllocatorStorageSize ||
                !IsGuestBufferAvailable(ctx, bufferAddress, bufferSize))
            {
                return ctx.SetReturn(RudpErrorOutOfMemory);
            }

            // The firmware allocator and both RUDP objects are backed by this
            // caller-owned region. Retain the exact address/size for the entire
            // initialized lifetime rather than treating Init as a success stub.
            _retainedBufferAddress = bufferAddress;
            _retainedBufferSize = bufferSize;
            _initialized = true;
            return ctx.SetReturn(0);
        }
    }

    private static bool IsGuestBufferAvailable(
        CpuContext ctx,
        ulong bufferAddress,
        int bufferSize)
    {
        var byteCount = (ulong)bufferSize;
        if (bufferAddress > ulong.MaxValue - (byteCount - 1))
        {
            return false;
        }

        Span<byte> probe = stackalloc byte[GuestBufferProbeSize];
        for (ulong offset = 0; offset < byteCount;)
        {
            var length = (int)Math.Min(
                (ulong)GuestBufferProbeSize,
                byteCount - offset);
            var chunk = probe[..length];
            var address = bufferAddress + offset;
            if (!ctx.Memory.TryRead(address, chunk) ||
                !ctx.Memory.TryWrite(address, chunk))
            {
                return false;
            }

            offset += (ulong)length;
        }

        return true;
    }

    internal static (bool Initialized, ulong BufferAddress, int BufferSize)
        GetStateForTests()
    {
        lock (StateGate)
        {
            return (_initialized, _retainedBufferAddress, _retainedBufferSize);
        }
    }

    internal static void ResetForTests()
    {
        lock (StateGate)
        {
            ClearRetainedState();
        }
    }

    private static void ClearRetainedState()
    {
        _initialized = false;
        _retainedBufferAddress = 0;
        _retainedBufferSize = 0;
    }
}
