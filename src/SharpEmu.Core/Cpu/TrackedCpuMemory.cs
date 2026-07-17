// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu;

public sealed class TrackedCpuMemory : ICpuMemory, ITrackedCpuMemory, IGuestMemoryAllocator
{
    private readonly ICpuMemory _inner;

    public TrackedCpuMemory(ICpuMemory inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public CpuMemoryAccessFailure? LastFailure { get; private set; }

    public ICpuMemory Inner => _inner;

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        var result = _inner.TryRead(virtualAddress, destination) ||
                     TryReadTrackedHostMemory(virtualAddress, destination);
        if (!result)
        {
            LastFailure = new CpuMemoryAccessFailure(virtualAddress, destination.Length, isWrite: false);
        }

        return result;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        var result = _inner.TryWrite(virtualAddress, source) ||
                     TryWriteTrackedHostMemory(virtualAddress, source);
        if (!result)
        {
            LastFailure = new CpuMemoryAccessFailure(virtualAddress, source.Length, isWrite: true);
        }

        return result;
    }

    public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
    {
        if (_inner is IGuestMemoryAllocator allocator)
        {
            return allocator.TryAllocateGuestMemory(size, alignment, out address);
        }

        address = 0;
        return false;
    }

    private static unsafe bool TryReadTrackedHostMemory(ulong address, Span<byte> destination)
    {
        if (!TryQueryTrackedHostRange(address, destination.Length, write: false))
        {
            return false;
        }

        new ReadOnlySpan<byte>((void*)address, destination.Length).CopyTo(destination);
        return true;
    }

    private static unsafe bool TryWriteTrackedHostMemory(ulong address, ReadOnlySpan<byte> source)
    {
        if (!TryQueryTrackedHostRange(address, source.Length, write: true))
        {
            return false;
        }

        source.CopyTo(new Span<byte>((void*)address, source.Length));
        return true;
    }

    private static unsafe bool TryQueryTrackedHostRange(ulong address, int length, bool write)
    {
        if (length == 0)
        {
            return true;
        }

        if (address == 0 || HostMemory.Query((void*)address, out var info) == 0 ||
            info.State != HostMemory.MEM_COMMIT)
        {
            return false;
        }

        ulong end;
        ulong regionEnd;
        try
        {
            end = checked(address + (ulong)length);
            regionEnd = checked(info.BaseAddress + info.RegionSize);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (address < info.BaseAddress || end > regionEnd)
        {
            return false;
        }

        return write
            ? info.Protect is HostMemory.PAGE_READWRITE or HostMemory.PAGE_EXECUTE_READWRITE
            : info.Protect is HostMemory.PAGE_READONLY or HostMemory.PAGE_READWRITE or
                HostMemory.PAGE_EXECUTE_READ or HostMemory.PAGE_EXECUTE_READWRITE;
    }
}
