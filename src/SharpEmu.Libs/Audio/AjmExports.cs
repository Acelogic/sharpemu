// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.Libs.Audio;

public static class AjmExports
{
    private const int OrbisAjmErrorInvalidContext = unchecked((int)0x80930002);
    private const int OrbisAjmErrorInvalidInstance = unchecked((int)0x80930003);
    private const int OrbisAjmErrorInvalidParameter = unchecked((int)0x80930005);
    private const int OrbisAjmErrorOutOfResources = unchecked((int)0x80930007);
    private const int OrbisAjmErrorCodecAlreadyRegistered = unchecked((int)0x80930009);
    private const int OrbisAjmErrorCodecNotRegistered = unchecked((int)0x8093000A);
    private const int OrbisAjmErrorWrongRevisionFlag = unchecked((int)0x8093000B);
    private const int OrbisAjmErrorMalformedBatch = unchecked((int)0x80930011);
    private const int OrbisAjmErrorJobCreation = unchecked((int)0x80930012);
    private const int AjmBatchHeaderSize = 0x28;
    private const int AjmDecodeDescriptorSize = 0x40;
    private const uint MaxCompatDecodeOutputSize = 16 * 1024 * 1024;
    private const uint MaxCompatDecodeSidebandSize = 64 * 1024;
    private const uint MaxCodecType = 23;
    private const int MaxInstanceIndex = 0x2FFF;
    private static readonly ConcurrentDictionary<uint, AjmContextState> Contexts = new();
    private static int _nextContextId;
    private static long _jobDecodeTraceCount;

    private sealed class AjmContextState
    {
        public object Gate { get; } = new();

        public HashSet<uint> RegisteredCodecs { get; } = new();

        public Dictionary<uint, uint> InstancesBySlot { get; } = new();

        public HashSet<uint> CompletedBatches { get; } = new();

        public int NextInstanceIndex { get; set; }

        public uint NextBatchId { get; set; }
    }

    public static int AjmInitialize(CpuContext ctx)
    {
        var reserved = ctx[CpuRegister.Rdi];
        var outputAddress = ctx[CpuRegister.Rsi];
        if (reserved != 0 || outputAddress == 0)
        {
            return unchecked((int)0x806A0001);
        }

        var contextId = unchecked((uint)Interlocked.Increment(ref _nextContextId));
        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, contextId);
        if (!ctx.Memory.TryWrite(outputAddress, value))
        {
            return unchecked((int)0x806A0001);
        }

