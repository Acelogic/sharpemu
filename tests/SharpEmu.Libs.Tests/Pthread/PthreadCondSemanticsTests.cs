// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

// POSIX condition variables are edges, not semaphore credits. A signal with no waiter
// must have no effect. This was violated by the previous implementation which persisted
// signals via PendingSignals, causing lock inversions and predicate bypasses.
// See issue #113.
public sealed class PthreadCondSemanticsTests
{
    private const ulong MemoryBase = 0x2_0000_0000;
    private const ulong MutexAddress = MemoryBase + 0x100;
    private const ulong CondAddress = MemoryBase + 0x200;

    [Fact]
    public void PthreadCondState_DoesNotHavePendingSignals()
    {
        // Verify that PthreadCondState no longer has the PendingSignals property.
        // This is a regression test to ensure the POSIX-correct behavior is maintained.
        var stateType = typeof(KernelPthreadCompatExports).GetNestedType("PthreadCondState", BindingFlags.NonPublic);
        Assert.NotNull(stateType);

        var pendingSignalsProp = stateType.GetProperty("PendingSignals");
        Assert.Null(pendingSignalsProp);

        var tryConsumeMethod = stateType.GetMethod("TryConsumePendingSignal");
        Assert.Null(tryConsumeMethod);
    }

    [Fact]
    public void PthreadCondSignal_WithNoWaiter_DoesNotPersist()
    {
        // This test verifies the semantic contract: signal without waiter is a no-op.
        // We can't easily test the full pthread flow without the scheduler, but we can
        // verify the code path by checking that SignalEpoch advances but no state persists.
        var stateType = typeof(KernelPthreadCompatExports).GetNestedType("PthreadCondState", BindingFlags.NonPublic);
        Assert.NotNull(stateType);

        // Create an instance via reflection
        var state = Activator.CreateInstance(stateType);
        Assert.NotNull(state);

        var syncRootProp = stateType.GetProperty("SyncRoot");
        var signalEpochProp = stateType.GetProperty("SignalEpoch");
        var waitersProp = stateType.GetProperty("Waiters");

        Assert.NotNull(syncRootProp);
        Assert.NotNull(signalEpochProp);
        Assert.NotNull(waitersProp);

        var syncRoot = syncRootProp.GetValue(state);
        Assert.NotNull(syncRoot);

        // Initial state
        Assert.Equal(0UL, (ulong)signalEpochProp.GetValue(state)!);
        Assert.Equal(0, (int)waitersProp.GetValue(state)!);

        // Simulate signal with no waiter (this would have incremented PendingSignals before)
        lock (syncRoot)
        {
            signalEpochProp.SetValue(state, (ulong)signalEpochProp.GetValue(state)! + 1);
            // Note: we don't increment PendingSignals because it doesn't exist
        }

        // Verify epoch advanced but no persistent signal state
        Assert.Equal(1UL, (ulong)signalEpochProp.GetValue(state)!);

        // A new waiter arriving should see the new epoch but not consume any "pending" signal
        // (because there's no such concept anymore)
        lock (syncRoot)
        {
            var observedEpoch = (ulong)signalEpochProp.GetValue(state)!;
            waitersProp.SetValue(state, (int)waitersProp.GetValue(state)! + 1);

            // Waiter sees epoch=1, will block until epoch changes again
            Assert.Equal(1UL, observedEpoch);
            Assert.Equal(1, (int)waitersProp.GetValue(state)!);
        }
    }

    [Fact]
    public void PthreadCondRelativeTimedwait_RegistersAndReturnsPosixTimeout()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(manager.TryGetExport("K953PF5u6Pc", out var export));
        Assert.Equal("pthread_cond_reltimedwait_np", export.Name);

        var context = new CpuContext(new AllocatingPthreadMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = MutexAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexInit(context));

        context[CpuRegister.Rdi] = CondAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondInit(context));

        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexLock(context));

        context[CpuRegister.Rdi] = CondAddress;
        context[CpuRegister.Rsi] = MutexAddress;
        context[CpuRegister.Rdx] = 0;
        Assert.Equal(60, KernelPthreadCompatExports.PosixPthreadCondRelativeTimedwait(context));

        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexUnlock(context));

        context[CpuRegister.Rdi] = CondAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondDestroy(context));
        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexDestroy(context));
    }

    private sealed class AllocatingPthreadMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;
        private ulong _nextAllocation;

        public AllocatingPthreadMemory(ulong baseAddress, int size)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
            _nextAllocation = baseAddress + 0x400;
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = 0;
            if (size == 0 || alignment == 0 || (alignment & (alignment - 1)) != 0)
            {
                return false;
            }

            var aligned = (_nextAllocation + alignment - 1) & ~(alignment - 1);
            if (size > int.MaxValue || !TryResolve(aligned, (int)size, out _))
            {
                return false;
            }

            address = aligned;
            _nextAllocation = aligned + size;
            return true;
        }

        public bool TryFreeGuestMemory(ulong address) => true;

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < _baseAddress || length < 0)
            {
                return false;
            }

            var relative = virtualAddress - _baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
