// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace SharpEmu.Libs.Audio;

public static class AudioOut2Exports
{
    // FMOD's PS5 backend allocates this ABI structure as four 16-byte lanes.
    // Clearing 0x80 bytes here overwrote the caller's stack canary immediately
    // following the 0x40-byte parameter block.
    private const int AudioOut2ContextParamSize = 0x40;
    private const int AudioOut2ErrorInvalidArgument = unchecked((int)0x80268001);
    private const int SpeakerArrayDescriptorSize = 0x28;
    private const int SpeakerArrayFooterSize = 0x18;
    private const uint MaximumSpeakerArrayCount = 0x20;
    private static long _nextContextHandle = 1;
    private static long _nextUserHandle = 1;
    private static int _nextPortId;
    private static long _pushTraceCount;

    // Per-context audio parameters captured at ContextCreate so ContextAdvance
    // can pace to the real playback cadence (grain samples at the sample rate).
    private static readonly ConcurrentDictionary<ulong, ContextState> Contexts = new();
    private static readonly ConcurrentDictionary<ulong, SpeakerArrayState> SpeakerArrays = new();

    private sealed class SpeakerArrayState
    {
        public SpeakerArrayState(
            ulong workspaceAddress,
            ulong workspaceSize,
            uint speakerCount,
            byte layout,
            int mode,
            uint coefficientConfiguration,
            bool coefficientFeature,
            byte[] positions)
        {
            WorkspaceAddress = workspaceAddress;
            WorkspaceSize = workspaceSize;
            SpeakerCount = speakerCount;
            Layout = layout;
            Mode = mode;
            CoefficientConfiguration = coefficientConfiguration;
            CoefficientFeature = coefficientFeature;
            Positions = positions;
        }

        public ulong WorkspaceAddress { get; }
        public ulong WorkspaceSize { get; }
        public uint SpeakerCount { get; }
        public byte Layout { get; }
        public int Mode { get; }
        public uint CoefficientConfiguration { get; }
        public bool CoefficientFeature { get; }
        public byte[] Positions { get; }
        public bool HasCoefficients => CoefficientConfiguration < 2;
    }

    private sealed class ContextState
    {
        private readonly object _paceGate = new();
        private long _nextAdvanceTimestamp;

        public ContextState(uint frequency, uint channels, uint grainSamples)
        {
            Frequency = frequency == 0 ? 48000 : frequency;
            Channels = channels == 0 ? 2 : channels;
            GrainSamples = grainSamples == 0 ? 256 : grainSamples;
        }

        public uint Frequency { get; }
        public uint Channels { get; }
        public uint GrainSamples { get; }

