// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Fiber;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;

namespace SharpEmu.Libs.Kernel;

public static class KernelRuntimeCompatExports
{
    internal const int ClockRealtime = 0;
    internal const int ClockVirtual = 1;
    internal const int ClockProf = 2;
    internal const int ClockMonotonic = 4;
    internal const int ClockUptime = 5;
    internal const int ClockUptimePrecise = 7;
    internal const int ClockUptimeFast = 8;
    internal const int ClockRealtimePrecise = 9;
    internal const int ClockRealtimeFast = 10;
    internal const int ClockMonotonicPrecise = 11;
    internal const int ClockMonotonicFast = 12;
    internal const int ClockSecond = 13;
    internal const int ClockThreadCputimeId = 14;
    internal const int ClockProcTime = 15;
    private const int Efault = 14;
    private const int Einval = 22;
    private const ulong TlsErrnoOffset = 0x40;
    private const ulong TlsStackChkGuardBaseOffset = 0x800;
    private const ulong StackChkGuardFieldOffset = 0x10;
    private const ulong TlsProcParamOffset = 0x100;
    private const ulong TlsMallocReplaceOffset = 0x200;
    private const ulong TlsNewReplaceOffset = 0x300;
    private const int MallocReplaceSize = 0x70;
    private const int NewReplaceSize = 0x68;
    private const int OrbisTimesecSize = sizeof(long) + sizeof(uint) + sizeof(uint);
    private const ulong ModuleInfoHandleOffset = 0x108;
    private const ulong ModuleInfoNameOffset = 0x10;
    private const ulong ModuleInfoEhFrameHeaderOffset = 0x148;
    private const int ModuleInfoNameMaxBytes = 64;
    private const ulong DefaultKernelTscFrequency = 10_000_000UL;
    private const ulong PrtAreaStartAddress = 0x0000001000000000UL;
    private const ulong PrtAreaSize = 0x000000EC00000000UL;
    private const int MapFlagFixed = 0x10;
    private const ulong DefaultVirtualRangeAlignment = 0x4000UL;
    private const int AioInitParamSize = 0x3C;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint PageExecuteReadWrite = 0x40;
    private static readonly object _stateGate = new();
    private static readonly long _processStartCounter = Stopwatch.GetTimestamp();
    private static readonly RdtscDelegate? _rdtscReader = CreateRdtscReader();
    private static readonly ulong _kernelTscFrequency = ResolveKernelTscFrequency();
    private static readonly ulong _stackChkGuardValue = 0xC0DEC0DECAFEBABEUL;
    private static readonly nint _stackChkGuardObjectAddress =
        HleDataSymbols.TryGetAddress("f7uOxY9mM1U", out var stackChkGuardAddress)
            ? unchecked((nint)stackChkGuardAddress)
            : AllocateStackChkGuardObject();
    private static ulong _applicationHeapApiAddress;
    private static ulong _processProcParamAddress;
    private static ulong _nextReservedVirtualBase = 0x6000_0000_0UL;
    private static uint _gpoStateBits;
    private static readonly HashSet<int> _loadedSysmodules = new();
    private static readonly object _prtApertureGate = new();
    private static readonly (ulong Base, ulong Size)[] _prtApertures = new (ulong Base, ulong Size)[3];
    private static int _stackChkFailCount;
    private static long _usleepTraceCount;
    private static readonly bool _traceUsleep =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_USLEEP"), "1", StringComparison.Ordinal);
    private static readonly bool _traceGuestThreads =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_GUEST_THREADS"), "1", StringComparison.Ordinal);

    [ThreadStatic]
    private static int _shortUsleepCount;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong RdtscDelegate();