        Contexts[contextId] = new AjmContextState();
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ajm.initialize reserved={reserved} out=0x{outputAddress:X16} context={contextId}");
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "MHur6qCsUus",
        ExportName = "sceAjmFinalize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmFinalize(CpuContext ctx)
    {
        Contexts.TryRemove(unchecked((uint)ctx[CpuRegister.Rdi]), out _);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "Q3dyFuwGn64",
        ExportName = "sceAjmModuleRegister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmModuleRegister(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var codecType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var reserved = ctx[CpuRegister.Rdx];
        if (codecType >= MaxCodecType || reserved != 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        lock (state.Gate)
        {
            if (!state.RegisteredCodecs.Add(codecType))
            {
                return ctx.SetReturn(OrbisAjmErrorCodecAlreadyRegistered);
            }
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ajm.module_register context={contextId} codec={codecType} reserved={reserved}");
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "AxoDrINp4J8",
        ExportName = "sceAjmInstanceCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmInstanceCreate(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var codecType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var flags = ctx[CpuRegister.Rdx];
        var outputAddress = ctx[CpuRegister.Rcx];
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        if (codecType >= MaxCodecType || outputAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if ((flags & 0x7) == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorWrongRevisionFlag);
        }

        uint instanceId;
        lock (state.Gate)
        {
            if (!state.RegisteredCodecs.Contains(codecType))
            {
                return ctx.SetReturn(OrbisAjmErrorCodecNotRegistered);
            }

            if (state.InstancesBySlot.Count >= MaxInstanceIndex)
            {
                return ctx.SetReturn(OrbisAjmErrorOutOfResources);
            }

            var nextInstanceIndex = state.NextInstanceIndex;
            uint instanceSlot;
            do
            {
                nextInstanceIndex = nextInstanceIndex % MaxInstanceIndex + 1;
                instanceSlot = unchecked((uint)nextInstanceIndex);
            }
            while (state.InstancesBySlot.ContainsKey(instanceSlot));

            instanceId = (codecType << 14) | instanceSlot;
            Span<byte> value = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(value, instanceId);
            if (!ctx.Memory.TryWrite(outputAddress, value))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
            }

            state.NextInstanceIndex = nextInstanceIndex;
            state.InstancesBySlot.Add(instanceSlot, instanceId);
        }

        Trace($"instance_create context={contextId} codec={codecType} flags=0x{flags:X} instance=0x{instanceId:X8}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "RbLbuKv8zho",
        ExportName = "sceAjmInstanceDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmInstanceDestroy(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        var instanceSlot = instanceId & 0x3FFF;
        lock (state.Gate)
        {
            if (instanceSlot == 0 || !state.InstancesBySlot.Remove(instanceSlot))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidInstance);
            }
        }

        Trace($"instance_destroy context={contextId} instance=0x{instanceId:X8}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "Wi7DtlLV+KI",
        ExportName = "sceAjmModuleUnregister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmModuleUnregister(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "MmpF1XsQiHw",
        ExportName = "sceAjmBatchInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm",
        PreferLle = true)]
    public static int AjmBatchInitialize(CpuContext ctx)
    {
        // Ghidra 12.1.2, libSceAjm.native.sprx SHA-256
        // 4da65731b07fa2911b9468505b2f1fc0a56df7373356fdc1dfa886b00385d8d9,
        // export 0x0de0. RDI is descriptor storage, RSI is its capacity, and
        // RDX receives the exact five-qword builder consumed by JobDecode.
        var storageAddress = ctx[CpuRegister.Rdi];
        var capacity = ctx[CpuRegister.Rsi];
        var batchAddress = ctx[CpuRegister.Rdx];
        if (storageAddress == 0 || batchAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        Span<byte> batch = stackalloc byte[AjmBatchHeaderSize];
        batch.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(batch, storageAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(batch[0x10..], capacity);
        if (!ctx.Memory.TryWrite(batchAddress, batch))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        Trace(
            $"batch_initialize batch=0x{batchAddress:X16} storage=0x{storageAddress:X16} " +
            $"capacity=0x{capacity:X}");
        return ctx.SetReturn(0);
    }

    // Ghidra 12.1.2, libSceAjm.native.sprx SHA-256
    // 4da65731b07fa2911b9468505b2f1fc0a56df7373356fdc1dfa886b00385d8d9,
    // export 0x1a10. The provider only appends this exact 0x40-byte decode
    // descriptor; submission and completion belong to BatchStart/BatchWait.
    [SysAbiExport(
        Nid = "39WxhR-ePew",
        ExportName = "sceAjmBatchJobDecode",
        Target = Generation.Gen5,
        LibraryName = "libSceAjm",
        PreferLle = true)]
    public static int AjmBatchJobDecode(CpuContext ctx)
    {
        var batchAddress = ctx[CpuRegister.Rdi];
        if (batchAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        Span<byte> batch = stackalloc byte[AjmBatchHeaderSize];
        if (!ctx.Memory.TryRead(batchAddress, batch))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        var descriptorBase = BinaryPrimitives.ReadUInt64LittleEndian(batch);
        var cursor = BinaryPrimitives.ReadUInt64LittleEndian(batch[0x08..]);
        var capacity = BinaryPrimitives.ReadUInt64LittleEndian(batch[0x10..]);
        var nextCursor = unchecked(cursor + AjmDecodeDescriptorSize);

        // The provider advances the cursor before checking capacity and does
        // not roll it back when the batch is too small.
        if (!ctx.TryWriteUInt64(batchAddress + 0x08, nextCursor))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if (nextCursor > capacity)
        {
            TraceJobDecode(
                "capacity",
                batchAddress,
                descriptorBase,
                cursor,
                capacity,
                nextCursor);
            return ctx.SetReturn(OrbisAjmErrorJobCreation);
        }

        var descriptorAddress = unchecked(descriptorBase + cursor);
        if (!ctx.TryWriteUInt64(batchAddress + 0x18, descriptorAddress) ||
            !ctx.TryWriteUInt64(batchAddress + 0x20, 0) ||
            !ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong), out var sidebandAddress))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        Span<byte> descriptor = stackalloc byte[AjmDecodeDescriptorSize];
        if (!ctx.Memory.TryRead(descriptorAddress, descriptor))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var inputAddress = ctx[CpuRegister.Rdx];
        var inputSize = unchecked((uint)ctx[CpuRegister.Rcx]);
        var outputAddress = ctx[CpuRegister.R8];
        var outputSize = unchecked((uint)ctx[CpuRegister.R9]);

        WriteMaskedDescriptorWord(descriptor, 0x00, 0xFC000030u, (instanceId & 0xFFFFFu) << 6);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[0x04..], 0x38);
        WriteMaskedDescriptorWord(descriptor, 0x08, 0xFFFFFFE0u, 0x01);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[0x0C..], inputSize);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x10..], inputAddress);
        WriteMaskedDescriptorWord(descriptor, 0x18, 0xFC000030u, 0x00200004);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[0x1C..], 0x1000);
        WriteMaskedDescriptorWord(descriptor, 0x20, 0xFFFFFFE0u, 0x11);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[0x24..], outputSize);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x28..], outputAddress);
        WriteMaskedDescriptorWord(descriptor, 0x30, 0xFFFFFFE0u, 0x12);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[0x34..], 0x20);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x38..], sidebandAddress);

        if (!ctx.Memory.TryWrite(descriptorAddress, descriptor))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        TraceJobDecode(
            "append",
            batchAddress,
            descriptorBase,
            cursor,
            capacity,
            nextCursor);
        return ctx.SetReturn(0);
    }

    // Ghidra 12.1.2, libSceAjm.native.sprx SHA-256
    // 4da65731b07fa2911b9468505b2f1fc0a56df7373356fdc1dfa886b00385d8d9,
    // export 0x0e10. The firmware wrapper validates the 0x28-byte builder and
    // submits ioctl 0xc0288907. SharpEmu models that missing device as an
    // immediate, stateful completion queue and emits silence for decode jobs.
    [SysAbiExport(
        Nid = "5tOfnaClcqM",
        ExportName = "sceAjmBatchStart",
        Target = Generation.Gen5,
        LibraryName = "libSceAjm",
        PreferLle = true)]
    public static int AjmBatchStart(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var batchAddress = ctx[CpuRegister.Rsi];
        var priority = unchecked((uint)ctx[CpuRegister.Rdx]);
        var errorAddress = ctx[CpuRegister.Rcx];
        var batchIdAddress = ctx[CpuRegister.R8];
        if (batchAddress == 0 || batchIdAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        Span<byte> batch = stackalloc byte[AjmBatchHeaderSize];
        if (!ctx.Memory.TryRead(batchAddress, batch))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        var bufferAddress = BinaryPrimitives.ReadUInt64LittleEndian(batch);
        var usedBytes = BinaryPrimitives.ReadUInt64LittleEndian(batch[0x08..]);
        var capacityBytes = BinaryPrimitives.ReadUInt64LittleEndian(batch[0x10..]);
        var jobPositionA = BinaryPrimitives.ReadUInt64LittleEndian(batch[0x18..]);
        var jobPositionB = BinaryPrimitives.ReadUInt64LittleEndian(batch[0x20..]);
        if (bufferAddress == 0 || usedBytes == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if (usedBytes > capacityBytes)
        {
            if (errorAddress != 0 &&
                !WriteBatchError(ctx, errorAddress, OrbisAjmErrorJobCreation, jobPositionA, 0, jobPositionB))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
            }

            return ctx.SetReturn(OrbisAjmErrorMalformedBatch);
        }

        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        uint batchId;
        lock (state.Gate)
        {
            do
            {
                state.NextBatchId++;
                if (state.NextBatchId == 0)
                {
                    state.NextBatchId++;
                }

                batchId = state.NextBatchId;
            }
            while (state.CompletedBatches.Contains(batchId));

            if (!ctx.TryWriteUInt32(batchIdAddress, batchId))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
            }

            state.CompletedBatches.Add(batchId);
        }

        var decodeJobs = CompleteDecodeJobsWithSilence(ctx, bufferAddress, usedBytes);
        Trace(
            $"batch_start context={contextId} batch={batchId} priority={priority} " +
            $"buffer=0x{bufferAddress:X16} used=0x{usedBytes:X} decode_jobs={decodeJobs}");
        return ctx.SetReturn(0);
    }

    // Ghidra 12.1.2, libSceAjm.sprx SHA-256
    // c7f70f6582a315df1f1f5541100bf83c78f179ce8736e6a2dca39acc0ee1c95e,
    // export 0x0880. The firmware wrapper submits ioctl 0xc0288908. Completed
    // HLE batches are consumed here exactly once; timeout is irrelevant for an
    // already-complete batch.
    [SysAbiExport(
        Nid = "-qLsfDAywIY",
        ExportName = "sceAjmBatchWait",
        Target = Generation.Gen5,
        LibraryName = "libSceAjm",
        PreferLle = true)]
    public static int AjmBatchWait(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var batchId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var timeout = unchecked((uint)ctx[CpuRegister.Rdx]);
        var errorAddress = ctx[CpuRegister.Rcx];
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        lock (state.Gate)
        {
            if (batchId == 0 || !state.CompletedBatches.Remove(batchId))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
            }
        }

        Trace(
            $"batch_wait context={contextId} batch={batchId} timeout={timeout} " +
            $"error=0x{errorAddress:X16}");
        return ctx.SetReturn(0);
    }

    internal static void ResetForTests()
    {
        Contexts.Clear();
        Interlocked.Exchange(ref _nextContextId, 0);
        Interlocked.Exchange(ref _jobDecodeTraceCount, 0);
    }

    private static void Trace(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] ajm.{message}");
        }
    }

    private static void TraceJobDecode(
        string outcome,
        ulong batchAddress,
        ulong descriptorBase,
        ulong cursor,
        ulong capacity,
        ulong nextCursor)
    {
        var sample = Interlocked.Increment(ref _jobDecodeTraceCount);
        if (sample > 8 && sample % 10000 != 0)
        {
            return;
        }

        Trace(
            $"batch_job_decode outcome={outcome} sample={sample} batch=0x{batchAddress:X16} " +
            $"base=0x{descriptorBase:X16} cursor=0x{cursor:X} capacity=0x{capacity:X} next=0x{nextCursor:X}");
    }

    private static void WriteMaskedDescriptorWord(
        Span<byte> descriptor,
        int offset,
        uint preservedMask,
        uint value)
    {
        var existing = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[offset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            descriptor[offset..],
            (existing & preservedMask) | value);
    }

    private static bool WriteBatchError(
        CpuContext ctx,
        ulong errorAddress,
        int result,
        ulong jobPositionA,
        uint jobIndex,
        ulong jobPositionB) =>
        ctx.TryWriteUInt32(errorAddress, unchecked((uint)result)) &&
        ctx.TryWriteUInt64(errorAddress + 0x08, jobPositionA) &&
        ctx.TryWriteUInt32(errorAddress + 0x10, jobIndex) &&
        ctx.TryWriteUInt64(errorAddress + 0x18, jobPositionB);

    private static int CompleteDecodeJobsWithSilence(
        CpuContext ctx,
        ulong bufferAddress,
        ulong usedBytes)
    {
        if (usedBytes < AjmDecodeDescriptorSize)
        {
            return 0;
        }

        var completed = 0;
        Span<byte> descriptor = stackalloc byte[AjmDecodeDescriptorSize];
        for (ulong offset = 0; offset <= usedBytes - AjmDecodeDescriptorSize; offset += sizeof(ulong))
        {
            if (!ctx.Memory.TryRead(unchecked(bufferAddress + offset), descriptor) ||
                BinaryPrimitives.ReadUInt32LittleEndian(descriptor[0x04..]) != 0x38 ||
                (BinaryPrimitives.ReadUInt32LittleEndian(descriptor[0x08..]) & 0x1F) != 0x01 ||
                BinaryPrimitives.ReadUInt32LittleEndian(descriptor[0x1C..]) != 0x1000 ||
                (BinaryPrimitives.ReadUInt32LittleEndian(descriptor[0x20..]) & 0x1F) != 0x11 ||
                (BinaryPrimitives.ReadUInt32LittleEndian(descriptor[0x30..]) & 0x1F) != 0x12)
            {
                continue;
            }

            var outputSize = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[0x24..]);
            var outputAddress = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[0x28..]);
            var sidebandSize = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[0x34..]);
            var sidebandAddress = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[0x38..]);
            if (outputSize <= MaxCompatDecodeOutputSize)
            {
                _ = ZeroGuestMemory(ctx, outputAddress, outputSize);
            }

            if (sidebandSize <= MaxCompatDecodeSidebandSize)
            {
                _ = ZeroGuestMemory(ctx, sidebandAddress, sidebandSize);
            }

            completed++;
            offset += AjmDecodeDescriptorSize - sizeof(ulong);
        }

        return completed;
    }

    private static bool ZeroGuestMemory(CpuContext ctx, ulong address, uint size)
    {
        if (size == 0)
        {
            return true;
        }

        if (address == 0)
        {
            return false;
        }

        Span<byte> zeroes = stackalloc byte[4096];
        uint written = 0;
        while (written < size)
        {
            var count = unchecked((int)Math.Min((uint)zeroes.Length, size - written));
            if (!ctx.Memory.TryWrite(unchecked(address + written), zeroes[..count]))
            {
                return false;
            }

            written += unchecked((uint)count);
        }

        return true;
    }
}
