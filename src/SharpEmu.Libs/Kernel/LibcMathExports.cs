// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Gen5 libc scalar-math exports used by Grand Theft Auto V.
/// </summary>
public static class LibcMathExports
{
    private const uint FloatingInvalid = 0x01;
    private const uint FloatingDivideByZero = 0x04;
    private const uint FloatingOverflow = 0x08;
    private const uint FloatingUnderflow = 0x10;
    private const uint FloatingInexact = 0x20;
    private const int ErrnoDomain = 33;
    private const int ErrnoRange = 34;

    // Thresholds are the exact constants used by the analyzed Gen5
    // libSceLibcInternal image. The comparison direction is part of the ABI
    // behavior: exp/expf use strict comparisons while exp2/exp2f include the
    // boundary.
    private static readonly double ExpOverflowThreshold =
        BitConverter.Int64BitsToDouble(unchecked((long)0x40862E42FEFA39EFUL));
    private static readonly double ExpUnderflowThreshold =
        BitConverter.Int64BitsToDouble(unchecked((long)0xC0874910D52D3051UL));
    private static readonly float ExpfOverflowThreshold =
        BitConverter.Int32BitsToSingle(unchecked((int)0x42B17180U));
    private static readonly float ExpfUnderflowThreshold =
        BitConverter.Int32BitsToSingle(unchecked((int)0xC2CFF1B5U));