    [SysAbiExport(
        Nid = "QvsZxomvUHs",
        ExportName = "sceKernelNanosleep",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelNanosleep(CpuContext ctx)
    {
        var requestAddress = ctx[CpuRegister.Rdi];
        var remainAddress = ctx[CpuRegister.Rsi];

        if (requestAddress == 0)
        {
            ctx[CpuRegister.Rax] = Einval;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        Span<byte> timespecBuffer = stackalloc byte[16];
        if (!ctx.Memory.TryRead(requestAddress, timespecBuffer))
        {
            ctx[CpuRegister.Rax] = Efault;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var tvSec = BinaryPrimitives.ReadInt64LittleEndian(timespecBuffer);
        var tvNsec = BinaryPrimitives.ReadInt64LittleEndian(timespecBuffer[sizeof(long)..]);

        if (tvSec < 0 || tvNsec < 0 || tvNsec >= 1_000_000_000L)
        {
            ctx[CpuRegister.Rax] = Einval;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (tvSec == 0 && tvNsec == 0)
        {
            WriteRemainingTime(ctx, remainAddress, 0, 0);
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        GuestThreadExecution.Scheduler?.Pump(ctx, "sceKernelNanosleep");

        // TimeSpan resolution is 100 ns ticks, so sub-100 ns requests round up to
        // a single tick rather than collapsing to a zero-length (no-op) sleep.
        var totalTicks = tvSec * TimeSpan.TicksPerSecond + Math.Max(tvNsec / 100L, 1L);
        try
        {
            Thread.Sleep(TimeSpan.FromTicks(totalTicks));
        }
        catch (ArgumentOutOfRangeException)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(int.MaxValue));
        }

        WriteRemainingTime(ctx, remainAddress, 0, 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "yS8U2TGCe1A",
        ExportName = "nanosleep",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixNanosleep(CpuContext ctx) => KernelNanosleep(ctx);

    [SysAbiExport(
        Nid = "6XG4B33N09g",
        ExportName = "sched_yield",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSchedYield(CpuContext ctx)
    {
        GuestThreadExecution.Scheduler?.Pump(ctx, "sched_yield");
        _ = Thread.Yield();
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static void WriteRemainingTime(CpuContext ctx, ulong remainAddress, long seconds, long nanoseconds)
    {
        if (remainAddress == 0)
        {
            return;
        }

        Span<byte> remainBuffer = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(remainBuffer, seconds);
        BinaryPrimitives.WriteInt64LittleEndian(remainBuffer[sizeof(long)..], nanoseconds);
        ctx.Memory.TryWrite(remainAddress, remainBuffer);
    }

    [SysAbiExport(
        Nid = "-ZR+hG7aDHw",
        ExportName = "sceKernelSleep",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSleep(CpuContext ctx)
    {
        var seconds = ctx[CpuRegister.Rdi];
        if (seconds == 0)
        {
            Thread.Yield();
        }
        else
        {
            GuestThreadExecution.Scheduler?.Pump(ctx, "sceKernelSleep");
            var milliseconds = seconds > (ulong)int.MaxValue / 1000UL
                ? (ulong)int.MaxValue
                : seconds * 1000UL;
            Thread.Sleep(unchecked((int)milliseconds));
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "1jfXLRVzisc",
        ExportName = "sceKernelUsleep",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelUsleep(CpuContext ctx)
    {
        var micros = ctx[CpuRegister.Rdi];
        TraceUsleepSpin(ctx, micros);
        if (micros == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        GuestThreadExecution.Scheduler?.Pump(ctx, "sceKernelUsleep");

        if (micros < 1000)
        {
            // Guest worker pools use usleep(1) as a polling backoff. Do not turn
            // PS microsecond waits into Windows millisecond sleeps on hot paths.
            if ((++_shortUsleepCount & 255) == 0)
            {
                Thread.Sleep(0);
            }
            else
            {
                Thread.Yield();
            }
        }
        else
        {
            _shortUsleepCount = 0;
            var sleepMilliseconds = (int)Math.Min((micros + 999UL) / 1000UL, int.MaxValue);
            Thread.Sleep(sleepMilliseconds);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static void TraceUsleepSpin(CpuContext ctx, ulong micros)
    {
        if (!_traceUsleep)
        {
            return;
        }

        var count = Interlocked.Increment(ref _usleepTraceCount);
        if (count > 32 && count % 10000 != 0)
        {
            return;
        }

        var rbx = ctx[CpuRegister.Rbx];
        var r12 = ctx[CpuRegister.R12];
        var r13 = ctx[CpuRegister.R13];
        var lockAddress = rbx == 0 ? 0 : rbx + 0xF78;
        var lockText = "unreadable";
        if (lockAddress != 0 && ctx.TryReadUInt64(lockAddress, out var lockValue))
        {
            lockText = $"0x{lockValue:X16}";
        }

        var schedulerText = "unreadable";
        if (r12 != 0 && ctx.TryReadUInt64(r12 + 8, out var schedulerAddress))
        {
            schedulerText = $"0x{schedulerAddress:X16}";
        }

        var waitValueText = "unreadable";
        if (r13 != 0 && ctx.TryReadUInt64(r13, out var waitValue))
        {
            waitValueText = $"0x{waitValue:X16}";
        }

        var callerReturnText = "unreadable";
        var rbp = ctx[CpuRegister.Rbp];
        if (rbp != 0 && ctx.TryReadUInt64(rbp + 8, out var callerReturn))
        {
            callerReturnText = $"0x{callerReturn:X16}";
        }

        var returnRip = GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame)
            ? frame.ReturnRip
            : 0UL;
        var thread = GuestThreadExecution.CurrentGuestThreadHandle;
        var fiber = FiberExports.GetCurrentFiberAddressForDiagnostics(ctx);

        Console.Error.WriteLine(
            $"[LOADER][TRACE] usleep#{count}: usec={micros} ret=0x{returnRip:X16} caller={callerReturnText} thread=0x{thread:X16} fiber=0x{fiber:X16} rbx=0x{rbx:X16} lock@+F78=0x{lockAddress:X16}:{lockText} r12=0x{r12:X16} scheduler@+8={schedulerText} r13=0x{r13:X16}:{waitValueText} r14=0x{ctx[CpuRegister.R14]:X16} r15=0x{ctx[CpuRegister.R15]:X16}");

        if (count % 100000 == 0 &&
            _traceGuestThreads &&
            GuestThreadExecution.Scheduler is { } scheduler)
        {
            foreach (var snapshot in scheduler.SnapshotThreads())
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] guest_thread.snapshot handle=0x{snapshot.ThreadHandle:X16} name='{snapshot.Name}' " +
                    $"state={snapshot.State} imports={snapshot.ImportCount} nid={snapshot.LastImportNid ?? "none"} " +
                    $"ret=0x{snapshot.LastReturnRip:X16} block={snapshot.BlockReason ?? "none"}");
            }
        }
    }

    [SysAbiExport(
        Nid = "QBi7HCK03hw",
        ExportName = "sceKernelClockGettime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelClockGettime(CpuContext ctx)
    {
        var clockId = unchecked((int)ctx[CpuRegister.Rdi]);
        var timeAddress = ctx[CpuRegister.Rsi];
        if (timeAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        long seconds;
        long nanoseconds;
        if (!ResolveClockTime(clockId, out seconds, out nanoseconds))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        Span<byte> timespecBuffer = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(timespecBuffer, seconds);
        BinaryPrimitives.WriteInt64LittleEndian(timespecBuffer[sizeof(long)..], nanoseconds);

        if (!ctx.Memory.TryWrite(timeAddress, timespecBuffer))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "ejekcaNQNq0",
        ExportName = "sceKernelGettimeofday",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGettimeofday(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        if (timeAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var now = DateTimeOffset.UtcNow;
        var seconds = now.ToUnixTimeSeconds();
        var microseconds = (now.Ticks % TimeSpan.TicksPerSecond) / 10;

        Span<byte> timevalBuffer = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(timevalBuffer, seconds);
        BinaryPrimitives.WriteInt64LittleEndian(timevalBuffer[sizeof(long)..], microseconds);
        if (!ctx.Memory.TryWrite(timeAddress, timevalBuffer))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "n88vx3C5nW8",
        ExportName = "gettimeofday",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGettimeofday(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        var timezoneAddress = ctx[CpuRegister.Rsi];
        var now = DateTimeOffset.UtcNow;
        var seconds = now.ToUnixTimeSeconds();
        var microseconds = (now.Ticks % TimeSpan.TicksPerSecond) / 10;

        if (timeAddress != 0)
        {
            Span<byte> timevalBuffer = stackalloc byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(timevalBuffer, seconds);
            BinaryPrimitives.WriteInt64LittleEndian(timevalBuffer[sizeof(long)..], microseconds);
            if (!ctx.Memory.TryWrite(timeAddress, timevalBuffer))
            {
                TrySetErrno(ctx, Efault);
                return -1;
            }
        }

        if (timezoneAddress != 0)
        {
            Span<byte> timezoneBuffer = stackalloc byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(timezoneBuffer, 0);
            BinaryPrimitives.WriteInt32LittleEndian(timezoneBuffer[sizeof(int)..], 0);
            if (!ctx.Memory.TryWrite(timezoneAddress, timezoneBuffer))
            {
                TrySetErrno(ctx, Efault);
                return -1;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "-2IRUCO--PM",
        ExportName = "sceKernelReadTsc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelReadTsc(CpuContext ctx)
    {
        if (TryReadHostTsc(out ulong counter))
        {
            ctx[CpuRegister.Rax] = counter;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var stopwatchTicks = Stopwatch.GetTimestamp();
        ctx[CpuRegister.Rax] = unchecked((ulong)Math.Max(0, stopwatchTicks));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "1j3S3n-tTW4",
        ExportName = "sceKernelGetTscFrequency",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetTscFrequency(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = _kernelTscFrequency;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "4J2sUJmuHZQ",
        ExportName = "sceKernelGetProcessTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetProcessTime(CpuContext ctx)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - _processStartCounter;
        var micros = elapsedTicks * 1_000_000L / Stopwatch.Frequency;
        ctx[CpuRegister.Rax] = unchecked((ulong)Math.Max(0, micros));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "fgxnMeTNUtY",
        ExportName = "sceKernelGetProcessTimeCounter",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetProcessTimeCounter(CpuContext ctx)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - _processStartCounter;
        ctx[CpuRegister.Rax] = unchecked((ulong)Math.Max(0, elapsedTicks));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "BNowx2l588E",
        ExportName = "sceKernelGetProcessTimeCounterFrequency",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetProcessTimeCounterFrequency(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)Stopwatch.Frequency);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "6xVpy0Fdq+I",
        ExportName = "_sigprocmask",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Sigprocmask(CpuContext ctx)
    {
        _ = ctx;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "959qrazPIrg",
        ExportName = "sceKernelGetProcParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetProcParam(CpuContext ctx)
    {
        ulong address;
        lock (_stateGate)
        {
            address = _processProcParamAddress;
        }

        if (address == 0)
        {
            address = GetTlsScratchAddress(ctx, TlsProcParamOffset);
        }

        if (address != 0)
        {
            if (address == GetTlsScratchAddress(ctx, TlsProcParamOffset))
            {
                _ = ctx.Memory.TryWrite(address, new byte[0x80]);
            }
        }

        TraceProcParam(ctx, address);

        ctx[CpuRegister.Rax] = address;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    public static void ConfigureProcessProcParamAddress(ulong procParamAddress)
    {
        lock (_stateGate)
        {
            _processProcParamAddress = procParamAddress;
        }
    }

    private static void TraceProcParam(CpuContext ctx, ulong address)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PROC_PARAM"), "1", StringComparison.Ordinal))
        {
            return;
        }

        if (address == 0)
        {
            Console.Error.WriteLine("[LOADER][TRACE] proc_param: address=0");
            return;
        }

        const int dumpSize = 0x200;
        var buffer = GC.AllocateUninitializedArray<byte>(dumpSize);
        if (!ctx.Memory.TryRead(address, buffer))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] proc_param: address=0x{address:X16} unreadable");
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] proc_param: address=0x{address:X16} size=0x{dumpSize:X}");
        for (var offset = 0; offset < dumpSize; offset += 16)
        {
            var slice = buffer.AsSpan(offset, 16);
            var hex = Convert.ToHexString(slice);
            Console.Error.WriteLine($"[LOADER][TRACE] proc_param[{offset:X3}]: {hex}");
        }

        TraceProcParamPointers(ctx, address, buffer);
    }

    private static void TraceProcParamPointers(CpuContext ctx, ulong baseAddress, ReadOnlySpan<byte> buffer)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PROC_PARAM_PTRS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        if (buffer.Length < 0x50)
        {
            return;
        }

        for (var offset = 0x20; offset <= 0x48; offset += 8)
        {
            var ptr = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset, 8));
            Console.Error.WriteLine($"[LOADER][TRACE] proc_param.ptr@{offset:X2}: 0x{ptr:X16}");
            if (ptr == 0)
            {
                continue;
            }

            TraceProcParamPointerTarget(ctx, ptr);
        }
    }

    private static void TraceProcParamPointerTarget(CpuContext ctx, ulong address)
    {
        const int maxAsciiBytes = 256;
        const int maxWideChars = 128;

        if (TryReadUtf8CString(ctx, address, maxAsciiBytes, out var asciiValue))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] proc_param.ptr.target ascii@0x{address:X16}: \"{asciiValue}\"");
            return;
        }

        if (TryReadUtf16CString(ctx, address, maxWideChars, out var wideValue))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] proc_param.ptr.target wide@0x{address:X16}: \"{wideValue}\"");
            return;
        }

        var preview = GC.AllocateUninitializedArray<byte>(64);
        if (ctx.Memory.TryRead(address, preview))
        {
            var hex = Convert.ToHexString(preview);
            Console.Error.WriteLine($"[LOADER][TRACE] proc_param.ptr.target hex@0x{address:X16}: {hex}");
            TraceProcParamEmbeddedPointers(ctx, address, preview);
        }
        else
        {
            Console.Error.WriteLine($"[LOADER][TRACE] proc_param.ptr.target unreadable@0x{address:X16}");
        }
    }

    private static bool TryReadUtf8CString(CpuContext ctx, ulong address, int maxBytes, out string value)
    {
        value = string.Empty;
        var buffer = GC.AllocateUninitializedArray<byte>(maxBytes);
        if (!ctx.Memory.TryRead(address, buffer))
        {
            return false;
        }

        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = maxBytes;
        }

        if (length == 0)
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, length);
        if (!IsMostlyPrintable(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryReadUtf16CString(CpuContext ctx, ulong address, int maxChars, out string value)
    {
        value = string.Empty;
        var maxBytes = maxChars * 2;
        var buffer = GC.AllocateUninitializedArray<byte>(maxBytes);
        if (!ctx.Memory.TryRead(address, buffer))
        {
            return false;
        }

        var lengthBytes = -1;
        for (var i = 0; i + 1 < buffer.Length; i += 2)
        {
            if (buffer[i] == 0 && buffer[i + 1] == 0)
            {
                lengthBytes = i;
                break;
            }
        }

        if (lengthBytes <= 0)
        {
            return false;
        }

        var text = Encoding.Unicode.GetString(buffer, 0, lengthBytes);
        if (!IsMostlyPrintable(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool IsMostlyPrintable(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var printable = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\0')
            {
                continue;
            }

            if (!char.IsControl(ch) || ch == '\r' || ch == '\n' || ch == '\t')
            {
                printable++;
            }
        }

        return printable >= Math.Max(4, text.Length * 3 / 4);
    }

    private static void TraceProcParamEmbeddedPointers(CpuContext ctx, ulong baseAddress, ReadOnlySpan<byte> data)
    {
        const int maxCandidates = 12;
        var found = 0;
        Span<byte> probe = stackalloc byte[2];

        for (var offset = 0; offset + 8 <= data.Length; offset += 8)
        {
            var candidate = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
            if (candidate == 0)
            {
                continue;
            }

            if (!ctx.Memory.TryRead(candidate, probe))
            {
                continue;
            }

            if (TryReadUtf8CString(ctx, candidate, 256, out var ascii))
            {
                Console.Error.WriteLine($"[LOADER][TRACE] proc_param.ptr.embed@0x{baseAddress:X16}+0x{offset:X2} -> 0x{candidate:X16} ascii \"{ascii}\"");
                if (++found >= maxCandidates)
                {
                    return;
                }
                continue;
            }

            if (TryReadUtf16CString(ctx, candidate, 128, out var wide))
            {
                Console.Error.WriteLine($"[LOADER][TRACE] proc_param.ptr.embed@0x{baseAddress:X16}+0x{offset:X2} -> 0x{candidate:X16} wide \"{wide}\"");
                if (++found >= maxCandidates)
                {
                    return;
                }
            }
        }
    }

    [SysAbiExport(
        Nid = "9BcDykPmo1I",
        ExportName = "__error",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int ErrorAddress(CpuContext ctx)
    {
        var address = GetTlsScratchAddress(ctx, TlsErrnoOffset);
        ctx[CpuRegister.Rax] = address;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    internal static bool TrySetErrno(CpuContext ctx, int value)
    {
        var address = GetTlsScratchAddress(ctx, TlsErrnoOffset);
        return address != 0 && ctx.TryWriteInt32(address, value);
    }

    internal static bool ResolveClockTime(int clockId, out long seconds, out long nanoseconds)
    {
        switch (clockId)
        {
            case ClockRealtime:
            case ClockRealtimePrecise:
            case ClockRealtimeFast:
            case ClockVirtual:
            case ClockProf:
            {
                var now = DateTimeOffset.UtcNow;
                seconds = now.ToUnixTimeSeconds();
                nanoseconds = (now.Ticks % TimeSpan.TicksPerSecond) * 100;
                return true;
            }

            // CLOCK_SECOND is FreeBSD's cached whole-second realtime clock (Quake's
            // audio_output_thread polls it and treated the previous EINVAL as a fatal
            // init failure, exiting and getting respawned in a loop).
            case ClockSecond:
                seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                nanoseconds = 0;
                return true;

            case ClockMonotonic:
            case ClockMonotonicPrecise:
            case ClockMonotonicFast:
            case ClockUptime:
            case ClockUptimePrecise:
            case ClockUptimeFast:
            // Per-thread/process CPU time approximated with the monotonic clock; games
            // use these for profiling deltas where monotonicity matters, not absolutes.
            case ClockThreadCputimeId:
            case ClockProcTime:
                GetProcessMonotonicTime(out seconds, out nanoseconds);
                return true;

            default:
                seconds = 0;
                nanoseconds = 0;
                return false;
        }
    }

    internal static void GetProcessMonotonicTime(out long seconds, out long nanoseconds)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - _processStartCounter;
        seconds = elapsedTicks / Stopwatch.Frequency;
        nanoseconds = (elapsedTicks % Stopwatch.Frequency) * 1_000_000_000L / Stopwatch.Frequency;
    }

    [SysAbiExport(
        Nid = "bnZxYgAFeA0",
        ExportName = "sceKernelGetSanitizerNewReplaceExternal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetSanitizerNewReplaceExternal(CpuContext ctx)
    {
        var address = GetTlsScratchAddress(ctx, TlsNewReplaceOffset);
        if (address != 0)
        {
            if (!ctx.Memory.TryWrite(address, new byte[NewReplaceSize]) ||
                !ctx.TryWriteUInt64(address, NewReplaceSize))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        ctx[CpuRegister.Rax] = address;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "py6L8jiVAN8",
        ExportName = "sceKernelGetSanitizerMallocReplaceExternal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetSanitizerMallocReplaceExternal(CpuContext ctx)
    {
        var address = GetTlsScratchAddress(ctx, TlsMallocReplaceOffset);
        if (address != 0)
        {
            if (!ctx.Memory.TryWrite(address, new byte[MallocReplaceSize]) ||
                !ctx.TryWriteUInt64(address, MallocReplaceSize))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        ctx[CpuRegister.Rax] = address;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "jh+8XiK4LeE",
        ExportName = "sceKernelIsAddressSanitizerEnabled",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelIsAddressSanitizerEnabled(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "ca7v6Cxulzs",
        ExportName = "sceKernelSetGPO",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSetGpo(CpuContext ctx)
    {
        _gpoStateBits = unchecked((uint)ctx[CpuRegister.Rdi]);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "4oXYe9Xmk0Q",
        ExportName = "sceKernelGetGPI",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetGpi(CpuContext ctx)
    {
        var bitMask = _gpoStateBits;
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_ALLOC_IMPORTS"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] get_gpi: mask=0x{bitMask:X8}");
        }
        ctx[CpuRegister.Rax] = bitMask;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "7oxv3PPCumo",
        ExportName = "sceKernelReserveVirtualRange",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelReserveVirtualRange(CpuContext ctx)
    {
        var inOutAddressPointer = ctx[CpuRegister.Rdi];
        var length = ctx[CpuRegister.Rsi];
        var flags = unchecked((int)ctx[CpuRegister.Rdx]);
        var alignment = ctx[CpuRegister.Rcx];
        if (inOutAddressPointer == 0 || length == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!ctx.TryReadUInt64(inOutAddressPointer, out var requestedAddress))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var effectiveAlignment = alignment == 0 ? DefaultVirtualRangeAlignment : alignment;
        var fixedMapping = (flags & MapFlagFixed) != 0;
        ulong desiredAddress;
        lock (_stateGate)
        {
            desiredAddress = requestedAddress != 0
                ? requestedAddress
                : AlignUp(_nextReservedVirtualBase, effectiveAlignment);
        }

        if (!TryReserveVirtualRange(ctx, desiredAddress, length, effectiveAlignment, allowSearch: !fixedMapping, out var mappedAddress))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (ShouldTraceVirtualMemory())
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] reserve_virtual_range: req=0x{requestedAddress:X16} desired=0x{desiredAddress:X16} mapped=0x{mappedAddress:X16} len=0x{length:X16} flags=0x{flags:X8} align=0x{effectiveAlignment:X16}");
        }

        if (!ctx.TryWriteUInt64(inOutAddressPointer, mappedAddress))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        lock (_stateGate)
        {
            _nextReservedVirtualBase = Math.Max(_nextReservedVirtualBase, mappedAddress + length);
        }

        KernelMemoryCompatExports.RegisterReservedVirtualRange(mappedAddress, length);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "BohYr-F7-is",
        ExportName = "sceKernelSetPrtAperture",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSetPrtAperture(CpuContext ctx)
    {
        var rawId = ctx[CpuRegister.Rdi];
        var apertureBase = ctx[CpuRegister.Rsi];
        var apertureSize = ctx[CpuRegister.Rdx];
        var apertureId = unchecked((int)rawId);

        if (apertureId < 0 || apertureId >= _prtApertures.Length)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if ((apertureBase & 0xFFFUL) != 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (apertureBase < PrtAreaStartAddress)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (apertureSize > PrtAreaSize)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (apertureBase - PrtAreaStartAddress > PrtAreaSize - apertureSize)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_prtApertureGate)
        {
            _prtApertures[apertureId] = (apertureBase, apertureSize);
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] set_prt_aperture: id={apertureId} base=0x{apertureBase:X16} size=0x{apertureSize:X16}");
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_ALLOC_IMPORTS"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] set_prt_aperture raw: rdi=0x{rawId:X16} rsi=0x{apertureBase:X16} rdx=0x{apertureSize:X16} rcx=0x{ctx[CpuRegister.Rcx]:X16} r8=0x{ctx[CpuRegister.R8]:X16} r9=0x{ctx[CpuRegister.R9]:X16}");
        }
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "f7KBOafysXo",
        ExportName = "sceKernelGetModuleInfoFromAddr",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetModuleInfoFromAddr(CpuContext ctx)
    {
        var queriedAddress = ctx[CpuRegister.Rdi];
        _ = ctx[CpuRegister.Rsi]; // mode
        var outInfoAddress = ctx[CpuRegister.Rdx];
        if (outInfoAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var moduleHandle = ResolveModuleHandleByAddress(queriedAddress);
        if (!ctx.TryWriteInt32(outInfoAddress + ModuleInfoHandleOffset, moduleHandle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (KernelModuleRegistry.TryGetModuleByHandle(moduleHandle, out var module))
        {
            _ = TryWriteModuleName(ctx, outInfoAddress, module.Name);
            if (!ctx.TryWriteUInt64(outInfoAddress + ModuleInfoEhFrameHeaderOffset, module.EhFrameHeaderAddress))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "RpQJJVKTiFM",
        ExportName = "sceKernelGetModuleInfoForUnwind",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetModuleInfoForUnwind(CpuContext ctx)
    {
        var queriedAddress = ctx[CpuRegister.Rdi];
        _ = ctx[CpuRegister.Rsi]; // flags
        var outInfoAddress = ctx[CpuRegister.Rdx];
        if (outInfoAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var moduleHandle = ResolveModuleHandleByAddress(queriedAddress);
        if (!ctx.TryWriteInt32(outInfoAddress + ModuleInfoHandleOffset, moduleHandle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (KernelModuleRegistry.TryGetModuleByHandle(moduleHandle, out var module))
        {
            _ = TryWriteModuleName(ctx, outInfoAddress, module.Name);
            if (!ctx.TryWriteUInt64(outInfoAddress + ModuleInfoEhFrameHeaderOffset, module.EhFrameHeaderAddress))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "kUpgrXIrz7Q",
        ExportName = "sceKernelGetModuleInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetModuleInfo(CpuContext ctx)
    {
        return KernelGetModuleInfoByHandleCore(
            ctx,
            unchecked((int)ctx[CpuRegister.Rdi]),
            ctx[CpuRegister.Rsi]);
    }

    [SysAbiExport(
        Nid = "QgsKEUfkqMA",
        ExportName = "sceKernelGetModuleInfo2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetModuleInfo2(CpuContext ctx)
    {
        return KernelGetModuleInfoByHandleCore(
            ctx,
            unchecked((int)ctx[CpuRegister.Rdi]),
            ctx[CpuRegister.Rsi]);
    }

    [SysAbiExport(
        Nid = "HZO7xOos4xc",
        ExportName = "sceKernelGetModuleInfoInternal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetModuleInfoInternal(CpuContext ctx)
    {
        return KernelGetModuleInfoByHandleCore(
            ctx,
            unchecked((int)ctx[CpuRegister.Rdi]),
            ctx[CpuRegister.Rsi]);
    }

    [SysAbiExport(
        Nid = "IuxnUuXk6Bg",
        ExportName = "sceKernelGetModuleList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetModuleList(CpuContext ctx)
    {
        return KernelGetModuleListCore(
            ctx,
            handlesAddress: ctx[CpuRegister.Rdi],
            capacity: ctx[CpuRegister.Rsi],
            outCountAddress: ctx[CpuRegister.Rdx],
            includeSystemModules: true);
    }

    [SysAbiExport(
        Nid = "ZzzC3ZGVAkc",
        ExportName = "sceKernelGetModuleList2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetModuleList2(CpuContext ctx)
    {
        return KernelGetModuleListCore(
            ctx,
            handlesAddress: ctx[CpuRegister.Rdi],
            capacity: ctx[CpuRegister.Rsi],
            outCountAddress: ctx[CpuRegister.Rdx],
            includeSystemModules: false);
    }

    [SysAbiExport(
        Nid = "Fjc4-n1+y2g",
        ExportName = "__elf_phdr_match_addr",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int ElfPhdrMatchAddr(CpuContext ctx)
    {
        var moduleInfoAddress = ctx[CpuRegister.Rdi];
        _ = ctx[CpuRegister.Rsi]; // dtor virtual address
        if (moduleInfoAddress == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        ctx[CpuRegister.Rax] = 1;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "OMDRKKAZ8I4",
        ExportName = "sceKernelDebugRaiseException",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDebugRaiseException(CpuContext ctx)
    {
        _ = ctx;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "zE-wXIZjLoM",
        ExportName = "sceKernelDebugRaiseExceptionOnReleaseMode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDebugRaiseExceptionOnReleaseMode(CpuContext ctx)
    {
        _ = ctx;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "f7uOxY9mM1U",
        ExportName = "__stack_chk_guard",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int StackCheckGuard(CpuContext ctx)
    {
        var baseAddress = _stackChkGuardObjectAddress != 0
            ? unchecked((ulong)_stackChkGuardObjectAddress)
            : GetTlsScratchAddress(ctx, TlsStackChkGuardBaseOffset);
        if (baseAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (_stackChkGuardObjectAddress != 0)
        {
            try
            {
                Marshal.WriteInt64(_stackChkGuardObjectAddress, unchecked((long)_stackChkGuardValue));
                Marshal.WriteInt64(IntPtr.Add(_stackChkGuardObjectAddress, (int)sizeof(ulong)), unchecked((long)_stackChkGuardValue));
            }
            catch
            {
            }
        }

        if (ctx.FsBase != 0)
        {
            _ = ctx.TryWriteUInt64(ctx.FsBase + 0x28, _stackChkGuardValue);
            _ = ctx.TryWriteUInt64(ctx.FsBase + TlsStackChkGuardBaseOffset, _stackChkGuardValue);
            _ = ctx.TryWriteUInt64(ctx.FsBase + TlsStackChkGuardBaseOffset + StackChkGuardFieldOffset, _stackChkGuardValue);
        }

        ctx[CpuRegister.Rax] = baseAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Ou3iL1abvng",
        ExportName = "__stack_chk_fail",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int StackCheckFail(CpuContext ctx)
    {
        var count = Interlocked.Increment(ref _stackChkFailCount);
        Console.Error.WriteLine(
            $"[LOADER][ERROR] __stack_chk_fail#{count}: rip=0x{ctx.Rip:X16} rdi=0x{ctx[CpuRegister.Rdi]:X16}");
        var result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP;
        GuestThreadExecution.RequestCurrentEntryExit("__stack_chk_fail", result);
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    [SysAbiExport(
        Nid = "p5EcQeEeJAE",
        ExportName = "_sceKernelRtldSetApplicationHeapAPI",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelRtldSetApplicationHeapApi(CpuContext ctx)
    {
        lock (_stateGate)
        {
            _applicationHeapApiAddress = ctx[CpuRegister.Rdi];
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "QKd0qM58Qes",
        ExportName = "sceKernelStopUnloadModule",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelStopUnloadModule(CpuContext ctx)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_MODULE_LOAD"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] sceKernelStopUnloadModule handle=0x{ctx[CpuRegister.Rdi]:X}");
        }
        var resultAddress = ctx[CpuRegister.R9];
        if (resultAddress != 0 && !ctx.TryWriteInt32(resultAddress, 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "wzvqT4UqKX8",
        ExportName = "sceKernelLoadStartModule",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelLoadStartModule(CpuContext ctx)
    {
        var modulePathAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.R9];
        if (resultAddress != 0 && !ctx.TryWriteInt32(resultAddress, 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        int handle = 0;
        if (TryReadUtf8Z(ctx, modulePathAddress, 512, out var modulePath) &&
            KernelModuleRegistry.TryFindByPathOrName(modulePath, out var moduleByPath))
        {
            handle = moduleByPath.Handle;
        }
        else if (!string.IsNullOrWhiteSpace(modulePath))
        {
            handle = KernelModuleRegistry.RegisterSyntheticModule(
                Path.GetFileName(modulePath),
                isSystemModule: false);
        }
        else if (KernelModuleRegistry.TryGetFirstModule(out var firstModule))
        {
            handle = firstModule.Handle;
        }
        else
        {
            handle = KernelModuleRegistry.RegisterSyntheticModule("module.sprx", isSystemModule: false);
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_MODULE_LOAD"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] sceKernelLoadStartModule path='{modulePath}' -> handle=0x{handle:X}");
            if (modulePath.Contains('\uFFFD'))
            {
                Span<byte> pathPreview = stackalloc byte[64];
                var preview = ctx.Memory.TryRead(modulePathAddress, pathPreview)
                    ? Convert.ToHexString(pathPreview)
                    : "<unreadable>";
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] malformed module path address=0x{modulePathAddress:X16} bytes={preview}");
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] module args rsi=0x{ctx[CpuRegister.Rsi]:X16} rdx=0x{ctx[CpuRegister.Rdx]:X16} rcx=0x{ctx[CpuRegister.Rcx]:X16} r8=0x{ctx[CpuRegister.R8]:X16} r9=0x{ctx[CpuRegister.R9]:X16}");
            }
        }

        ctx[CpuRegister.Rax] = unchecked((uint)handle);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Y1nEpkCieOY",
        ExportName = "sceKernelLoadStartModuleInternalForMono",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelLoadStartModuleInternalForMono(CpuContext ctx)
    {
        // The PSM Mono loader uses the same six-argument ABI as
        // sceKernelLoadStartModule, but requests an internal load mode. The
        // firmware AOT assemblies are preloaded beside libScePsm so their
        // exported dll_start/dll_size symbols are already available to dlsym.
        return KernelLoadStartModule(ctx);
    }

    [SysAbiExport(
        Nid = "g8cM39EUZ6o",
        ExportName = "sceSysmoduleLoadModule",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSysmodule")]
    public static int SysmoduleLoadModule(CpuContext ctx)
    {
        var moduleId = unchecked((int)ctx[CpuRegister.Rdi]);
        _ = KernelModuleRegistry.MarkSysmoduleLoaded(moduleId);
        lock (_stateGate)
        {
            _loadedSysmodules.Add(moduleId);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "39iV5E1HoCk",
        ExportName = "sceSysmoduleLoadModuleInternal",
        Target = Generation.Gen5,
        LibraryName = "libSceSysmodule")]
    public static int SysmoduleLoadModuleInternal(CpuContext ctx) => SysmoduleLoadModule(ctx);

    [SysAbiExport(
        Nid = "ynFKQ5bfGks",
        ExportName = "sceSysmoduleIsLoadedInternal",
        Target = Generation.Gen5,
        LibraryName = "libSceSysmodule")]
    public static int SysmoduleIsLoadedInternal(CpuContext ctx) => SysmoduleIsLoaded(ctx);

    [SysAbiExport(
        Nid = "k+AXqu2-eBc",
        ExportName = "getpagesize",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetPageSize(CpuContext ctx)
    {
        // Orbis/Prospero user processes use 16 KiB VM pages. Jemspace rejects
        // larger values before it creates Mono's local heap.
        ctx[CpuRegister.Rax] = 0x4000;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "mkawd0NA9ts",
        ExportName = "sysconf",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSysconf(CpuContext ctx)
    {
        // FreeBSD _SC_PAGESIZE is 47. Mono uses it to size the optional shared
        // metadata area and expects Prospero's 16 KiB user page size.
        if (unchecked((int)ctx[CpuRegister.Rdi]) == 47)
        {
            ctx[CpuRegister.Rax] = 0x4000;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        TrySetErrno(ctx, Einval);
        ctx[CpuRegister.Rax] = ulong.MaxValue;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "DFmMT80xcNI",
        ExportName = "sysctl",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSysctl(CpuContext ctx)
    {
        var oldLengthAddress = ctx[CpuRegister.Rcx];
        if (oldLengthAddress == 0 || !ctx.TryWriteUInt64(oldLengthAddress, 0))
        {
            TrySetErrno(ctx, Efault);
            ctx[CpuRegister.Rax] = ulong.MaxValue;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // The Mono startup query enumerates process records only to discover
        // other runtimes sharing its optional metadata area. Returning an empty
        // result preserves the ABI while keeping this emulated process isolated.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "MhC53TKmjVA",
        ExportName = "sysctlbyname",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSysctlByName(CpuContext ctx)
    {
        const int enoent = 2;
        var nameAddress = ctx[CpuRegister.Rdi];
        var oldAddress = ctx[CpuRegister.Rsi];
        var oldLengthAddress = ctx[CpuRegister.Rdx];

        if (nameAddress == 0 ||
            oldLengthAddress == 0 ||
            !TryReadUtf8CString(ctx, nameAddress, 256, out var name) ||
            !ctx.TryReadUInt64(oldLengthAddress, out var requestedLength))
        {
            TrySetErrno(ctx, Efault);
            ctx[CpuRegister.Rax] = ulong.MaxValue;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!string.Equals(name, "kern.rng_pseudo", StringComparison.Ordinal))
        {
            TrySetErrno(ctx, enoent);
            ctx[CpuRegister.Rax] = ulong.MaxValue;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (oldAddress == 0)
        {
            // The random sysctl is caller-sized. A length-only query therefore
            // preserves the requested size and succeeds.
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (requestedLength > 1024 * 1024)
        {
            TrySetErrno(ctx, Einval);
            ctx[CpuRegister.Rax] = ulong.MaxValue;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var randomBytes = new byte[(int)requestedLength];
        RandomNumberGenerator.Fill(randomBytes);
        if (!ctx.Memory.TryWrite(oldAddress, randomBytes) ||
            !ctx.TryWriteUInt64(oldLengthAddress, requestedLength))
        {
            TrySetErrno(ctx, Efault);
            ctx[CpuRegister.Rax] = ulong.MaxValue;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "QuJYZ2KVGGQ",
        ExportName = "shm_open",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSharedMemoryOpen(CpuContext ctx)
    {
        // POSIX shared objects are optional for Mono's shared metadata area. A
        // normal ENOSYS response selects its fully supported private allocation
        // path and avoids exposing host shared-memory namespaces to the guest.
        const int enosys = 78;
        TrySetErrno(ctx, enosys);
        ctx[CpuRegister.Rax] = ulong.MaxValue;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "ih4CD9-gghM",
        ExportName = "ftruncate",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixFileTruncate(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "tPWsbOUGO8k",
        ExportName = "shm_unlink",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSharedMemoryUnlink(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "BPE9s9vQQXo",
        ExportName = "mmap",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcMmap(CpuContext ctx)
    {
        var requestedSize = ctx[CpuRegister.Rsi];
        if (requestedSize == 0 ||
            ctx.Memory is not IGuestMemoryAllocator allocator ||
            !allocator.TryAllocateGuestMemory(requestedSize, alignment: 0x4000, out var mappedAddress))
        {
            ctx[CpuRegister.Rax] = ulong.MaxValue;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        ctx[CpuRegister.Rax] = mappedAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "UqDGjXA5yUM",
        ExportName = "munmap",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcMunmap(CpuContext ctx)
    {
        // Anonymous libc mappings come from SharpEmu's monotonic guest arena,
        // which cannot currently release individual spans. The address becomes
        // logically unmapped to the caller even though backing is retained for
        // the lifetime of this emulation process.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "SfQIZcqvvms",
        ExportName = "strncpy",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcStrncpy(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var sourceAddress = ctx[CpuRegister.Rsi];
        var count = ctx[CpuRegister.Rdx];
        if (destinationAddress == 0 || sourceAddress == 0 || count > int.MaxValue)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var copied = new byte[(int)count];
        if (count != 0)
        {
            var source = GC.AllocateUninitializedArray<byte>((int)count);
            if (!ctx.Memory.TryRead(sourceAddress, source))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var terminator = Array.IndexOf(source, (byte)0);
            var copyLength = terminator < 0 ? source.Length : terminator;
            source.AsSpan(0, copyLength).CopyTo(copied);
            if (!ctx.Memory.TryWrite(destinationAddress, copied))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        ctx[CpuRegister.Rax] = destinationAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "YNzNkJzYqEg",
        ExportName = "strncpy_s",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcStrncpySecure(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var destinationSize = ctx[CpuRegister.Rsi];
        var sourceAddress = ctx[CpuRegister.Rdx];
        var count = ctx[CpuRegister.Rcx];
        if (destinationAddress == 0 || destinationSize == 0 || sourceAddress == 0 ||
            destinationSize > int.MaxValue || count > int.MaxValue)
        {
            ctx[CpuRegister.Rax] = Einval;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var capacity = checked((int)destinationSize);
        var sourceLimit = checked((int)Math.Min(count, destinationSize - 1));
        var source = GC.AllocateUninitializedArray<byte>(sourceLimit + 1);
        if (!ctx.Memory.TryRead(sourceAddress, source))
        {
            ctx[CpuRegister.Rax] = Efault;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var terminator = Array.IndexOf(source, (byte)0, 0, sourceLimit);
        var copyLength = terminator < 0 ? sourceLimit : terminator;
        var destination = new byte[capacity];
        source.AsSpan(0, copyLength).CopyTo(destination);
        if (!ctx.Memory.TryWrite(destinationAddress, destination))
        {
            ctx[CpuRegister.Rax] = Efault;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "enqPGLfmVNU",
        ExportName = "strtok_r",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcStrtokReentrant(CpuContext ctx)
    {
        var currentAddress = ctx[CpuRegister.Rdi];
        var delimiterAddress = ctx[CpuRegister.Rsi];
        var savePointerAddress = ctx[CpuRegister.Rdx];
        if (delimiterAddress == 0 || savePointerAddress == 0 ||
            !TryReadUtf8CString(ctx, delimiterAddress, 64, out var delimiters))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (currentAddress == 0 && !ctx.TryReadUInt64(savePointerAddress, out currentAddress))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Span<byte> value = stackalloc byte[1];
        var examined = 0;
        while (currentAddress != 0 && examined++ < 4096)
        {
            if (!ctx.Memory.TryRead(currentAddress, value))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (value[0] == 0)
            {
                _ = ctx.TryWriteUInt64(savePointerAddress, 0);
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            if (delimiters.IndexOf((char)value[0]) < 0)
            {
                break;
            }

            currentAddress++;
        }

        var tokenAddress = currentAddress;
        while (examined++ < 4096)
        {
            if (!ctx.Memory.TryRead(currentAddress, value))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (value[0] == 0)
            {
                if (!ctx.TryWriteUInt64(savePointerAddress, 0))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                ctx[CpuRegister.Rax] = tokenAddress;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            if (delimiters.IndexOf((char)value[0]) >= 0)
            {
                value[0] = 0;
                if (!ctx.Memory.TryWrite(currentAddress, value) ||
                    !ctx.TryWriteUInt64(savePointerAddress, currentAddress + 1))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                ctx[CpuRegister.Rax] = tokenAddress;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            currentAddress++;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "mXlxhmLNMPg",
        ExportName = "strtol",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcStrtol(CpuContext ctx)
    {
        var inputAddress = ctx[CpuRegister.Rdi];
        var endPointerAddress = ctx[CpuRegister.Rsi];
        var numberBase = unchecked((int)ctx[CpuRegister.Rdx]);
        if (inputAddress == 0 || (numberBase != 0 && (numberBase < 2 || numberBase > 36)))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var buffer = GC.AllocateUninitializedArray<byte>(256);
        if (!ctx.Memory.TryRead(inputAddress, buffer))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        var index = 0;
        while (index < length && buffer[index] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\v' or (byte)'\f')
        {
            index++;
        }

        var negative = false;
        if (index < length && buffer[index] is (byte)'+' or (byte)'-')
        {
            negative = buffer[index] == (byte)'-';
            index++;
        }

        if (numberBase == 0)
        {
            numberBase = index + 1 < length && buffer[index] == (byte)'0' &&
                         buffer[index + 1] is (byte)'x' or (byte)'X'
                ? 16
                : index < length && buffer[index] == (byte)'0' ? 8 : 10;
        }

        if (numberBase == 16 && index + 1 < length && buffer[index] == (byte)'0' &&
            buffer[index + 1] is (byte)'x' or (byte)'X')
        {
            index += 2;
        }

        var digitStart = index;
        ulong magnitude = 0;
        while (index < length)
        {
            var c = buffer[index];
            var digit = c is >= (byte)'0' and <= (byte)'9'
                ? c - (byte)'0'
                : c is >= (byte)'a' and <= (byte)'z'
                    ? c - (byte)'a' + 10
                    : c is >= (byte)'A' and <= (byte)'Z'
                        ? c - (byte)'A' + 10
                        : 0xFF;
            if (digit >= numberBase)
            {
                break;
            }

            magnitude = unchecked((magnitude * (uint)numberBase) + (uint)digit);
            index++;
        }

        if (index == digitStart)
        {
            index = 0;
            magnitude = 0;
        }

        if (endPointerAddress != 0 &&
            !KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, endPointerAddress, inputAddress + (ulong)index))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var result = negative ? unchecked(0UL - magnitude) : magnitude;
        ctx[CpuRegister.Rax] = result;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "MpxhMh8QFro",
        ExportName = "fwrite",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcFwrite(CpuContext ctx)
    {
        // The guest owns FILE; returning the completed element count preserves
        // stdio semantics without dereferencing its opaque stream structure.
        ctx[CpuRegister.Rax] = ctx[CpuRegister.Rdx];
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "fffwELXNVFA",
        ExportName = "fprintf",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcFprintf(CpuContext ctx)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_FPRINTF"), "1", StringComparison.Ordinal))
        {
            var formatAddress = ctx[CpuRegister.Rsi];
            var arguments = new[]
            {
                ctx[CpuRegister.Rdx],
                ctx[CpuRegister.Rcx],
                ctx[CpuRegister.R8],
                ctx[CpuRegister.R9],
            };
            var format = TryReadUtf8CString(ctx, formatAddress, 1024, out var formatText)
                ? formatText.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
                : $"<unreadable@0x{formatAddress:X16}>";
            var renderedArguments = arguments.Select(argument =>
                argument >= 0x10000 && TryReadUtf8CString(ctx, argument, 4096, out var text)
                    ? $"0x{argument:X16}=\"{text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}\""
                    : $"0x{argument:X16}");
            Console.Error.WriteLine($"[LOADER][TRACE] fprintf format=\"{format}\" args=[{string.Join(", ", renderedArguments)}]");
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "MUjC4lbHrK4",
        ExportName = "fflush",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcFlushFile(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "m5wN+SwZOR4",
        ExportName = "putchar",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcPutchar(CpuContext ctx)
    {
        var value = unchecked((byte)ctx[CpuRegister.Rdi]);
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_FPRINTF"), "1", StringComparison.Ordinal))
        {
            Console.Error.Write((char)value);
        }

        ctx[CpuRegister.Rax] = value;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "m0iS6jNsXds",
        ExportName = "sched_get_priority_min",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SchedGetPriorityMin(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 1;
        return 1;
    }

    [SysAbiExport(
        Nid = "CBNtXOoef-E",
        ExportName = "sched_get_priority_max",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SchedGetPriorityMax(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 99;
        return 99;
    }

    [SysAbiExport(
        Nid = "nTxZBp8YNGc",
        ExportName = "pthread_mutex_setname_np",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexSetName(CpuContext ctx)
    {
        // Mutex names are diagnostic metadata and do not affect synchronization.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "EZ8h70dtFLg",
        ExportName = "pthread_cond_setname_np",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondSetName(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "smbQukfxYJM",
        ExportName = "getenv",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcGetEnvironmentVariable(CpuContext ctx)
    {
        // Console environment variables are not inherited from the host.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "UtO0OHMCgmI",
        ExportName = "sceKernelIsDevelopmentMode",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelIsDevelopmentMode(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "cHlIo6CoRTA",
        ExportName = "sceKernelGetNamedMemoryDomainCompat1270",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetNamedMemoryDomainCompat1270(CpuContext ctx)
    {
        // libmonosgen 12.70 queries the named "monoGc" memory domain and maps
        // the returned selector to its flexible-memory call. SharpEmu's guest
        // arena is not partitioned into domains, so the default selector is 0.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "KiJEPEWRyUY",
        ExportName = "sigaction",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int LibcSigaction(CpuContext ctx)
    {
        var previousActionAddress = ctx[CpuRegister.Rdx];
        if (previousActionAddress != 0)
        {
            // The runtime first queries dispositions to select an unused
            // realtime signal. Expose the default disposition; actual native
            // fault delivery remains owned by SharpEmu's signal bridge.
            Span<byte> defaultAction = stackalloc byte[0x20];
            defaultAction.Clear();
            if (!ctx.Memory.TryWrite(previousActionAddress, defaultAction))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "CU8m+Qs+HN4",
        ExportName = "sceSysmoduleLoadModuleByNameInternal",
        Target = Generation.Gen5,
        LibraryName = "libSceSysmodule")]
    public static int SysmoduleLoadModuleByNameInternal(CpuContext ctx)
    {
        var nameAddress = ctx[CpuRegister.Rdi];
        if (nameAddress == 0 || !TryReadUtf8CString(ctx, nameAddress, 512, out var moduleName))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        _ = KernelModuleRegistry.RegisterSyntheticModule(moduleName, isSystemModule: true);

        // The internal-by-name form optionally returns a module initializer
        // and a loader result through its third and fifth arguments. SharpEmu
        // has no native system-module initializer to call, so expose an empty
        // initializer and a successful loader result.
        var initializerAddress = ctx[CpuRegister.Rdx];
        if (initializerAddress != 0 &&
            !KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, initializerAddress, 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var resultAddress = ctx[CpuRegister.R8];
        if (resultAddress != 0 &&
            !KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, resultAddress, 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "nu4a0-arQis",
        ExportName = "sceKernelAioInitializeParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAioInitializeParam(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        Span<byte> zero = stackalloc byte[AioInitParamSize];
        zero.Clear();
        if (!ctx.Memory.TryWrite(paramAddress, zero))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "-o5uEDpN+oY",
        ExportName = "sceKernelConvertUtcToLocaltime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelConvertUtcToLocaltime(CpuContext ctx)
    {
        var utcSeconds = unchecked((long)ctx[CpuRegister.Rdi]);
        var localTimeAddress = ctx[CpuRegister.Rsi];
        var timesecAddress = ctx[CpuRegister.Rdx];
        var dstSecondsAddress = ctx[CpuRegister.Rcx];

        if (localTimeAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var utc = DateTimeOffset.FromUnixTimeSeconds(utcSeconds);
        var local = TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Local);
        var offset = local.Offset;
        var localSeconds = utcSeconds + (long)offset.TotalSeconds;
        var dstSeconds = TimeZoneInfo.Local.IsDaylightSavingTime(local.DateTime)
            ? (uint)Math.Max(0, TimeZoneInfo.Local.GetAdjustmentRules()
                .Where(rule => rule.DateStart <= local.Date && rule.DateEnd >= local.Date)
                .Select(rule => rule.DaylightDelta.TotalSeconds)
                .DefaultIfEmpty(0)
                .Max())
            : 0u;
        var westSeconds = unchecked((uint)(int)offset.TotalSeconds);

        if (!ctx.TryWriteUInt64(localTimeAddress, unchecked((ulong)localSeconds)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (timesecAddress != 0)
        {
            Span<byte> timesec = stackalloc byte[OrbisTimesecSize];
            BinaryPrimitives.WriteInt64LittleEndian(timesec, utcSeconds);
            BinaryPrimitives.WriteUInt32LittleEndian(timesec.Slice(sizeof(long), sizeof(uint)), westSeconds);
            BinaryPrimitives.WriteUInt32LittleEndian(timesec.Slice(sizeof(long) + sizeof(uint), sizeof(uint)), dstSeconds);
            if (!ctx.Memory.TryWrite(timesecAddress, timesec))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        if (dstSecondsAddress != 0 && !ctx.TryWriteUInt64(dstSecondsAddress, dstSeconds))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "0NTHN1NKONI",
        ExportName = "sceKernelConvertLocaltimeToUtc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelConvertLocaltimeToUtc(CpuContext ctx)
    {
        var localSeconds = unchecked((long)ctx[CpuRegister.Rdi]);
        var utcTimeAddress = ctx[CpuRegister.Rdx];
        var timezoneAddress = ctx[CpuRegister.Rcx];
        var dstSecondsAddress = ctx[CpuRegister.R8];

        if (timezoneAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var localDate = DateTimeOffset.FromUnixTimeSeconds(localSeconds).DateTime;
        var offset = TimeZoneInfo.Local.GetUtcOffset(localDate);
        var utcSeconds = localSeconds - (long)offset.TotalSeconds;
        var dstSeconds = TimeZoneInfo.Local.IsDaylightSavingTime(localDate)
            ? (int)Math.Max(0, TimeZoneInfo.Local.GetAdjustmentRules()
                .Where(rule => rule.DateStart <= localDate.Date && rule.DateEnd >= localDate.Date)
                .Select(rule => rule.DaylightDelta.TotalSeconds)
                .DefaultIfEmpty(0)
                .Max())
            : 0;
        var minutesWest = unchecked((int)-offset.TotalMinutes);

        if (!ctx.TryWriteInt32(timezoneAddress, minutesWest) ||
            !ctx.TryWriteInt32(timezoneAddress + sizeof(int), dstSeconds / 60))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (utcTimeAddress != 0 && !ctx.TryWriteUInt64(utcTimeAddress, unchecked((ulong)utcSeconds)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (dstSecondsAddress != 0 && !ctx.TryWriteInt32(dstSecondsAddress, dstSeconds))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "vYU8P9Td2Zo",
        ExportName = "sceKernelAioInitializeImpl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAioInitializeImpl(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var size = unchecked((int)ctx[CpuRegister.Rsi]);
        if (paramAddress == 0 || size < AioInitParamSize)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eR2bZFAAU0Q",
        ExportName = "sceSysmoduleUnloadModule",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSysmodule")]
    public static int SysmoduleUnloadModule(CpuContext ctx)
    {
        var moduleId = unchecked((int)ctx[CpuRegister.Rdi]);
        KernelModuleRegistry.MarkSysmoduleUnloaded(moduleId);
        lock (_stateGate)
        {
            _loadedSysmodules.Remove(moduleId);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "vXZhrtJxkGc",
        ExportName = "sceSysmoduleUnloadModuleInternal",
        Target = Generation.Gen5,
        LibraryName = "libSceSysmodule")]
    public static int SysmoduleUnloadModuleInternal(CpuContext ctx) => SysmoduleUnloadModule(ctx);

    [SysAbiExport(
        Nid = "hHrGoGoNf+s",
        ExportName = "sceSysmoduleLoadModuleInternalWithArg",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSysmodule")]
    public static int SysmoduleLoadModuleInternalWithArg(CpuContext ctx)
    {
        var moduleId = unchecked((int)ctx[CpuRegister.Rdi]);
        var resultAddress = ctx[CpuRegister.R8];
        _ = KernelModuleRegistry.MarkSysmoduleLoaded(moduleId);
        lock (_stateGate)
        {
            _loadedSysmodules.Add(moduleId);
        }

        if (resultAddress != 0 && !ctx.TryWriteInt32(resultAddress, 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "fMP5NHUOaMk",
        ExportName = "sceSysmoduleIsLoaded",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSysmodule")]
    public static int SysmoduleIsLoaded(CpuContext ctx)
    {
        var moduleId = unchecked((int)ctx[CpuRegister.Rdi]);
        var loaded = KernelModuleRegistry.IsSysmoduleLoaded(moduleId);
        lock (_stateGate)
        {
            loaded |= _loadedSysmodules.Contains(moduleId);
        }

        ctx[CpuRegister.Rax] = loaded ? 0UL : unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "WslcK1FQcGI",
        ExportName = "sceKernelIsNeoMode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelIsNeoMode(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Xjoosiw+XPI",
        ExportName = "sceKernelUuidCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelUuidCreate(CpuContext ctx)
    {
        var uuidAddress = ctx[CpuRegister.Rdi];
        if (uuidAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        Span<byte> uuid = stackalloc byte[16];
        RandomNumberGenerator.Fill(uuid);
        if (!ctx.Memory.TryWrite(uuidAddress, uuid))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int ResolveModuleHandleByAddress(ulong queriedAddress)
    {
        if (queriedAddress != 0 &&
            KernelModuleRegistry.TryGetModuleByAddress(queriedAddress, out var moduleFromAddress))
        {
            return moduleFromAddress.Handle;
        }

        if (KernelModuleRegistry.TryGetFirstModule(out var firstModule))
        {
            return firstModule.Handle;
        }

        return 1;
    }

    private static int KernelGetModuleInfoByHandleCore(CpuContext ctx, int handle, ulong outInfoAddress)
    {
        if (outInfoAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!KernelModuleRegistry.TryGetModuleByHandle(handle, out var module))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (!ctx.TryWriteInt32(outInfoAddress + ModuleInfoHandleOffset, module.Handle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        _ = TryWriteModuleName(ctx, outInfoAddress, module.Name);
        if (!ctx.TryWriteUInt64(outInfoAddress + ModuleInfoEhFrameHeaderOffset, module.EhFrameHeaderAddress))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int KernelGetModuleListCore(
        CpuContext ctx,
        ulong handlesAddress,
        ulong capacity,
        ulong outCountAddress,
        bool includeSystemModules)
    {
        if (outCountAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var handles = KernelModuleRegistry.GetModuleHandles(includeSystemModules);
        if (!ctx.TryWriteUInt64(outCountAddress, unchecked((ulong)handles.Length)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (handlesAddress == 0 || capacity == 0 || handles.Length == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var writableCount = (int)Math.Min(Math.Min(capacity, (ulong)int.MaxValue), (ulong)handles.Length);
        for (var i = 0; i < writableCount; i++)
        {
            if (!ctx.TryWriteInt32(handlesAddress + (ulong)(i * sizeof(int)), handles[i]))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        if ((ulong)handles.Length > capacity)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryWriteModuleName(CpuContext ctx, ulong outInfoAddress, string moduleName)
    {
        if (outInfoAddress == 0 || string.IsNullOrWhiteSpace(moduleName))
        {
            return false;
        }

        var encoded = Encoding.UTF8.GetBytes(moduleName);
        var payloadLength = Math.Min(encoded.Length, ModuleInfoNameMaxBytes - 1);
        var buffer = new byte[ModuleInfoNameMaxBytes];
        if (payloadLength > 0)
        {
            Array.Copy(encoded, 0, buffer, 0, payloadLength);
        }

        return ctx.Memory.TryWrite(outInfoAddress + ModuleInfoNameOffset, buffer);
    }

    private static bool TryReadUtf8Z(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        if (address == 0 || maxLength <= 0)
        {
            return false;
        }

        const int pageSize = 4096;
        const int inlineChunkSize = 64;
        Span<byte> buffer = stackalloc byte[Math.Min(maxLength, 512)];
        var length = 0;

        for (var offset = 0; offset < maxLength && length < buffer.Length;)
        {
            var current = address + (ulong)offset;
            var pageRemaining = pageSize - (int)(current & (pageSize - 1));
            var chunkSize = Math.Min(
                buffer.Length - length,
                Math.Min(maxLength - offset, Math.Min(inlineChunkSize, pageRemaining)));

            if (chunkSize <= 0)
            {
                return false;
            }

            var chunk = buffer.Slice(length, chunkSize);
            if (!ctx.Memory.TryRead(current, chunk))
            {
                return false;
            }

            var nulIndex = chunk.IndexOf((byte)0);
            if (nulIndex >= 0)
            {
                value = Encoding.UTF8.GetString(buffer[..(length + nulIndex)]);
                return true;
            }

            length += chunkSize;
            offset += chunkSize;
        }

        return false;
    }

    private static ulong GetTlsScratchAddress(CpuContext ctx, ulong offset)
    {
        if (ctx.FsBase == 0)
        {
            return 0;
        }

        return unchecked(ctx.FsBase + offset);
    }

    private static nint AllocateStackChkGuardObject()
    {
        try
        {
            var memory = Marshal.AllocHGlobal(sizeof(ulong) * 2);
            Marshal.WriteInt64(memory, unchecked((long)_stackChkGuardValue));
            Marshal.WriteInt64(IntPtr.Add(memory, sizeof(ulong)), unchecked((long)_stackChkGuardValue));
            return memory;
        }
        catch
        {
            return 0;
        }
    }

    private static ulong ResolveKernelTscFrequency()
    {
        const ulong minSane = 1_000_000UL;

        var overrideHzText = Environment.GetEnvironmentVariable("SHARPEMU_TSC_FREQ_HZ");
        if (!string.IsNullOrWhiteSpace(overrideHzText) &&
            ulong.TryParse(overrideHzText, out var overrideHz) &&
            overrideHz >= minSane)
        {
            TraceKernelTscFrequency("env", overrideHz);
            return overrideHz;
        }

        if (TryCalibrateHostTscFrequency(out ulong calibratedHz) && calibratedHz >= minSane)
        {
            TraceKernelTscFrequency("calibrated-rdtsc", calibratedHz);
            return calibratedHz;
        }

        if (TryResolveCpuidTscFrequency(out ulong cpuidHz) && cpuidHz >= minSane)
        {
            TraceKernelTscFrequency("cpuid", cpuidHz);
            return cpuidHz;
        }

        var hostQpc = Stopwatch.Frequency > 0
            ? unchecked((ulong)Stopwatch.Frequency)
            : DefaultKernelTscFrequency;
        if (hostQpc >= minSane)
        {
            TraceKernelTscFrequency("qpc", hostQpc);
            return hostQpc;
        }

        TraceKernelTscFrequency("default", DefaultKernelTscFrequency);
        return DefaultKernelTscFrequency;
    }

    private static void TraceKernelTscFrequency(string source, ulong frequencyHz)
    {
        Console.Error.WriteLine($"[LOADER][INFO] Kernel TSC frequency: {frequencyHz} Hz ({source})");
    }

    private static bool TryResolveCpuidTscFrequency(out ulong frequencyHz)
    {
        frequencyHz = 0;
        if (!X86Base.IsSupported)
        {
            return false;
        }

        try
        {
            var leaf15 = X86Base.CpuId(unchecked((int)0x15), 0);
            uint denominator = unchecked((uint)leaf15.Eax);
            uint numerator = unchecked((uint)leaf15.Ebx);
            uint crystalHz = unchecked((uint)leaf15.Ecx);
            if (denominator != 0 && numerator != 0 && crystalHz != 0)
            {
                frequencyHz = ((ulong)crystalHz * numerator) / denominator;
                if (frequencyHz != 0)
                {
                    return true;
                }
            }

            var leaf16 = X86Base.CpuId(unchecked((int)0x16), 0);
            uint baseMHz = unchecked((uint)leaf16.Eax);
            if (baseMHz != 0)
            {
                frequencyHz = (ulong)baseMHz * 1_000_000UL;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryCalibrateHostTscFrequency(out ulong frequencyHz)
    {
        frequencyHz = 0;
        if (!TryReadHostTsc(out _))
        {
            return false;
        }

        const int sampleCount = 3;
        Span<ulong> estimates = stackalloc ulong[sampleCount];
        int validSamples = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            if (!TryCalibrateHostTscFrequencySample(out ulong estimate))
            {
                continue;
            }

            estimates[validSamples++] = estimate;
        }

        if (validSamples == 0)
        {
            return false;
        }

        estimates[..validSamples].Sort();
        frequencyHz = estimates[validSamples / 2];
        return frequencyHz != 0;
    }

    private static bool TryCalibrateHostTscFrequencySample(out ulong frequencyHz)
    {
        frequencyHz = 0;
        if (!TryReadHostTsc(out ulong startTsc))
        {
            return false;
        }

        long startQpc = Stopwatch.GetTimestamp();
        long minimumQpcDelta = Math.Max(Stopwatch.Frequency / 20, 1);
        long endQpc;
        ulong endTsc;
        do
        {
            Thread.SpinWait(256);
            endQpc = Stopwatch.GetTimestamp();
            if (!TryReadHostTsc(out endTsc))
            {
                return false;
            }
        }
        while (endQpc - startQpc < minimumQpcDelta);

        ulong deltaTsc = endTsc - startTsc;
        long deltaQpc = endQpc - startQpc;
        if (deltaTsc == 0 || deltaQpc <= 0)
        {
            return false;
        }

        frequencyHz = unchecked((deltaTsc * (ulong)Stopwatch.Frequency) / (ulong)deltaQpc);
        return frequencyHz != 0;
    }

    private static bool TryReadHostTsc(out ulong counter)
    {
        counter = 0;
        if (_rdtscReader is null)
        {
            return false;
        }

        try
        {
            counter = _rdtscReader();
            return counter != 0;
        }
        catch
        {
            counter = 0;
            return false;
        }
    }

    private static RdtscDelegate? CreateRdtscReader()
    {
        if (!Environment.Is64BitProcess ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return null;
        }

        try
        {
            nint stubAddress = VirtualAlloc(nint.Zero, (nuint)16, MemCommit | MemReserve, PageExecuteReadWrite);
            if (stubAddress == 0)
            {
                return null;
            }

            ReadOnlySpan<byte> stub = stackalloc byte[]
            {
                0x0F, 0x31,
                0x48, 0xC1, 0xE2, 0x20,
                0x48, 0x09, 0xD0,
                0xC3,
            };

            unsafe
            {
                fixed (byte* src = stub)
                {
                    Buffer.MemoryCopy(src, (void*)stubAddress, stub.Length, stub.Length);
                }
            }

            return Marshal.GetDelegateForFunctionPointer<RdtscDelegate>(stubAddress);
        }
        catch
        {
            return null;
        }
    }

    private static unsafe nint VirtualAlloc(nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect) =>
        (nint)HostMemory.Alloc((void*)lpAddress, dwSize, flAllocationType, flProtect);

    private static bool TryReserveVirtualRange(
        CpuContext ctx,
        ulong desiredAddress,
        ulong length,
        ulong alignment,
        bool allowSearch,
        out ulong mappedAddress)
    {
        return KernelVirtualRangeAllocator.TryReserve(
            ctx,
            desiredAddress,
            length,
            executable: false,
            alignment,
            allowSearch,
            allowAllocateAtAlternative: allowSearch,
            "reserve_virtual_range",
            out mappedAddress);
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        var mask = alignment - 1;
        return (value + mask) & ~mask;
    }

    private static bool ShouldTraceVirtualMemory()
    {
        return string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_VIRTUAL_MEMORY"), "1", StringComparison.Ordinal);
    }
}
