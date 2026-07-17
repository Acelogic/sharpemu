// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelMemoryCompatExportsTests
{
    [Fact]
    public void PosixStat_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        const ulong statAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/shader.cache");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = statAddress;

        var result = KernelMemoryCompatExports.PosixStat(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Sprintf_ReadsVariadicDoubleFromXmmRegister()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong destinationAddress = memoryBase + 0x100;
        const ulong formatAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(formatAddress, "%.4f");
        context[CpuRegister.Rdi] = destinationAddress;
        context[CpuRegister.Rsi] = formatAddress;
        context.SetXmmRegister(
            0,
            unchecked((ulong)BitConverter.DoubleToInt64Bits(0.5576)),
            0);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");

            var result = KernelMemoryCompatExports.Sprintf(context);

            Assert.Equal(0, result);
            Assert.Equal(6UL, context[CpuRegister.Rax]);
            Span<byte> output = stackalloc byte[7];
            Assert.True(memory.TryRead(destinationAddress, output));
            Assert.Equal("0.5576\0", Encoding.UTF8.GetString(output));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void BasicLibcCompatExports_RegisterByKnownNids()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        AssertExport(manager, "5TjaJwkLWxE", "bcmp");
        AssertExport(manager, "AEJdIVZTEmo", "qsort");
        AssertExport(manager, "1Pk0qZQGeWo", "sscanf");
        AssertExport(manager, "pXvbDfchu6k", "strncasecmp");
        AssertExport(manager, "g7zzzLDYGw0", "strdup");
        AssertExport(manager, "YQ0navp+YIc", "puts");
        AssertExport(manager, "8vE6Z6VEYyk", "access");
    }

    [Fact]
    public void BcmpAndStrncasecmp_CompareGuestMemory()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong leftAddress = memoryBase + 0x100;
        const ulong rightAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(memory.TryWrite(leftAddress, new byte[] { 1, 2, 3 }));
        Assert.True(memory.TryWrite(rightAddress, new byte[] { 1, 2, 4 }));
        context[CpuRegister.Rdi] = leftAddress;
        context[CpuRegister.Rsi] = rightAddress;
        context[CpuRegister.Rdx] = 3;
        Assert.Equal(0, KernelMemoryCompatExports.Bcmp(context));
        Assert.NotEqual(0UL, context[CpuRegister.Rax]);

        memory.WriteCString(leftAddress, "AbCd");
        memory.WriteCString(rightAddress, "aBcX");
        context[CpuRegister.Rdx] = 3;
        Assert.Equal(0, KernelMemoryCompatExports.Strncasecmp(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        context[CpuRegister.Rdx] = 4;
        Assert.Equal(0, KernelMemoryCompatExports.Strncasecmp(context));
        Assert.NotEqual(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Qsort_InvokesGuestComparatorAndSortsElements()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong arrayAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var values = new ulong[] { 40, 10, 30, 20 };
        for (var index = 0; index < values.Length; index++)
        {
            Assert.True(context.TryWriteUInt64(arrayAddress + ((ulong)index * sizeof(ulong)), values[index]));
        }

        context[CpuRegister.Rdi] = arrayAddress;
        context[CpuRegister.Rsi] = (ulong)values.Length;
        context[CpuRegister.Rdx] = sizeof(ulong);
        context[CpuRegister.Rcx] = 0x1234_5678;

        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = new QsortTestScheduler();
        try
        {
            Assert.Equal(0, KernelMemoryCompatExports.Qsort(context));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }

        for (var index = 0; index < values.Length; index++)
        {
            Assert.True(context.TryReadUInt64(arrayAddress + ((ulong)index * sizeof(ulong)), out var value));
            Assert.Equal((ulong)((index + 1) * 10), value);
        }
    }

    [Fact]
    public void Sscanf_ParsesShellCoreFloatScansetStringAndHexFormats()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong inputAddress = memoryBase + 0x100;
        const ulong formatAddress = memoryBase + 0x300;
        const ulong firstAddress = memoryBase + 0x500;
        const ulong secondAddress = memoryBase + 0x510;
        const ulong thirdAddress = memoryBase + 0x520;
        const ulong fourthAddress = memoryBase + 0x530;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        memory.WriteCString(inputAddress, "10%, 20%, 30%, 40");
        memory.WriteCString(
            formatAddress,
            "%f%*[%%, \t]%f%*[%%, \t]%f%*[%%, \t]%f");
        context[CpuRegister.Rdi] = inputAddress;
        context[CpuRegister.Rsi] = formatAddress;
        context[CpuRegister.Rdx] = firstAddress;
        context[CpuRegister.Rcx] = secondAddress;
        context[CpuRegister.R8] = thirdAddress;
        context[CpuRegister.R9] = fourthAddress;

        Assert.Equal(0, KernelMemoryCompatExports.Sscanf(context));
        Assert.Equal(4UL, context[CpuRegister.Rax]);
        Assert.Equal(10f, ReadSingle(memory, firstAddress));
        Assert.Equal(20f, ReadSingle(memory, secondAddress));
        Assert.Equal(30f, ReadSingle(memory, thirdAddress));
        Assert.Equal(40f, ReadSingle(memory, fourthAddress));

        memory.WriteCString(inputAddress, "12.5 label");
        memory.WriteCString(formatAddress, "%f%31s");
        context[CpuRegister.Rdx] = firstAddress;
        context[CpuRegister.Rcx] = secondAddress;
        Assert.Equal(0, KernelMemoryCompatExports.Sscanf(context));
        Assert.Equal(2UL, context[CpuRegister.Rax]);
        Assert.Equal(12.5f, ReadSingle(memory, firstAddress));
        Span<byte> text = stackalloc byte[6];
        Assert.True(memory.TryRead(secondAddress, text));
        Assert.Equal("label\0", Encoding.UTF8.GetString(text));

        memory.WriteCString(inputAddress, "ff");
        memory.WriteCString(formatAddress, "%x");
        context[CpuRegister.Rdx] = firstAddress;
        Assert.Equal(0, KernelMemoryCompatExports.Sscanf(context));
        Assert.Equal(1UL, context[CpuRegister.Rax]);
        Span<byte> hex = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(firstAddress, hex));
        Assert.Equal(0xFFu, BitConverter.ToUInt32(hex));
    }

    [Fact]
    public void Strdup_CopiesCStringIntoTrackedLibcHeap()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong sourceAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        memory.WriteCString(sourceAddress, "ShellCore");
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = sourceAddress;

        Assert.Equal(0, KernelMemoryCompatExports.Strdup(context));
        var duplicate = context[CpuRegister.Rax];
        Assert.NotEqual(0UL, duplicate);
        try
        {
            Assert.Equal("ShellCore", Marshal.PtrToStringUTF8(unchecked((nint)duplicate)));
        }
        finally
        {
            context[CpuRegister.Rdi] = duplicate;
            Assert.Equal(0, KernelMemoryCompatExports.Free(context));
        }
    }

    [Fact]
    public void Access_ReturnsPosixResultForExistingAndMissingPaths()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);
        var existingPath = Path.GetTempFileName();
        try
        {
            memory.WriteCString(pathAddress, existingPath);
            context[CpuRegister.Rdi] = pathAddress;
            context[CpuRegister.Rsi] = 0;
            Assert.Equal(0, KernelMemoryCompatExports.PosixAccess(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);

            memory.WriteCString(pathAddress, existingPath + ".missing");
            Assert.Equal(-1, KernelMemoryCompatExports.PosixAccess(context));
            Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        }
        finally
        {
            File.Delete(existingPath);
        }
    }

    private static void AssertExport(ModuleManager manager, string nid, string name)
    {
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
    }

    private static float ReadSingle(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        Assert.True(memory.TryRead(address, bytes));
        return BitConverter.ToSingle(bytes);
    }

    private sealed class QsortTestScheduler : IGuestThreadScheduler
    {
        public bool SupportsGuestContextTransfer => false;

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
        }

        public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryJoinThread(
            CpuContext callerContext,
            ulong threadHandle,
            out ulong returnValue,
            out string? error)
        {
            returnValue = 0;
            error = "not supported";
            return false;
        }

        public void Pump(CpuContext callerContext, string reason)
        {
        }

        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;

        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => false;

        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => false;

        public bool TrySuspendGuestThread(ulong guestThreadHandle, out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryResumeGuestThread(ulong guestThreadHandle, out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryGetSuspendedGuestThreadContext(
            ulong guestThreadHandle,
            out GuestCpuContinuation continuation,
            out string? error)
        {
            continuation = default;
            error = "not supported";
            return false;
        }

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => Array.Empty<GuestThreadSnapshot>();

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error)
        {
            var result = TryCallGuestFunction(
                callerContext,
                entryPoint,
                arg0,
                arg1,
                0,
                stackAddress,
                stackSize,
                reason,
                out _,
                out error);
            return result;
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            if (!callerContext.TryReadUInt64(arg0, out var left) ||
                !callerContext.TryReadUInt64(arg1, out var right))
            {
                returnValue = 0;
                error = "unreadable comparator argument";
                return false;
            }

            returnValue = unchecked((uint)left.CompareTo(right));
            error = null;
            return true;
        }

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryRaiseGuestException(
            CpuContext callerContext,
            ulong threadHandle,
            ulong handler,
            int exceptionType,
            out string? error)
        {
            error = "not supported";
            return false;
        }
    }
}
