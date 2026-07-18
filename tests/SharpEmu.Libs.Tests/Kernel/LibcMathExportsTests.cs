// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class LibcMathExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ErrnoAddress = MemoryBase + 0x40;
    private const int InitialErrno = 0x1234;
    private const uint InitialMxcsr = 0x1F80;
    private const uint FloatingInvalid = 0x01;
    private const uint FloatingDivideByZero = 0x04;
    private const uint FloatingOverflowAndInexact = 0x28;
    private const uint FloatingUnderflowAndInexact = 0x30;
    private const int ErrnoDomain = 33;
    private const int ErrnoRange = 34;

    public static IEnumerable<object[]> ExportCases()
    {
        yield return new object[] { "JBcgYuW8lPU", "acos" };
        yield return new object[] { "7Ly52zaL44Q", "asin" };
        yield return new object[] { "GZWjF-YIFFk", "asinf" };
        yield return new object[] { "OXmauLdQ8kY", "atan" };
        yield return new object[] { "weDug8QD-lE", "atanf" };
        yield return new object[] { "2WE3BTYVwKM", "cos" };
        yield return new object[] { "-P6FNMzk2Kc", "cosf" };
        yield return new object[] { "NVadfnzQhHQ", "exp" };
        yield return new object[] { "dnaeGXbjP6E", "exp2" };
        yield return new object[] { "wuAQt-j+p4o", "exp2f" };
        yield return new object[] { "8zsu04XNsZ4", "expf" };
        yield return new object[] { "rtV7-jWC6Yg", "log" };
        yield return new object[] { "lhpd6Wk6ccs", "log10f" };
        yield return new object[] { "Y5DhuDKGlnQ", "log2" };
        yield return new object[] { "hsi9drzHR2k", "log2f" };
        yield return new object[] { "RQXLbdT2lc4", "logf" };
        yield return new object[] { "H8ya2H00jbI", "sin" };
        yield return new object[] { "Q4rRL34CEeE", "sinf" };
        yield return new object[] { "T7uyNqP7vQA", "tan" };
        yield return new object[] { "ZE6RNL+eLbk", "tanf" };
    }

    public static IEnumerable<object[]> PositiveDoubleCases()
    {
        yield return new object[] { "JBcgYuW8lPU", "acos", 0.5, Math.PI / 3.0, 1e-12 };
        yield return new object[] { "7Ly52zaL44Q", "asin", 0.5, Math.PI / 6.0, 1e-12 };
        yield return new object[] { "OXmauLdQ8kY", "atan", 1.0, Math.PI / 4.0, 1e-12 };
        yield return new object[] { "2WE3BTYVwKM", "cos", 0.0, 1.0, 0.0 };
        yield return new object[] { "NVadfnzQhHQ", "exp", 1.0, Math.E, 1e-12 };
        yield return new object[] { "dnaeGXbjP6E", "exp2", 3.0, 8.0, 0.0 };
        yield return new object[] { "rtV7-jWC6Yg", "log", Math.E, 1.0, 1e-12 };
        yield return new object[] { "Y5DhuDKGlnQ", "log2", 8.0, 3.0, 0.0 };
        yield return new object[] { "H8ya2H00jbI", "sin", Math.PI / 2.0, 1.0, 1e-12 };
        yield return new object[] { "T7uyNqP7vQA", "tan", Math.PI / 4.0, 1.0, 1e-12 };
    }

    public static IEnumerable<object[]> PositiveSingleCases()
    {
        yield return new object[] { "GZWjF-YIFFk", "asinf", 0.5f, MathF.PI / 6.0f, 1e-5f };
        yield return new object[] { "weDug8QD-lE", "atanf", 1.0f, MathF.PI / 4.0f, 1e-5f };
        yield return new object[] { "-P6FNMzk2Kc", "cosf", 0.0f, 1.0f, 0.0f };
        yield return new object[] { "wuAQt-j+p4o", "exp2f", 3.0f, 8.0f, 0.0f };
        yield return new object[] { "8zsu04XNsZ4", "expf", 1.0f, MathF.E, 1e-5f };
        yield return new object[] { "lhpd6Wk6ccs", "log10f", 100.0f, 2.0f, 1e-5f };
        yield return new object[] { "hsi9drzHR2k", "log2f", 8.0f, 3.0f, 0.0f };
        yield return new object[] { "RQXLbdT2lc4", "logf", MathF.E, 1.0f, 1e-5f };
        yield return new object[] { "Q4rRL34CEeE", "sinf", MathF.PI / 2.0f, 1.0f, 1e-5f };
        yield return new object[] { "ZE6RNL+eLbk", "tanf", MathF.PI / 4.0f, 1.0f, 1e-5f };
    }

    public static IEnumerable<object[]> InverseDomainCases()
    {
        yield return new object[] { "JBcgYuW8lPU", "acos", false };
        yield return new object[] { "7Ly52zaL44Q", "asin", false };
        yield return new object[] { "GZWjF-YIFFk", "asinf", true };
    }

    public static IEnumerable<object[]> AtanSignedZeroCases()
    {
        yield return new object[] { "OXmauLdQ8kY", "atan", false };
        yield return new object[] { "weDug8QD-lE", "atanf", true };
    }

    public static IEnumerable<object[]> TrigInfinityCases()
    {
        yield return new object[] { "2WE3BTYVwKM", "cos", false };
        yield return new object[] { "-P6FNMzk2Kc", "cosf", true };
        yield return new object[] { "H8ya2H00jbI", "sin", false };
        yield return new object[] { "Q4rRL34CEeE", "sinf", true };
        yield return new object[] { "T7uyNqP7vQA", "tan", false };
        yield return new object[] { "ZE6RNL+eLbk", "tanf", true };
    }

    public static IEnumerable<object[]> ExpRangeCases()
    {
        yield return new object[] { "NVadfnzQhHQ", "exp", false, 1000.0, true, FloatingOverflowAndInexact };
        yield return new object[] { "NVadfnzQhHQ", "exp", false, -1000.0, false, FloatingUnderflowAndInexact };
        yield return new object[] { "dnaeGXbjP6E", "exp2", false, 2000.0, true, FloatingOverflowAndInexact };
        yield return new object[] { "dnaeGXbjP6E", "exp2", false, -2000.0, false, FloatingUnderflowAndInexact };
        yield return new object[] { "wuAQt-j+p4o", "exp2f", true, 200.0, true, FloatingOverflowAndInexact };
        yield return new object[] { "wuAQt-j+p4o", "exp2f", true, -200.0, false, FloatingUnderflowAndInexact };
        yield return new object[] { "8zsu04XNsZ4", "expf", true, 100.0, true, FloatingOverflowAndInexact };
        yield return new object[] { "8zsu04XNsZ4", "expf", true, -200.0, false, FloatingUnderflowAndInexact };
    }

    public static IEnumerable<object[]> ExpBoundaryCases()
    {
        yield return new object[]
        {
            "NVadfnzQhHQ", "exp", false,
            BitConverter.Int64BitsToDouble(unchecked((long)0x40862E42FEFA39EFUL)), 0U,
        };
        yield return new object[]
        {
            "NVadfnzQhHQ", "exp", false,
            BitConverter.Int64BitsToDouble(unchecked((long)0xC0874910D52D3051UL)), 0U,
        };
        yield return new object[] { "dnaeGXbjP6E", "exp2", false, 1024.0, FloatingOverflowAndInexact };
        yield return new object[] { "dnaeGXbjP6E", "exp2", false, -1075.0, FloatingUnderflowAndInexact };
        yield return new object[]
        {
            "8zsu04XNsZ4", "expf", true,
            (double)BitConverter.Int32BitsToSingle(unchecked((int)0x42B17180U)), 0U,
        };
        yield return new object[]
        {
            "8zsu04XNsZ4", "expf", true,
            (double)BitConverter.Int32BitsToSingle(unchecked((int)0xC2CFF1B5U)), 0U,
        };
        yield return new object[] { "wuAQt-j+p4o", "exp2f", true, 128.0, FloatingOverflowAndInexact };
        yield return new object[] { "wuAQt-j+p4o", "exp2f", true, -150.0, FloatingUnderflowAndInexact };
    }

    public static IEnumerable<object[]> LogErrorCases()
    {
        yield return new object[] { "rtV7-jWC6Yg", "log", false, 0.0, false, ErrnoRange, FloatingDivideByZero };
        yield return new object[] { "rtV7-jWC6Yg", "log", false, -1.0, true, ErrnoDomain, FloatingInvalid };
        yield return new object[] { "RQXLbdT2lc4", "logf", true, 0.0, false, ErrnoRange, FloatingDivideByZero };
        yield return new object[] { "RQXLbdT2lc4", "logf", true, -1.0, true, ErrnoDomain, FloatingInvalid };
        yield return new object[] { "lhpd6Wk6ccs", "log10f", true, -1.0, true, ErrnoDomain, FloatingInvalid };
        yield return new object[] { "Y5DhuDKGlnQ", "log2", false, -1.0, true, ErrnoDomain, FloatingInvalid };
        yield return new object[] { "hsi9drzHR2k", "log2f", true, -1.0, true, ErrnoDomain, FloatingInvalid };
    }

    public static IEnumerable<object[]> LogZeroWithoutRaiseCases()
    {
        yield return new object[] { "lhpd6Wk6ccs", "log10f", true };
        yield return new object[] { "Y5DhuDKGlnQ", "log2", false };
        yield return new object[] { "hsi9drzHR2k", "log2f", true };
    }

    [Theory]
    [MemberData(nameof(ExportCases))]
    public void Exports_RegisterAsGen5Libc(string nid, string name)
    {
        var manager = CreateManager();

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(nid, export.Nid);
        Assert.Equal(name, export.Name);
        Assert.Equal("libc", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    [Theory]
    [MemberData(nameof(PositiveDoubleCases))]
    public void DoubleExports_ReturnExpectedAndDoNotInventExceptions(
        string nid,
        string name,
        double input,
        double expected,
        double tolerance)
    {
        var (memory, context) = CreateContext();
        SetDouble(context, input);

        Dispatch(nid, name, context);

        var result = ReadDouble(context);
        Assert.InRange(Math.Abs(result - expected), 0.0, tolerance);
        AssertNoError(memory, context);
    }

    [Theory]
    [MemberData(nameof(PositiveSingleCases))]
    public void SingleExports_ReturnExpectedAndDoNotInventExceptions(
        string nid,
        string name,
        float input,
        float expected,
        float tolerance)
    {
        var (memory, context) = CreateContext();
        SetSingle(context, input);

        Dispatch(nid, name, context);

        var result = ReadSingle(context);
        Assert.InRange(MathF.Abs(result - expected), 0.0f, tolerance);
        AssertNoError(memory, context);
    }

    [Theory]
    [MemberData(nameof(InverseDomainCases))]
    public void InverseTrigDomainError_ReturnsNanAndRaisesInvalid(
        string nid,
        string name,
        bool single)
    {
        var (memory, context) = CreateContext();
        if (single)
        {
            SetSingle(context, 2.0f);
        }
        else
        {
            SetDouble(context, 2.0);
        }

        Dispatch(nid, name, context);

        Assert.True(single ? float.IsNaN(ReadSingle(context)) : double.IsNaN(ReadDouble(context)));
        AssertError(memory, context, ErrnoDomain, FloatingInvalid);
    }

    [Theory]
    [MemberData(nameof(AtanSignedZeroCases))]
    public void Atan_PreservesNegativeZeroWithoutRaising(
        string nid,
        string name,
        bool single)
    {
        var (memory, context) = CreateContext();
        if (single)
        {
            SetSingle(context, BitConverter.Int32BitsToSingle(unchecked((int)0x8000_0000U)));
        }
        else
        {
            SetDouble(context, BitConverter.Int64BitsToDouble(unchecked((long)0x8000_0000_0000_0000UL)));
        }

        Dispatch(nid, name, context);

        if (single)
        {
            Assert.Equal(0x8000_0000U, unchecked((uint)BitConverter.SingleToInt32Bits(ReadSingle(context))));
        }
        else
        {
            Assert.Equal(0x8000_0000_0000_0000UL, unchecked((ulong)BitConverter.DoubleToInt64Bits(ReadDouble(context))));
        }
        AssertNoError(memory, context);
    }

    [Theory]
    [MemberData(nameof(TrigInfinityCases))]
    public void TrigInfinity_ReturnsNanAndRaisesInvalid(
        string nid,
        string name,
        bool single)
    {
        var (memory, context) = CreateContext();
        if (single)
        {
            SetSingle(context, float.PositiveInfinity);
        }
        else
        {
            SetDouble(context, double.PositiveInfinity);
        }

        Dispatch(nid, name, context);

        Assert.True(single ? float.IsNaN(ReadSingle(context)) : double.IsNaN(ReadDouble(context)));
        AssertError(memory, context, ErrnoDomain, FloatingInvalid);
    }

    [Theory]
    [MemberData(nameof(ExpRangeCases))]
    public void ExpRangeError_RaisesExactFirmwareState(
        string nid,
        string name,
        bool single,
        double input,
        bool overflow,
        uint flags)
    {
        var (memory, context) = CreateContext();
        if (single)
        {
            SetSingle(context, (float)input);
        }
        else
        {
            SetDouble(context, input);
        }

        Dispatch(nid, name, context);

        if (single)
        {
            var result = ReadSingle(context);
            Assert.True(overflow ? float.IsPositiveInfinity(result) : IsPositiveZero(result));
        }
        else
        {
            var result = ReadDouble(context);
            Assert.True(overflow ? double.IsPositiveInfinity(result) : IsPositiveZero(result));
        }
        AssertError(memory, context, ErrnoRange, flags);
    }

    [Theory]
    [MemberData(nameof(ExpBoundaryCases))]
    public void ExpThresholds_UseExactFirmwareComparisonDirection(
        string nid,
        string name,
        bool single,
        double input,
        uint flags)
    {
        var (memory, context) = CreateContext();
        if (single)
        {
            SetSingle(context, (float)input);
        }
        else
        {
            SetDouble(context, input);
        }

        Dispatch(nid, name, context);

        _ = single ? ReadSingle(context) : ReadDouble(context);
        if (flags == 0)
        {
            AssertNoError(memory, context);
        }
        else
        {
            AssertError(memory, context, ErrnoRange, flags);
        }
    }

    [Theory]
    [MemberData(nameof(LogErrorCases))]
    public void LogErrors_ReturnExpectedAndRaiseExactFirmwareState(
        string nid,
        string name,
        bool single,
        double input,
        bool expectNan,
        int errno,
        uint flags)
    {
        var (memory, context) = CreateContext();
        if (single)
        {
            SetSingle(context, (float)input);
        }
        else
        {
            SetDouble(context, input);
        }

        Dispatch(nid, name, context);

        if (single)
        {
            var result = ReadSingle(context);
            Assert.True(expectNan ? float.IsNaN(result) : float.IsNegativeInfinity(result));
        }
        else
        {
            var result = ReadDouble(context);
            Assert.True(expectNan ? double.IsNaN(result) : double.IsNegativeInfinity(result));
        }
        AssertError(memory, context, errno, flags);
    }

    [Theory]
    [MemberData(nameof(LogZeroWithoutRaiseCases))]
    public void SelectedLogZeroPaths_ReturnNegativeInfinityWithoutExplicitRaise(
        string nid,
        string name,
        bool single)
    {
        var (memory, context) = CreateContext();
        if (single)
        {
            SetSingle(context, 0.0f);
        }
        else
        {
            SetDouble(context, 0.0);
        }

        Dispatch(nid, name, context);

        Assert.True(single
            ? float.IsNegativeInfinity(ReadSingle(context))
            : double.IsNegativeInfinity(ReadDouble(context)));
        AssertNoError(memory, context);
    }

    private static ModuleManager CreateManager()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        return manager;
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateContext()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            FsBase = MemoryBase,
            Mxcsr = InitialMxcsr,
        };
        context[CpuRegister.Rax] = ulong.MaxValue;
        WriteErrno(memory, InitialErrno);
        return (memory, context);
    }

    private static void Dispatch(string nid, string name, CpuContext context)
    {
        var manager = CreateManager();
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libc", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
        Assert.True(manager.TryDispatch(nid, context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private static void SetDouble(CpuContext context, double value) =>
        context.SetXmmRegister(
            0,
            unchecked((ulong)BitConverter.DoubleToInt64Bits(value)),
            ulong.MaxValue);

    private static double ReadDouble(CpuContext context)
    {
        context.GetXmmRegister(0, out var low, out var high);
        Assert.Equal(0UL, high);
        return BitConverter.Int64BitsToDouble(unchecked((long)low));
    }

    private static void SetSingle(CpuContext context, float value) =>
        context.SetXmmRegister(
            0,
            unchecked((uint)BitConverter.SingleToInt32Bits(value)) | 0xFFFF_FFFF_0000_0000UL,
            ulong.MaxValue);

    private static float ReadSingle(CpuContext context)
    {
        context.GetXmmRegister(0, out var low, out var high);
        Assert.Equal(0UL, low >> 32);
        Assert.Equal(0UL, high);
        return BitConverter.Int32BitsToSingle(unchecked((int)(uint)low));
    }

    private static bool IsPositiveZero(double value) =>
        unchecked((ulong)BitConverter.DoubleToInt64Bits(value)) == 0;

    private static bool IsPositiveZero(float value) =>
        unchecked((uint)BitConverter.SingleToInt32Bits(value)) == 0;

    private static void AssertNoError(FakeCpuMemory memory, CpuContext context)
    {
        Assert.Equal(InitialErrno, ReadErrno(memory));
        Assert.Equal(InitialMxcsr, context.Mxcsr);
    }

    private static void AssertError(
        FakeCpuMemory memory,
        CpuContext context,
        int errno,
        uint flags)
    {
        Assert.Equal(errno, ReadErrno(memory));
        Assert.Equal(InitialMxcsr | flags, context.Mxcsr);
    }

    private static void WriteErrno(FakeCpuMemory memory, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(ErrnoAddress, bytes));
    }

    private static int ReadErrno(FakeCpuMemory memory)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(ErrnoAddress, bytes));
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }
}