        // Blocks the advancing thread until one grain worth of wall-clock time
        // has elapsed since the previous advance, matching hardware timing so
        // audio-gated titles neither spin nor drift ahead.
        public void PaceAdvance()
        {
            long delay;
            lock (_paceGate)
            {
                var now = Stopwatch.GetTimestamp();
                if (_nextAdvanceTimestamp < now)
                {
                    _nextAdvanceTimestamp = now;
                }

                delay = _nextAdvanceTimestamp - now;
                _nextAdvanceTimestamp += checked(
                    (long)Math.Ceiling(Stopwatch.Frequency * (double)GrainSamples / Frequency));
            }

            if (delay > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds((double)delay / Stopwatch.Frequency));
            }
        }
    }

    [SysAbiExport(
        Nid = "g2tViFIohHE",
        ExportName = "sceAudioOut2Initialize",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2Initialize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Firmware 12.70 libSceAudioOut.sprx SHA-256
    // 948dfdc30b9c974c5447d9078853beb1555a2e548de6093b05a114e99445ab33:
    // G1YOKDJYX2Y at 0x4ec40 normalizes the two flags and delegates to the
    // shared speaker-array sizing routine at 0x4ef40. GTA V uses the returned
    // value directly as the size of a mandatory aligned allocation.
    [SysAbiExport(
        Nid = "G1YOKDJYX2Y",
        ExportName = "sceAudioOut2GetSpeakerArrayMemorySize",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2",
        PreferLle = true)]
    public static int AudioOut2GetSpeakerArrayMemorySize(CpuContext ctx)
    {
        var speakerCount = unchecked((uint)ctx[CpuRegister.Rdi]);
        var useObjectLayout = unchecked((uint)ctx[CpuRegister.Rsi]) != 0;
        var includeCoefficients = unchecked((uint)ctx[CpuRegister.Rdx]) != 0;
        var size = GetSpeakerArrayMemorySize(speakerCount, useObjectLayout, includeCoefficients);

        TraceAudioOut2(
            $"speaker-array-memory-size speakers={speakerCount} object-layout={useObjectLayout} " +
            $"coefficients={includeCoefficients} size=0x{size:X}");
        ctx[CpuRegister.Rax] = size;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Firmware 12.70 libSceAudioOut.sprx SHA-256
    // 948dfdc30b9c974c5447d9078853beb1555a2e548de6093b05a114e99445ab33:
    // +k91hoTuoA8 at 0x4ec60 delegates to FUN_0004efd0. The public ABI is
    // (outHandle, descriptor, auxiliary); the wrapper replaces RCX with a
    // provider-private feature byte, so callers such as GTA may leave RCX stale.
    [SysAbiExport(
        Nid = "+k91hoTuoA8",
        ExportName = "sceAudioOut2SpeakerArrayCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2",
        PreferLle = true)]
    public static int AudioOut2SpeakerArrayCreate(CpuContext ctx)
    {
        var outHandleAddress = ctx[CpuRegister.Rdi];
        var descriptorAddress = ctx[CpuRegister.Rsi];
        var auxiliaryAddress = ctx[CpuRegister.Rdx];
        if (outHandleAddress == 0 || descriptorAddress == 0)
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        Span<byte> descriptor = stackalloc byte[SpeakerArrayDescriptorSize];
        if (!ctx.Memory.TryRead(descriptorAddress, descriptor))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var positionsAddress = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[0x00..]);
        var speakerCount = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[0x08..]);
        var layout = descriptor[0x0C];
        var workspaceAddress = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[0x10..]);
        var workspaceSize = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[0x18..]);
        var mode = BinaryPrimitives.ReadInt32LittleEndian(descriptor[0x20..]);
        var modeParameter = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(descriptor[0x24..]));

        if (positionsAddress == 0 ||
            workspaceAddress == 0 ||
            workspaceSize == 0 ||
            speakerCount > MaximumSpeakerArrayCount ||
            (mode == 1 && (!float.IsFinite(modeParameter) || modeParameter < 0.0f)))
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        var coefficientConfiguration = uint.MaxValue;
        var coefficientFeature = false;
        if (auxiliaryAddress != 0)
        {
            Span<byte> auxiliaryConfiguration = stackalloc byte[sizeof(uint)];
            if (!ctx.Memory.TryRead(auxiliaryAddress, auxiliaryConfiguration))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            coefficientConfiguration = BinaryPrimitives.ReadUInt32LittleEndian(auxiliaryConfiguration);
            if (coefficientConfiguration < 2)
            {
                // Firmware 12.70's SDK gate is active, so coefficient creation
                // also consumes the feature byte at auxiliary +4.
                Span<byte> feature = stackalloc byte[1];
                if (auxiliaryAddress > ulong.MaxValue - sizeof(uint) ||
                    !ctx.Memory.TryRead(auxiliaryAddress + sizeof(uint), feature))
                {
                    return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                coefficientFeature = feature[0] != 0;
            }
        }

        var includeCoefficients = coefficientConfiguration < 2;
        var requiredSize = GetSpeakerArrayMemorySize(speakerCount, layout != 0, includeCoefficients);
        if (workspaceSize < requiredSize ||
            workspaceSize < SpeakerArrayFooterSize ||
            workspaceAddress > ulong.MaxValue - (workspaceSize - SpeakerArrayFooterSize))
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        var positionsSize = checked((int)(speakerCount * 3U * sizeof(float)));
        var positions = new byte[positionsSize];
        if (positions.Length != 0 && !ctx.Memory.TryRead(positionsAddress, positions))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var handle = workspaceAddress + workspaceSize - SpeakerArrayFooterSize;
        Span<byte> footer = stackalloc byte[SpeakerArrayFooterSize];
        footer.Clear();
        // The provider stores opaque primary/secondary implementation pointers
        // in the first two qwords. HLE owns equivalent host state instead, so it
        // leaves those pointers null while preserving the proven mode/layout ABI.
        BinaryPrimitives.WriteInt32LittleEndian(footer[0x10..], mode);
        footer[0x14] = layout;

        if (!ctx.Memory.TryWrite(handle, footer) || !TryWriteUInt64(ctx, outHandleAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        SpeakerArrays[handle] = new SpeakerArrayState(
            workspaceAddress,
            workspaceSize,
            speakerCount,
            layout,
            mode,
            coefficientConfiguration,
            coefficientFeature,
            positions);
        TraceAudioOut2(
            $"speaker-array-create handle=0x{handle:X} speakers={speakerCount} layout={layout} " +
            $"mode={mode} coefficients={includeCoefficients} workspace=0x{workspaceAddress:X}+0x{workspaceSize:X}");
        return SetReturn(ctx, 0);
    }

    // Firmware wrapper 28QqMnuuJ9Y at 0x4ee10 delegates to FUN_0004f540.
    // GTA's mode-zero speaker array requests all 36 fifth-order rows as indices
    // 64..99, with two floats per row. Exact decoder synthesis remains provider
    // work; zero-initializing every requested row is a deterministic progression
    // fallback and avoids exposing stale guest stack data as audio coefficients.
    [SysAbiExport(
        Nid = "28QqMnuuJ9Y",
        ExportName = "sceAudioOut2GetSpeakerArrayAmbisonicsCoefficients",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2",
        PreferLle = true)]
    public static int AudioOut2GetSpeakerArrayAmbisonicsCoefficients(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var coefficientIndex = unchecked((uint)ctx[CpuRegister.Rsi]);
        var outputAddress = ctx[CpuRegister.Rdx];
        var speakerCount = unchecked((uint)ctx[CpuRegister.Rcx]);
        if (handle == 0 ||
            outputAddress == 0 ||
            !SpeakerArrays.TryGetValue(handle, out var state) ||
            !state.HasCoefficients ||
            state.SpeakerCount != speakerCount ||
            !IsValidAmbisonicsCoefficientIndex(state.Mode, coefficientIndex))
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        Span<byte> coefficients = stackalloc byte[checked((int)speakerCount * sizeof(float))];
        coefficients.Clear();
        if (!ctx.Memory.TryWrite(outputAddress, coefficients))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetReturn(ctx, 0);
    }

    // Firmware wrapper erCWQR5eKiQ at 0x4ecf0 delegates to FUN_0004f3b0,
    // which rejects null and tears down both the primary and optional decoder.
    [SysAbiExport(
        Nid = "erCWQR5eKiQ",
        ExportName = "sceAudioOut2SpeakerArrayDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2",
        PreferLle = true)]
    public static int AudioOut2SpeakerArrayDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (handle == 0 || !SpeakerArrays.TryRemove(handle, out _))
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        TraceAudioOut2($"speaker-array-destroy handle=0x{handle:X}");
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "t5YrizufpQc",
        ExportName = "sceAudioOut2ContextResetParam",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextResetParam(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        param.Clear();
        // Firmware 12.70 t5YrizufpQc at 0x11050 copies the 16-byte default
        // block at 0x5e160, stores 0x100 at +0x10, and clears through +0x3f.
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x00..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x10..], 0x100);

        return ctx.Memory.TryWrite(paramAddress, param)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "pDmme7Bgm6E",
        ExportName = "sceAudioOut2ContextQueryMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextQueryMemory(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memorySizeAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0 || memorySizeAddress == 0)
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        if (!ctx.Memory.TryRead(paramAddress, param))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var bedCount = BinaryPrimitives.ReadUInt32LittleEndian(param[0x00..]);
        var objectCount = BinaryPrimitives.ReadUInt32LittleEndian(param[0x04..]);
        var reservedObjectCount = BinaryPrimitives.ReadUInt32LittleEndian(param[0x08..]);
        var busCount = BinaryPrimitives.ReadUInt32LittleEndian(param[0x0C..]);
        var mode = BinaryPrimitives.ReadUInt32LittleEndian(param[0x10..]);
        var objectMode = BinaryPrimitives.ReadUInt32LittleEndian(param[0x14..]);

        // Firmware 12.70 pDmme7Bgm6E at 0x2a6b0 validates this public
        // parameter block, normalizes it at 0x2a7a0, and calls +8fuZ1rh4PA
        // with RDX equal to the caller's single uint64_t output. The latter
        // clears RCX before entering the sizing routine at 0xe330, so there is
        // no alignment/secondary output. Writing a fabricated structure here
        // used to overwrite GTA V's adjacent stack canary.
        if (mode < 0x100 || (mode & 0xFF) != 0 ||
            bedCount > 0x20 || reservedObjectCount > objectCount || busCount == 0 ||
            (objectCount != 0 && reservedObjectCount != 0) ||
            (objectCount != 0 && objectMode is not (1 or 2)))
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        // The provider gates larger modes on hardware capability globals.
        // GTA uses mode 0x100; fail closed for modes that need those gates.
        if ((objectCount == 0 && mode > 0x800) ||
            (objectCount != 0 && mode > 0x400))
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        var normalizedObjectCount = Math.Min(objectCount, 0x80U);
        var normalizedBusCount = (ulong)(mode >> 8) * (busCount + 1UL) - 1UL;
        if (normalizedBusCount > 0x40)
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        const uint builtInVoiceCount = 0x15;
        var memorySize = ((ulong)bedCount + normalizedObjectCount + builtInVoiceCount) * 0xB60UL
            + GetAudioOut2DescriptorSize(bedCount)
            + GetAudioOut2DescriptorSize(normalizedObjectCount)
            + GetAudioOut2DescriptorSize(builtInVoiceCount)
            + AlignUp((ulong)normalizedObjectCount * 0x18UL, 0x80UL);

        Span<byte> sizeBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(sizeBytes, memorySize);
        TraceAudioOut2(
            $"context-query-memory beds={bedCount} objects={objectCount} buses={busCount} " +
            $"mode=0x{mode:X} object-mode={objectMode} size=0x{memorySize:X}");

        return ctx.Memory.TryWrite(memorySizeAddress, sizeBytes)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "0x6o1VVAYSY",
        ExportName = "sceAudioOut2ContextCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextCreate(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memoryAddress = ctx[CpuRegister.Rsi];
        var memorySize = ctx[CpuRegister.Rdx];
        var outContextAddress = ctx[CpuRegister.Rcx];
        if (paramAddress == 0 || memoryAddress == 0 || memorySize == 0 || outContextAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Read channels/frequency/grain from the reset-param blob so the
        // context can pace advances to the real audio cadence.
        uint channels = 2;
        uint frequency = 48000;
        uint grain = 256;
        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        if (ctx.Memory.TryRead(paramAddress, param))
        {
            var pc = BinaryPrimitives.ReadUInt32LittleEndian(param[0x04..]);
            var pf = BinaryPrimitives.ReadUInt32LittleEndian(param[0x08..]);
            var pg = BinaryPrimitives.ReadUInt32LittleEndian(param[0x0C..]);
            if (pc is > 0 and <= 8) channels = pc;
            if (pf is >= 8000 and <= 192000) frequency = pf;
            // Values below one cache line are flags/counts in observed PS5
            // callers, not audio grains. Keep the hardware-sized default.
            if (pg is >= 64 and <= 0x4000) grain = pg;
            TraceAudioOut2($"context-param address=0x{paramAddress:X} bytes={Convert.ToHexString(param)}");
        }

        var handle = (ulong)Interlocked.Increment(ref _nextContextHandle);
        Contexts[handle] = new ContextState(frequency, channels, grain);
        TraceAudioOut2($"context-create handle=0x{handle:X} frequency={frequency} channels={channels} grain={grain} memory=0x{memoryAddress:X} size=0x{memorySize:X}");
        return TryWriteUInt64(ctx, outContextAddress, handle)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "on6ZH7Abo10",
        ExportName = "sceAudioOut2ContextDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextDestroy(CpuContext ctx)
    {
        Contexts.TryRemove(ctx[CpuRegister.Rdi], out _);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "DxGyV8dtOR8",
        ExportName = "sceAudioOut2ContextBedWrite",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextBedWrite(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "aII9h5nli9U",
        ExportName = "sceAudioOut2ContextPush",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextPush(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var traceCount = Interlocked.Increment(ref _pushTraceCount);
        if (traceCount <= 16)
        {
            TraceAudioOut2($"context-push count={traceCount} rdi=0x{handle:X} rsi=0x{ctx[CpuRegister.Rsi]:X} rdx=0x{ctx[CpuRegister.Rdx]:X} rcx=0x{ctx[CpuRegister.Rcx]:X}");
        }

        if (Contexts.TryGetValue(handle, out var context))
        {
            // FMOD's PS5 output path uses ContextPush as the submission clock
            // and does not call ContextAdvance. Pace pushes to one hardware
            // grain so the feeder cannot outrun playback and starve the game.
            context.PaceAdvance();
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "PE2zHMqLSHs",
        ExportName = "sceAudioOut2ContextAdvance",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextAdvance(CpuContext ctx)
    {
        // Advancing renders one grain of audio on hardware; pace it to the same
        // wall-clock cadence so the guest audio thread runs at the right speed.
        if (Contexts.TryGetValue(ctx[CpuRegister.Rdi], out var context))
        {
            context.PaceAdvance();
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "R7d0F1g2qsU",
        ExportName = "sceAudioOut2ContextGetQueueLevel",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextGetQueueLevel(CpuContext ctx)
    {
        // Firmware 12.70 libSceAudioOut.sprx SHA-256
        // 948dfdc30b9c974c5447d9078853beb1555a2e548de6093b05a114e99445ab33:
        // R7d0F1g2qsU at 0x2b3f0 enters the shared implementation at 0x2c260,
        // whose two optional outputs are uint32_t pointers. GTA V places the
        // first output four bytes before its stack canary, so an eight-byte
        // store here corrupts the canary.
        //
        // The advance path paces synchronously, so both queue values are zero.
        var levelAddress = ctx[CpuRegister.Rsi];
        var availableAddress = ctx[CpuRegister.Rdx];
        if (levelAddress == 0 && availableAddress == 0)
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        if ((levelAddress != 0 && !TryWriteUInt32(ctx, levelAddress, 0)) ||
            (availableAddress != 0 && !TryWriteUInt32(ctx, availableAddress, 0)))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "JK2wamZPzwM",
        ExportName = "sceAudioOut2PortCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortCreate(CpuContext ctx)
    {
        var type = unchecked((int)ctx[CpuRegister.Rdi]);
        var paramAddress = ctx[CpuRegister.Rsi];
        var outPortAddress = ctx[CpuRegister.Rdx];
        var contextAddress = ctx[CpuRegister.Rcx];
        if (type < 0 || type > 255 || paramAddress == 0 || outPortAddress == 0 || contextAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var portId = unchecked((uint)Interlocked.Increment(ref _nextPortId)) & 0xFF;
        var handle = 0x2000_0000UL | ((ulong)(uint)type << 16) | portId;
        return TryWriteUInt64(ctx, outPortAddress, handle)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "8XTArSPyWHk",
        ExportName = "sceAudioOut2PortSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortSetAttributes(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "gatEUKG+Ea4",
        ExportName = "sceAudioOut2PortGetState",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortGetState(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var stateAddress = ctx[CpuRegister.Rsi];
        if (handle == 0 || stateAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var type = (int)((handle >> 16) & 0xFF);
        Span<byte> state = stackalloc byte[0x20];
        state.Clear();
        var output = type == 2 ? 0x40 : 0x01;
        var channels = type == 2 ? 1 : 2;
        BinaryPrimitives.WriteUInt16LittleEndian(state[0x00..], unchecked((ushort)output));
        state[0x02] = unchecked((byte)channels);
        BinaryPrimitives.WriteInt16LittleEndian(state[0x04..], -1);

        return ctx.Memory.TryWrite(stateAddress, state)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "DImz2Ft9E2g",
        ExportName = "sceAudioOut2GetSpeakerInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSpeakerInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> info = stackalloc byte[0x40];
        info.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x00..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x04..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x08..], 48000);

        return ctx.Memory.TryWrite(infoAddress, info)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "cd+Rtw+D1x8",
        ExportName = "sceAudioOut2PortDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortDestroy(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "IaZXJ9M79uo",
        ExportName = "sceAudioOut2UserDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserDestroy(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "xywYcRB7nbQ",
        ExportName = "sceAudioOut2UserCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserCreate(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var outUserAddress = ctx[CpuRegister.Rsi];
        if ((userId != 0 && userId != 1 && userId != 1000 && userId != 0x10000000 && userId != 255) ||
            outUserAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = (ulong)Interlocked.Increment(ref _nextUserHandle);
        return TryWriteUInt64(ctx, outUserAddress, handle)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool TryWriteUInt64(CpuContext ctx, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static ulong GetSpeakerArrayMemorySize(
        uint speakerCount,
        bool useObjectLayout,
        bool includeCoefficients)
    {
        // Ghidra 0x4ef40. The public export always supplies zero for the
        // private fourth parameter, selecting (3, 7) for the object layout.
        var size = useObjectLayout
            ? GetObjectSpeakerArrayBaseSize(speakerCount) + 0xA0UL
            : GetStandardSpeakerArrayBaseSize(speakerCount) + 0x80UL;

        if (!includeCoefficients)
        {
            return size + 0x100UL;
        }

        var coefficientBytes = GetAmbisonicsCoefficientBytes(speakerCount, 5, 0xF0);
        return size + 0x1A0UL + AlignUp32(coefficientBytes + 0x100UL);
    }

    private static ulong GetStandardSpeakerArrayBaseSize(uint speakerCount)
    {
        // Ghidra 0x3f790.
        var countPlusOne = (ulong)unchecked(speakerCount + 1U);
        return AlignUp32(countPlusOne * 8UL) +
               ((ulong)(unchecked(speakerCount + 8U) & ~7U) * 4UL) +
               AlignUp32(countPlusOne * 2UL) +
               AlignUp32(countPlusOne * 0x10UL);
    }

    private static ulong GetObjectSpeakerArrayBaseSize(uint speakerCount)
    {
        // Ghidra 0x41a00 with its public-export constants param2=3,param3=7.
        const uint objectCount = 3;
        const uint objectStrideSelector = 7;
        var totalCount = unchecked(speakerCount + objectCount);
        var lowCount = totalCount & 0xFFFFU;
        var expandedCount = lowCount < 3U ? lowCount : unchecked((lowCount * 2U) - 4U);

        var size = GetSpeakerMixWorkspaceSize(lowCount) + 0x60UL +
                   AlignUp32((ulong)totalCount * 0xCUL) +
                   ((ulong)(unchecked(speakerCount + objectCount + 7U) & ~7U) * 4UL) +
                   AlignUp32((ulong)expandedCount * 6UL) +
                   AlignUp32((ulong)expandedCount * 0x30UL);

        size += ((objectStrideSelector * 2UL) + 0x18UL) * objectCount;
        var lastIndex = unchecked(totalCount - 1U);
        if (lastIndex > 0x1FU)
        {
            size += ((lastIndex >> 3) & 0xFFFF_FFFCUL) + 4UL;
        }

        return AlignUp32(size);
    }

    private static ulong GetSpeakerMixWorkspaceSize(uint count)
    {
        // Ghidra 0x44110.
        var smallCount = count < 3U;
        var doubled = smallCount ? count : unchecked((count * 2U) - 4U);
        var tripled = smallCount ? count : unchecked((count * 3U) - 6U);
        return AlignUp32((ulong)count * 2UL) +
               AlignUp32((ulong)tripled * 4UL) +
               AlignUp32((ulong)doubled * 6UL);
    }

    private static ulong GetAmbisonicsCoefficientBytes(uint speakerCount, uint order, uint stride)
    {
        // Ghidra 0x45d40.
        var alignedSpeakers = unchecked(speakerCount + 7U) & ~7U;
        var alignedStride = unchecked(stride + 7U) & ~7U;
        var coefficientCount = unchecked((order + 1U) * (order + 1U));
        return ((ulong)stride * alignedSpeakers +
                ((ulong)alignedStride + alignedSpeakers) * coefficientCount) * 4UL;
    }

    private static bool IsValidAmbisonicsCoefficientIndex(int mode, uint coefficientIndex) =>
        mode switch
        {
            0 => coefficientIndex is >= 0x40 and <= 0x63,
            1 => coefficientIndex < 0x10,
            _ => coefficientIndex < 0x10 || coefficientIndex is >= 0x40 and <= 0x63,
        };

    internal static void ResetSpeakerArraysForTests() => SpeakerArrays.Clear();

    internal static int SpeakerArrayCountForTests => SpeakerArrays.Count;

    // Firmware 12.70 FUN_0000dc90 at 0xdc90.
    private static ulong GetAudioOut2DescriptorSize(uint count) =>
        (((ulong)count + 0x1FUL) >> 5) * 4UL + 0xCUL;

    private static ulong AlignUp(ulong value, ulong alignment) =>
        unchecked(value + alignment - 1UL) & ~(alignment - 1UL);

    private static ulong AlignUp32(ulong value) => unchecked(value + 0x1FUL) & ~0x1FUL;

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static void TraceAudioOut2(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AUDIO_OUT2"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] audio_out2.{message}");
        }
    }
}
