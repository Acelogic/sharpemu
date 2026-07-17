// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static class KernelSemaphoreCompatExports
{
    private const int MaxSemaphoreNameLength = 128;
    private static readonly ConcurrentDictionary<uint, KernelSemaphoreState> _semaphores = new();
    private static int _nextSemaphoreHandle = 1;

    private sealed class KernelSemaphoreState
    {
        public required string Name { get; init; }
        public required int InitialCount { get; init; }
        public required int MaxCount { get; init; }
        public int Count { get; set; }
        public int WaitingThreads { get; set; }
        public int CancelEpoch { get; set; }
        public bool Deleted { get; set; }
        public object Gate { get; } = new();
    }

    private sealed class SemaphoreWaiter
    {
        public required int NeedCount { get; init; }
        public required int CancelEpochAtBlock { get; init; }
        public bool Timed { get; init; }
        public ulong TimeoutAddress { get; init; }
        public long DeadlineTimestamp { get; init; }

        // Written and read only under the owning semaphore's Gate.
        public int? Result { get; set; }
    }

    private static string GetSemaphoreWakeKey(uint handle) => $"kernel_sema:0x{handle:X8}";

    [SysAbiExport(
        Nid = "188x57JYp0g",
        ExportName = "sceKernelCreateSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCreateSema(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        var attr = unchecked((uint)ctx[CpuRegister.Rdx]);
        var initialCount = unchecked((int)ctx[CpuRegister.Rcx]);
        var maxCount = unchecked((int)ctx[CpuRegister.R8]);
        var optionAddress = ctx[CpuRegister.R9];

        const uint supportedAttributeMask = 0x103;
        if (semaphoreAddress == 0 ||
            nameAddress == 0 ||
            (attr & ~supportedAttributeMask) != 0 ||
            initialCount < 0 ||
            maxCount <= 0 ||
            initialCount > maxCount ||
            optionAddress != 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryReadNullTerminatedUtf8(nameAddress, MaxSemaphoreNameLength, out var name))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        if (handle == 0)
        {
            handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        }

        var state = new KernelSemaphoreState
        {
            Name = name,
            InitialCount = initialCount,
            MaxCount = maxCount,
            Count = initialCount,
        };
        _semaphores[handle] = state;

        if (!KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, semaphoreAddress, handle))
        {
            _semaphores.TryRemove(handle, out _);
            // Handles are sequential and guest-predictable, so a hostile guest can
            // race a WaitSema onto the handle between publication above and this
            // rollback. Strand-proof that waiter exactly like DeleteSema does.
            lock (state.Gate)
            {
                state.Deleted = true;
            }

            _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetSemaphoreWakeKey(handle));
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceSemaphore($"create handle=0x{handle:X8} name='{name}' attr=0x{attr:X} init={initialCount} max={maxCount}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "Zxa0VhQVTsk",
        ExportName = "sceKernelWaitSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelWaitSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var needCount = unchecked((int)ctx[CpuRegister.Rsi]);
        var timeoutAddress = ctx[CpuRegister.Rdx];
        uint timeoutUsec = 0;

        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (needCount < 1 || needCount > semaphore.MaxCount)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        SemaphoreWaiter waiter;
        lock (semaphore.Gate)
        {
            if (semaphore.Count >= needCount)
            {
                semaphore.Count -= needCount;
                TraceSemaphore($"wait handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
            }

            if (timeoutAddress != 0 &&
                !KernelMemoryCompatExports.TryReadUInt32Compat(ctx, timeoutAddress, out timeoutUsec))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (timeoutAddress != 0 && timeoutUsec == 0)
            {
                _ = KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, timeoutAddress, 0);
                TraceSemaphore($"wait-timeout handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count} timeout_us=0");
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
            }

            var deadlineTimestamp = timeoutAddress != 0
                ? GuestThreadExecution.ComputeDeadlineTimestamp(
                    TimeSpan.FromTicks(Math.Min((long)timeoutUsec * 10L, TimeSpan.MaxValue.Ticks)))
                : 0;
            waiter = new SemaphoreWaiter
            {
                NeedCount = needCount,
                CancelEpochAtBlock = semaphore.CancelEpoch,
                Timed = timeoutAddress != 0,
                TimeoutAddress = timeoutAddress,
                DeadlineTimestamp = deadlineTimestamp,
            };
            if (GuestThreadExecution.RequestCurrentThreadBlock(
                    ctx,
                    "sceKernelWaitSema",
                    GetSemaphoreWakeKey(handle),
                    resumeHandler: () => CompleteBlockedSemaWait(ctx, semaphore, waiter),
                    wakeHandler: () => TryConsumeBlockedSemaWait(semaphore, waiter),
                    blockDeadlineTimestamp: deadlineTimestamp))
            {
                semaphore.WaitingThreads++;
                TraceSemaphore($"wait-block handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count} waiters={semaphore.WaitingThreads} timeout_us={timeoutUsec}");
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
            }
        }

        // The primary executor cannot park through the guest-thread
        // continuation mechanism. Wait on the host while actively dispatching
        // ready guest workers; one of those workers is commonly the producer
        // that will signal this semaphore. Returning TRY_AGAIN here violates
        // sceKernelWaitSema's infinite-wait contract and trips game asserts.
        var scheduler = GuestThreadExecution.Scheduler;
        while (true)
        {
            lock (semaphore.Gate)
            {
                if (semaphore.Deleted)
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED);
                }

                if (semaphore.CancelEpoch != waiter.CancelEpochAtBlock)
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED);
                }

                if (semaphore.Count >= needCount)
                {
                    semaphore.Count -= needCount;
                    if (timeoutAddress != 0)
                    {
                        _ = KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, timeoutAddress, 0);
                    }
                    TraceSemaphore($"wait-host-wake handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
                }
            }

            if (waiter.Timed && Stopwatch.GetTimestamp() >= waiter.DeadlineTimestamp)
            {
                _ = KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, timeoutAddress, 0);
                TraceSemaphore($"wait-host-timeout handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count} timeout_us={timeoutUsec}");
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
            }

            scheduler?.Pump(ctx, "sceKernelWaitSema");
            Thread.Sleep(1);
        }
    }

    [SysAbiExport(
        Nid = "12wOHk8ywb0",
        ExportName = "sceKernelPollSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelPollSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var needCount = unchecked((int)ctx[CpuRegister.Rsi]);

        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (needCount < 1 || needCount > semaphore.MaxCount)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (semaphore.Gate)
        {
            if (semaphore.Count < needCount)
            {
                TraceSemaphore($"poll-busy handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
            }

            semaphore.Count -= needCount;
            TraceSemaphore($"poll handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }
    }

    [SysAbiExport(
        Nid = "4czppHBiriw",
        ExportName = "sceKernelSignalSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSignalSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var signalCount = unchecked((int)ctx[CpuRegister.Rsi]);

        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (signalCount <= 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (semaphore.Gate)
        {
            if (semaphore.Count > semaphore.MaxCount - signalCount)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            semaphore.Count += signalCount;
            TraceSemaphore($"signal handle=0x{handle:X8} name='{semaphore.Name}' signal={signalCount} count={semaphore.Count} waiters={semaphore.WaitingThreads}");
        }

        // Wake after releasing the gate (lock order: scheduler gate -> semaphore gate).
        // Wake everyone; the wake handler consumes the count per waiter, so a waiter
        // whose needCount exceeds the remaining count stays parked while a smaller
        // waiter can proceed.
        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetSemaphoreWakeKey(handle));
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "4DM06U2BNEY",
        ExportName = "sceKernelCancelSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCancelSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var setCount = unchecked((int)ctx[CpuRegister.Rsi]);
        var waitingThreadsAddress = ctx[CpuRegister.Rdx];

        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (setCount > semaphore.MaxCount)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (semaphore.Gate)
        {
            if (waitingThreadsAddress != 0 &&
                !KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, waitingThreadsAddress, unchecked((uint)semaphore.WaitingThreads)))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            semaphore.Count = setCount < 0 ? semaphore.InitialCount : setCount;
            semaphore.CancelEpoch++;
            // WaitingThreads is NOT zeroed here: each canceled waiter decrements it
            // exactly once in its wake handler. Zeroing here as well would double-count
            // and silently absorb the increment of a waiter that parks between this
            // gate release and the wake-all below.
            TraceSemaphore($"cancel handle=0x{handle:X8} name='{semaphore.Name}' set={setCount} count={semaphore.Count}");
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetSemaphoreWakeKey(handle));
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "R1Jvn8bSCW8",
        ExportName = "sceKernelDeleteSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!_semaphores.TryRemove(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        // Delete succeeds even with blocked waiters; they wake with the deleted
        // result (the SCE kernel wakes waiters with the EACCES-class code).
        lock (semaphore.Gate)
        {
            semaphore.Deleted = true;
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetSemaphoreWakeKey(handle));
        TraceSemaphore($"delete handle=0x{handle:X8} name='{semaphore.Name}'");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "pDuPEf3m4fI",
        ExportName = "sem_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemInit(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        var initialCountValue = ctx[CpuRegister.Rdx];
        if (semaphoreAddress == 0 || initialCountValue > int.MaxValue)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        if (handle == 0)
        {
            handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        }

        var initialCount = unchecked((int)initialCountValue);
        var state = new KernelSemaphoreState
        {
            Name = $"posix@0x{semaphoreAddress:X16}",
            InitialCount = initialCount,
            MaxCount = int.MaxValue,
            Count = initialCount,
        };
        _semaphores[handle] = state;
        if (!KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, semaphoreAddress, handle))
        {
            _semaphores.TryRemove(handle, out _);
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceSemaphore($"posix-init address=0x{semaphoreAddress:X16} handle=0x{handle:X8} count={initialCount}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "-wUggz2S5yk",
        ExportName = "sem_setname",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemSetName(CpuContext ctx)
    {
        // FreeBSD semaphore names are diagnostic metadata. Validate the guest
        // object and name, but retain the immutable internal label used by the
        // scheduler so wake keys cannot change while waiters are blocked.
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        if (!TryGetPosixSemaphoreHandle(ctx, semaphoreAddress, out var handle) ||
            !_semaphores.ContainsKey(handle) ||
            nameAddress == 0 ||
            !ctx.TryReadNullTerminatedUtf8(nameAddress, MaxSemaphoreNameLength, out _))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "YCV5dGGBcCo",
        ExportName = "sem_wait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemWait(CpuContext ctx)
    {
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 0;
        return KernelWaitSema(ctx);
    }

    [SysAbiExport(
        Nid = "WBWzsRifCEA",
        ExportName = "sem_trywait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemTryWait(CpuContext ctx)
    {
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle))
        {
            KernelRuntimeCompatExports.TrySetErrno(ctx, 22);
            ctx[CpuRegister.Rax] = ulong.MaxValue;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        var result = KernelPollSema(ctx);
        if (result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        KernelRuntimeCompatExports.TrySetErrno(
            ctx,
            result == (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY ? 35 : 22);
        ctx[CpuRegister.Rax] = ulong.MaxValue;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "w5IHyvahg-o",
        ExportName = "sem_timedwait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemTimedWait(CpuContext ctx)
    {
        var timeoutAddress = ctx[CpuRegister.Rsi];
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle) ||
            !_semaphores.TryGetValue(handle, out var semaphore) ||
            timeoutAddress == 0 ||
            !ctx.TryReadUInt64(timeoutAddress, out var secondsValue) ||
            !ctx.TryReadUInt64(timeoutAddress + sizeof(long), out var nanosecondsValue))
        {
            return ReturnPosixSemaphoreError(ctx, 22);
        }

        var seconds = unchecked((long)secondsValue);
        var nanoseconds = unchecked((long)nanosecondsValue);
        if (seconds < 0 || nanoseconds is < 0 or >= 1_000_000_000L ||
            seconds > (long.MaxValue - nanoseconds / 1_000_000L) / 1000L)
        {
            return ReturnPosixSemaphoreError(ctx, 22);
        }

        var deadlineMilliseconds = seconds * 1000L + nanoseconds / 1_000_000L;
        var remainingMilliseconds = deadlineMilliseconds - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (remainingMilliseconds <= 0)
        {
            return ReturnPosixSemaphoreError(ctx, 60);
        }

        var maximumTimeoutMilliseconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;
        var timeoutTicks = remainingMilliseconds >= maximumTimeoutMilliseconds
            ? TimeSpan.MaxValue.Ticks
            : remainingMilliseconds * TimeSpan.TicksPerMillisecond;
        var deadlineTimestamp = GuestThreadExecution.ComputeDeadlineTimestamp(TimeSpan.FromTicks(timeoutTicks));
        var waiter = new SemaphoreWaiter
        {
            NeedCount = 1,
            CancelEpochAtBlock = semaphore.CancelEpoch,
            Timed = true,
            TimeoutAddress = 0,
            DeadlineTimestamp = deadlineTimestamp,
        };

        lock (semaphore.Gate)
        {
            if (semaphore.Deleted)
            {
                return ReturnPosixSemaphoreError(ctx, 22);
            }

            if (semaphore.Count > 0)
            {
                semaphore.Count--;
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            // Mono enters its GC-safe BLOCKING state before calling sem_timedwait.
            // Yield guest workers through the continuation scheduler so Mono can
            // execute DONE_BLOCKING after the import returns. Pumping other guest
            // work from inside this import leaves the worker's Mono state parked
            // in BLOCKING and trips the next managed magic trampoline.
            if (GuestThreadExecution.RequestCurrentThreadBlock(
                    ctx,
                    "sem_timedwait",
                    GetSemaphoreWakeKey(handle),
                    resumeHandler: () => CompleteBlockedPosixSemaWait(ctx, semaphore, waiter),
                    wakeHandler: () => TryConsumeBlockedSemaWait(semaphore, waiter),
                    blockDeadlineTimestamp: deadlineTimestamp))
            {
                semaphore.WaitingThreads++;
                TraceSemaphore($"posix-timedwait-block handle=0x{handle:X8} name='{semaphore.Name}' waiters={semaphore.WaitingThreads}");
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }
        }

        // The primary executor has no resumable guest-thread continuation. Keep
        // its producer/consumer fallback, but guest workers always take the safe
        // scheduler path above.
        while (true)
        {
            lock (semaphore.Gate)
            {
                if (semaphore.Deleted)
                {
                    return ReturnPosixSemaphoreError(ctx, 22);
                }

                if (semaphore.Count > 0)
                {
                    semaphore.Count--;
                    ctx[CpuRegister.Rax] = 0;
                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                }
            }

            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadlineMilliseconds)
            {
                // FreeBSD/Orbis ETIMEDOUT.
                return ReturnPosixSemaphoreError(ctx, 60);
            }

            GuestThreadExecution.Scheduler?.Pump(ctx, "sem_timedwait");
            Thread.Sleep(1);
        }
    }

    [SysAbiExport(
        Nid = "IKP8typ0QUk",
        ExportName = "sem_post",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemPost(CpuContext ctx)
    {
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        return KernelSignalSema(ctx);
    }

    [SysAbiExport(
        Nid = "Bq+LRV-N6Hk",
        ExportName = "sem_getvalue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemGetValue(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        var valueAddress = ctx[CpuRegister.Rsi];
        if (valueAddress == 0 || !TryGetPosixSemaphoreHandle(ctx, semaphoreAddress, out var handle) ||
            !_semaphores.TryGetValue(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        int count;
        lock (semaphore.Gate)
        {
            count = semaphore.Count;
        }

        return KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, valueAddress, unchecked((uint)count))
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "cDW233RAwWo",
        ExportName = "sem_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemDestroy(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        if (!TryGetPosixSemaphoreHandle(ctx, semaphoreAddress, out var handle))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        var result = KernelDeleteSema(ctx);
        if (result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            _ = KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, semaphoreAddress, 0);
        }

        return result;
    }

    private static bool TryGetPosixSemaphoreHandle(CpuContext ctx, ulong semaphoreAddress, out uint handle)
    {
        handle = 0;
        return semaphoreAddress != 0 &&
               KernelMemoryCompatExports.TryReadUInt32Compat(ctx, semaphoreAddress, out handle) &&
               handle != 0;
    }

    private static int ReturnPosixSemaphoreError(CpuContext ctx, int error)
    {
        KernelRuntimeCompatExports.TrySetErrno(ctx, error);
        ctx[CpuRegister.Rax] = ulong.MaxValue;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Wake handler: runs under the scheduler's guest-thread gate (lock order:
    // scheduler gate -> semaphore gate). Returns true iff the waiter has a final
    // result and should be re-readied; false leaves it parked.
    private static bool TryConsumeBlockedSemaWait(KernelSemaphoreState semaphore, SemaphoreWaiter waiter)
    {
        lock (semaphore.Gate)
        {
            return TryConsumeBlockedSemaWaitLocked(semaphore, waiter);
        }
    }

    private static bool TryConsumeBlockedSemaWaitLocked(KernelSemaphoreState semaphore, SemaphoreWaiter waiter)
    {
        if (waiter.Result is not null)
        {
            return true;
        }

        if (semaphore.Deleted)
        {
            waiter.Result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED;
            semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
            TraceSemaphore($"wake-deleted name='{semaphore.Name}' need={waiter.NeedCount}");
            return true;
        }

        if (semaphore.CancelEpoch != waiter.CancelEpochAtBlock)
        {
            waiter.Result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED;
            semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
            TraceSemaphore($"wake-canceled name='{semaphore.Name}' need={waiter.NeedCount}");
            return true;
        }

        if (semaphore.Count >= waiter.NeedCount)
        {
            semaphore.Count -= waiter.NeedCount;
            waiter.Result = (int)OrbisGen2Result.ORBIS_GEN2_OK;
            semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
            TraceSemaphore($"wake-consume name='{semaphore.Name}' need={waiter.NeedCount} count={semaphore.Count} waiters={semaphore.WaitingThreads}");
            return true;
        }

        return false;
    }

    // Resume handler: runs on the woken guest thread outside the scheduler gate;
    // its return value becomes the guest's RAX for the resumed sceKernelWaitSema.
    private static int CompleteBlockedSemaWait(CpuContext ctx, KernelSemaphoreState semaphore, SemaphoreWaiter waiter)
    {
        lock (semaphore.Gate)
        {
            if (waiter.Result is null && !TryConsumeBlockedSemaWaitLocked(semaphore, waiter))
            {
                waiter.Result = waiter.Timed
                    ? (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT
                    : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN;
                semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
                TraceSemaphore(
                    waiter.Timed
                        ? $"wake-timeout name='{semaphore.Name}' need={waiter.NeedCount} count={semaphore.Count}"
                        : $"resume-no-outcome name='{semaphore.Name}' need={waiter.NeedCount} count={semaphore.Count}");
            }

            if (waiter.TimeoutAddress != 0)
            {
                _ = KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, waiter.TimeoutAddress, 0);
            }

            return waiter.Result!.Value;
        }
    }

    private static int CompleteBlockedPosixSemaWait(
        CpuContext ctx,
        KernelSemaphoreState semaphore,
        SemaphoreWaiter waiter)
    {
        lock (semaphore.Gate)
        {
            if (waiter.Result is null && !TryConsumeBlockedSemaWaitLocked(semaphore, waiter))
            {
                waiter.Result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
                semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
                TraceSemaphore($"posix-timedwait-timeout name='{semaphore.Name}' count={semaphore.Count}");
            }

            if (waiter.Result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
            {
                return 0;
            }

            var errno = waiter.Result == (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT ? 60 : 22;
            KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
            return -1;
        }
    }

    private static void TraceSemaphore(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SEMA"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] sema.{message}");
        }
    }
}
