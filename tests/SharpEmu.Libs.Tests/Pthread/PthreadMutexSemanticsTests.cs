// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

public sealed class PthreadMutexSemanticsTests
{
    [Fact]
    public void AdaptiveMutex_SelfLockUsesCompatibilityRecursion()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        var memory = new AllocatingCpuMemory(memoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(context.TryWriteUInt64(mutexAddress, 1)); // Static adaptive initializer.
        context[CpuRegister.Rdi] = mutexAddress;

        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
    }

    [Fact]
    public void MutexUnlock_ReservesHandoffForOldestWaiter()
    {
        const ulong memoryBase = 0x1_1000_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        var memory = new AllocatingCpuMemory(memoryBase, 0x4000);
        var ownerContext = new CpuContext(memory, Generation.Gen5);
        ownerContext[CpuRegister.Rdi] = mutexAddress;
        Assert.True(ownerContext.TryWriteUInt64(mutexAddress, 1)); // Static adaptive initializer.
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ownerContext));

        var state = GetMutexState(mutexAddress);
        var waiterCount = state.GetType().GetProperty("WaiterCount", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(waiterCount);

        using var waiterStarted = new ManualResetEventSlim();
        using var waiterAcquired = new ManualResetEventSlim();
        using var releaseWaiter = new ManualResetEventSlim();
        Exception? waiterError = null;
        var waiter = new Thread(() =>
        {
            try
            {
                var waiterContext = new CpuContext(memory, Generation.Gen5);
                waiterContext[CpuRegister.Rdi] = mutexAddress;
                waiterStarted.Set();
                Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(waiterContext));
                waiterAcquired.Set();
                Assert.True(releaseWaiter.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(waiterContext));
            }
            catch (Exception exception)
            {
                waiterError = exception;
            }
        });

        waiter.IsBackground = true;
        waiter.Start();
        Assert.True(waiterStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(
            () =>
            {
                lock (state)
                {
                    return (int)waiterCount.GetValue(state)! == 1;
                }
            },
            TimeSpan.FromSeconds(5)));

        int bargingResult;
        lock (state)
        {
            // Keep the waiter from reacquiring the host monitor between these
            // two calls. The mutex is unowned but already promised to it, so a
            // newcomer must observe BUSY rather than stealing the hand-off.
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ownerContext));
            bargingResult = KernelPthreadCompatExports.PthreadMutexTrylock(ownerContext);
            if (bargingResult == 0)
            {
                // Keep the regression test self-cleaning against the old,
                // barging implementation so its waiter cannot leak on failure.
                Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ownerContext));
            }
        }

        try
        {
            Assert.True(waiterAcquired.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseWaiter.Set();
            Assert.True(waiter.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.Null(waiterError);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, bargingResult);
    }

    private static object GetMutexState(ulong mutexAddress)
    {
        var statesField = typeof(KernelPthreadCompatExports).GetField(
            "_mutexStates",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(statesField);

        var states = statesField.GetValue(null);
        Assert.NotNull(states);
        var tryGetValue = states.GetType().GetMethod("TryGetValue", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(tryGetValue);

        object?[] arguments = [mutexAddress, null];
        Assert.True((bool)tryGetValue.Invoke(states, arguments)!);
        Assert.NotNull(arguments[1]);
        return arguments[1]!;
    }

    private sealed class AllocatingCpuMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;
        private ulong _nextAllocation;

        public AllocatingCpuMemory(ulong baseAddress, int size)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
            _nextAllocation = baseAddress + 0x1000;
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
            var mask = alignment - 1;
            var aligned = (_nextAllocation + mask) & ~mask;
            if (!TryResolve(aligned, checked((int)size), out _))
            {
                address = 0;
                return false;
            }

            address = aligned;
            _nextAllocation = aligned + size;
            return true;
        }

        public bool TryFreeGuestMemory(ulong address) =>
            address >= _baseAddress && address < _baseAddress + (ulong)_storage.Length;

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < _baseAddress)
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
