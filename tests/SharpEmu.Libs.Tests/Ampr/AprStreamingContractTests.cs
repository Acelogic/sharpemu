// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

public sealed class AprStreamingContractTests
{
    [Fact]
    public void ResolveWithEmptyPrefix_UsesRecoveredOutputWidthsAndLow32Count()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong prefixAddress = memoryBase + 0x100;
        const ulong pathListAddress = memoryBase + 0x200;
        const ulong pathAddress = memoryBase + 0x300;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong sizesAddress = memoryBase + 0x900;
        byte[] fileContents = [1, 3, 5, 7, 9];
        var hostPath = Path.GetTempFileName();

        try
        {
            File.WriteAllBytes(hostPath, fileContents);
            var memory = new FakeCpuMemory(memoryBase, 0x2000);
            var context = new CpuContext(memory, Generation.Gen5);
            memory.WriteCString(prefixAddress, string.Empty);
            memory.WriteCString(pathAddress, hostPath);
            WriteUInt64(memory, pathListAddress, pathAddress);
            context[CpuRegister.Rdi] = prefixAddress;
            context[CpuRegister.Rsi] = pathListAddress;
            context[CpuRegister.Rdx] = 0x1_0000_0001;
            context[CpuRegister.Rcx] = idsAddress;
            context[CpuRegister.R8] = sizesAddress;

            Assert.Equal(
                0,
                GtaVKernelContractExports.AprResolveFilepathsWithPrefixToIdsAndFileSizes(context));

            Assert.NotEqual(uint.MaxValue, ReadUInt32(memory, idsAddress));
            Assert.Equal((ulong)fileContents.Length, ReadUInt64(memory, sizesAddress));
        }
        finally
        {
            File.Delete(hostPath);
        }
    }

    [Fact]
    public void ResolveWithPrefix_CombinesPathsAndContinuesPastMissingFiles()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong prefixAddress = memoryBase + 0x100;
        const ulong pathListAddress = memoryBase + 0x200;
        const ulong firstPathAddress = memoryBase + 0x300;
        const ulong secondPathAddress = memoryBase + 0x400;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong sizesAddress = memoryBase + 0x900;
        byte[] fileContents = [2, 4, 6, 8];
        var temporaryDirectory = Directory.CreateTempSubdirectory("sharpemu-apr-prefix-");
        var hostPath = Path.Combine(temporaryDirectory.FullName, "asset.bin");

        try
        {
            File.WriteAllBytes(hostPath, fileContents);
            var memory = new FakeCpuMemory(memoryBase, 0x2000);
            var context = new CpuContext(memory, Generation.Gen5);
            memory.WriteCString(prefixAddress, temporaryDirectory.FullName + Path.DirectorySeparatorChar);
            memory.WriteCString(firstPathAddress, "asset.bin");
            memory.WriteCString(secondPathAddress, "missing.bin");
            WriteUInt64(memory, pathListAddress, firstPathAddress);
            WriteUInt64(memory, pathListAddress + sizeof(ulong), secondPathAddress);
            context[CpuRegister.Rdi] = prefixAddress;
            context[CpuRegister.Rsi] = pathListAddress;
            context[CpuRegister.Rdx] = 2;
            context[CpuRegister.Rcx] = idsAddress;
            context[CpuRegister.R8] = sizesAddress;

            Assert.Equal(
                0,
                GtaVKernelContractExports.AprResolveFilepathsWithPrefixToIdsAndFileSizes(context));

            Assert.NotEqual(uint.MaxValue, ReadUInt32(memory, idsAddress));
            Assert.Equal(uint.MaxValue, ReadUInt32(memory, idsAddress + sizeof(uint)));
            Assert.Equal((ulong)fileContents.Length, ReadUInt64(memory, sizesAddress));
            Assert.Equal(0UL, ReadUInt64(memory, sizesAddress + sizeof(ulong)));
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ResolveWithRequiredNullPointer_ReturnsKernelEfault(
        bool nullPrefix,
        bool nullPathList,
        bool nullSizes)
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong prefixAddress = memoryBase + 0x100;
        const ulong pathListAddress = memoryBase + 0x200;
        const ulong sizesAddress = memoryBase + 0x300;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = nullPrefix ? 0 : prefixAddress;
        context[CpuRegister.Rsi] = nullPathList ? 0 : pathListAddress;
        context[CpuRegister.Rdx] = 1;
        context[CpuRegister.R8] = nullSizes ? 0 : sizesAddress;

        Assert.Equal(
            unchecked((int)0x8002000E),
            GtaVKernelContractExports.AprResolveFilepathsWithPrefixToIdsAndFileSizes(context));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1025UL)]
    public void ResolveWithInvalidCount_ReturnsInvalidArgument(ulong count)
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = memoryBase + 0x100;
        context[CpuRegister.Rsi] = memoryBase + 0x200;
        context[CpuRegister.Rdx] = count;
        context[CpuRegister.R8] = memoryBase + 0x300;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            GtaVKernelContractExports.AprResolveFilepathsWithPrefixToIdsAndFileSizes(context));
    }

    [Fact]
    public void ResolveStatAndReadFile_UsesSharedAprFileId()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathListAddress = memoryBase + 0x100;
        const ulong pathAddress = memoryBase + 0x200;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong statAddress = memoryBase + 0x900;
        const ulong commandBufferAddress = memoryBase + 0x1000;
        const ulong recordBufferAddress = memoryBase + 0x1100;
        const ulong destinationAddress = memoryBase + 0x2000;
        const ulong stackAddress = memoryBase + 0x3000;
        byte[] fileContents = [10, 11, 12, 13, 14, 15, 16, 17];
        var hostPath = Path.GetTempFileName();

        try
        {
            File.WriteAllBytes(hostPath, fileContents);
            var memory = new FakeCpuMemory(memoryBase, 0x4000);
            var context = new CpuContext(memory, Generation.Gen5);
            memory.WriteCString(pathAddress, hostPath);
            WriteUInt64(memory, pathListAddress, pathAddress);

            context[CpuRegister.Rdi] = pathListAddress;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = idsAddress;

            Assert.Equal(0, KernelMemoryCompatExports.KernelAprResolveFilepathsToIds(context));

            Span<byte> idBytes = stackalloc byte[sizeof(uint)];
            Assert.True(memory.TryRead(idsAddress, idBytes));
            var fileId = BinaryPrimitives.ReadUInt32LittleEndian(idBytes);
            Assert.NotEqual(uint.MaxValue, fileId);

            context[CpuRegister.Rdi] = fileId;
            context[CpuRegister.Rsi] = statAddress;

            Assert.Equal(0, KernelMemoryCompatExports.KernelAprGetFileStat(context));

            Span<byte> stat = stackalloc byte[120];
            Assert.True(memory.TryRead(statAddress, stat));
            Assert.Equal(fileContents.Length, BinaryPrimitives.ReadInt64LittleEndian(stat[72..]));

            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rsi] = recordBufferAddress;
            context[CpuRegister.Rdx] = 0x100;

            Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
            Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));

            const ulong readOffset = 2;
            const ulong readSize = 4;
            WriteUInt64(memory, stackAddress + sizeof(ulong), readOffset);
            context[CpuRegister.Rsp] = stackAddress;
            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rcx] = fileId;
            context[CpuRegister.R8] = destinationAddress;
            context[CpuRegister.R9] = readSize;

            Assert.Equal(0, AmprExports.AprCommandBufferReadFile(context));

            Span<byte> destination = stackalloc byte[(int)readSize];
            Assert.True(memory.TryRead(destinationAddress, destination));
            Assert.Equal(fileContents.AsSpan((int)readOffset, (int)readSize), destination);

            Span<byte> record = stackalloc byte[0x30];
            Assert.True(memory.TryRead(recordBufferAddress, record));
            Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(record));
            Assert.Equal(fileId, BinaryPrimitives.ReadUInt32LittleEndian(record[0x04..]));
            Assert.Equal(destinationAddress, BinaryPrimitives.ReadUInt64LittleEndian(record[0x08..]));
            Assert.Equal(readSize, BinaryPrimitives.ReadUInt64LittleEndian(record[0x10..]));
            Assert.Equal(readOffset, BinaryPrimitives.ReadUInt64LittleEndian(record[0x18..]));
            Assert.Equal(readSize, BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]));
        }
        finally
        {
            File.Delete(hostPath);
        }
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