    [SysAbiExport(
        Nid = "JBcgYuW8lPU",
        ExportName = "acos",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcAcos(CpuContext ctx) =>
        ReturnDouble(ctx, Math.Acos, InverseTrigDoubleError);

    [SysAbiExport(
        Nid = "7Ly52zaL44Q",
        ExportName = "asin",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcAsin(CpuContext ctx) =>
        ReturnDouble(ctx, Math.Asin, InverseTrigDoubleError);

    [SysAbiExport(
        Nid = "GZWjF-YIFFk",
        ExportName = "asinf",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcAsinf(CpuContext ctx) =>
        ReturnSingle(ctx, MathF.Asin, InverseTrigSingleError);

    [SysAbiExport(
        Nid = "OXmauLdQ8kY",
        ExportName = "atan",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcAtan(CpuContext ctx) => ReturnDouble(ctx, Math.Atan);

    [SysAbiExport(
        Nid = "weDug8QD-lE",
        ExportName = "atanf",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcAtanf(CpuContext ctx) => ReturnSingle(ctx, MathF.Atan);

    [SysAbiExport(
        Nid = "2WE3BTYVwKM",
        ExportName = "cos",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcCos(CpuContext ctx) =>
        ReturnDouble(ctx, Math.Cos, TrigDoubleError);

    [SysAbiExport(
        Nid = "-P6FNMzk2Kc",
        ExportName = "cosf",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcCosf(CpuContext ctx) =>
        ReturnSingle(ctx, MathF.Cos, TrigSingleError);

    [SysAbiExport(
        Nid = "NVadfnzQhHQ",
        ExportName = "exp",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcExp(CpuContext ctx) =>
        ReturnDouble(ctx, Math.Exp, ExpDoubleError);

    [SysAbiExport(
        Nid = "dnaeGXbjP6E",
        ExportName = "exp2",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcExp2(CpuContext ctx) =>
        ReturnDouble(ctx, PowTwo, Exp2DoubleError);

    [SysAbiExport(
        Nid = "wuAQt-j+p4o",
        ExportName = "exp2f",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcExp2f(CpuContext ctx) =>
        ReturnSingle(ctx, PowTwo, Exp2SingleError);

    [SysAbiExport(
        Nid = "8zsu04XNsZ4",
        ExportName = "expf",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcExpf(CpuContext ctx) =>
        ReturnSingle(ctx, MathF.Exp, ExpSingleError);

    [SysAbiExport(
        Nid = "rtV7-jWC6Yg",
        ExportName = "log",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcLog(CpuContext ctx) =>
        ReturnDouble(ctx, Math.Log, LogDoubleError);

    [SysAbiExport(
        Nid = "lhpd6Wk6ccs",
        ExportName = "log10f",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcLog10f(CpuContext ctx) =>
        ReturnSingle(ctx, MathF.Log10, NegativeSingleError);

    [SysAbiExport(
        Nid = "Y5DhuDKGlnQ",
        ExportName = "log2",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcLog2(CpuContext ctx) =>
        ReturnDouble(ctx, Math.Log2, NegativeDoubleError);

    [SysAbiExport(
        Nid = "hsi9drzHR2k",
        ExportName = "log2f",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcLog2f(CpuContext ctx) =>
        ReturnSingle(ctx, MathF.Log2, NegativeSingleError);

    [SysAbiExport(
        Nid = "RQXLbdT2lc4",
        ExportName = "logf",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcLogf(CpuContext ctx) =>
        ReturnSingle(ctx, MathF.Log, LogSingleError);

    [SysAbiExport(
        Nid = "H8ya2H00jbI",
        ExportName = "sin",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcSin(CpuContext ctx) =>
        ReturnDouble(ctx, Math.Sin, TrigDoubleError);

    [SysAbiExport(
        Nid = "Q4rRL34CEeE",
        ExportName = "sinf",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcSinf(CpuContext ctx) =>
        ReturnSingle(ctx, MathF.Sin, TrigSingleError);

    [SysAbiExport(
        Nid = "T7uyNqP7vQA",
        ExportName = "tan",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcTan(CpuContext ctx) =>
        ReturnDouble(ctx, Math.Tan, TrigDoubleError);

    [SysAbiExport(
        Nid = "ZE6RNL+eLbk",
        ExportName = "tanf",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcTanf(CpuContext ctx) =>
        ReturnSingle(ctx, MathF.Tan, TrigSingleError);

    private static int ReturnDouble(
        CpuContext ctx,
        Func<double, double> operation,
        Func<double, uint>? explicitError = null)
    {
        ctx.GetXmmRegister(0, out var inputBits, out _);
        var input = BitConverter.Int64BitsToDouble(unchecked((long)inputBits));
        var error = explicitError?.Invoke(input) ?? 0;
        if (error != 0)
        {
            RaiseMathError(ctx, error);
        }

        var result = operation(input);
        ctx.SetXmmRegister(
            0,
            unchecked((ulong)BitConverter.DoubleToInt64Bits(result)),
            0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int ReturnSingle(
        CpuContext ctx,
        Func<float, float> operation,
        Func<float, uint>? explicitError = null)
    {
        ctx.GetXmmRegister(0, out var inputBits, out _);
        var input = BitConverter.Int32BitsToSingle(unchecked((int)(uint)inputBits));
        var error = explicitError?.Invoke(input) ?? 0;
        if (error != 0)
        {
            RaiseMathError(ctx, error);
        }

        var result = operation(input);
        ctx.SetXmmRegister(
            0,
            unchecked((uint)BitConverter.SingleToInt32Bits(result)),
            0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static uint InverseTrigDoubleError(double input)
    {
        var magnitudeBits = unchecked((ulong)BitConverter.DoubleToInt64Bits(input)) & 0x7FFF_FFFF_FFFF_FFFFUL;
        return magnitudeBits > 0x3FF0_0000_0000_0000UL ? FloatingInvalid : 0;
    }

    private static uint InverseTrigSingleError(float input)
    {
        var magnitudeBits = unchecked((uint)BitConverter.SingleToInt32Bits(input)) & 0x7FFF_FFFFU;
        return magnitudeBits > 0x3F80_0000U ? FloatingInvalid : 0;
    }

    private static uint TrigDoubleError(double input) =>
        double.IsInfinity(input) ? FloatingInvalid : 0;

    private static uint TrigSingleError(float input) =>
        float.IsInfinity(input) ? FloatingInvalid : 0;

    private static uint ExpDoubleError(double input)
    {
        if (!double.IsFinite(input))
        {
            return 0;
        }

        if (input > ExpOverflowThreshold)
        {
            return FloatingOverflow;
        }

        return input < ExpUnderflowThreshold ? FloatingUnderflow : 0;
    }

    private static uint Exp2DoubleError(double input)
    {
        if (!double.IsFinite(input))
        {
            return 0;
        }

        if (input >= 1024.0)
        {
            return FloatingOverflow;
        }

        return input <= -1075.0 ? FloatingUnderflow : 0;
    }

    private static uint ExpSingleError(float input)
    {
        if (!float.IsFinite(input))
        {
            return 0;
        }

        if (input > ExpfOverflowThreshold)
        {
            return FloatingOverflow;
        }

        return input < ExpfUnderflowThreshold ? FloatingUnderflow : 0;
    }

    private static uint Exp2SingleError(float input)
    {
        if (!float.IsFinite(input))
        {
            return 0;
        }

        if (input >= 128.0f)
        {
            return FloatingOverflow;
        }

        return input <= -150.0f ? FloatingUnderflow : 0;
    }

    private static double PowTwo(double input) => Math.Pow(2.0, input);

    private static float PowTwo(float input) => MathF.Pow(2.0f, input);

    private static uint LogDoubleError(double input)
    {
        if (input == 0.0)
        {
            return FloatingDivideByZero;
        }

        return input < 0.0 ? FloatingInvalid : 0;
    }

    private static uint LogSingleError(float input)
    {
        if (input == 0.0f)
        {
            return FloatingDivideByZero;
        }

        return input < 0.0f ? FloatingInvalid : 0;
    }

    private static uint NegativeDoubleError(double input) =>
        input < 0.0 ? FloatingInvalid : 0;

    private static uint NegativeSingleError(float input) =>
        input < 0.0f ? FloatingInvalid : 0;

    private static void RaiseMathError(CpuContext ctx, uint exception)
    {
        var raised = exception;
        if ((exception & (FloatingOverflow | FloatingUnderflow)) != 0)
        {
            raised |= FloatingInexact;
        }

        // Gen5 _Feraise stores EDOM for invalid operations and ERANGE for
        // divide-by-zero, overflow, or underflow before OR-ing the explicit
        // exception bits into MXCSR.
        var errno = (raised & FloatingInvalid) != 0 ? ErrnoDomain : ErrnoRange;
        _ = KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        ctx.Mxcsr |= raised & 0xFFFFU;

        // Do not synthesize ordinary FE_INEXACT here. Sony's polynomial/range
        // reduction and the host Math/MathF implementation can also differ in
        // their final rounded bit, so only the firmware's explicit _Feraise
        // paths above are reflected in guest state.
    }
}
