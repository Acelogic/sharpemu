// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition("KernelFileCompatState", DisableParallelization = true)]
public sealed class KernelFileCompatStateCollection;

[Collection("KernelFileCompatState")]
public sealed class KernelFileCompatExportsTests : IDisposable
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong PathAddress = Base + 0x100;
    private const int OpenWriteCreateTruncate = 0x1 | 0x0200 | 0x0400;

    private readonly FakeCpuMemory _memory = new(Base, 0x20000);
    private readonly CpuContext _ctx;
    private readonly string _root;

    public KernelFileCompatExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _root = Path.Combine(Path.GetTempPath(), $"sharpemu-kernel-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        KernelMemoryCompatExports.ClearGuestPathMounts();
        KernelMemoryCompatExports.RegisterGuestPathMount("/savedata0", _root);
    }

    public void Dispose()
    {
        KernelMemoryCompatExports.ClearGuestPathMounts();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void KernelWrite_ReadsLibcBackedBuffer()
    {
        var guestPath = "/savedata0/metadata.bin\0"u8.ToArray();
        Assert.True(_memory.TryWrite(PathAddress, guestPath));
        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = OpenWriteCreateTruncate;
        Assert.Equal(0, KernelMemoryCompatExports.KernelOpenUnderscore(_ctx));
        var fd = unchecked((int)_ctx[CpuRegister.Rax]);
        Assert.True(fd > 2);

        var expected = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var payloadAddress = AllocateTracked(expected.Length);
        try
        {
            Marshal.Copy(expected, 0, unchecked((nint)payloadAddress), expected.Length);
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = payloadAddress;
            _ctx[CpuRegister.Rdx] = unchecked((ulong)expected.Length);
            Assert.Equal(0, KernelMemoryCompatExports.KernelWrite(_ctx));
            Assert.Equal(unchecked((ulong)expected.Length), _ctx[CpuRegister.Rax]);
        }
        finally
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            Assert.Equal(0, KernelMemoryCompatExports.KernelClose(_ctx));
            FreeTracked(payloadAddress);
        }

        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(_root, "metadata.bin")));
    }

    [Fact]
    public void KernelGetdents_EmptyDirectoryReturnsDotEntriesBeforeEof()
    {
        const ulong bufferAddress = Base + 0x1000;
        var fd = OpenRootDirectory();

        try
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = bufferAddress;
            _ctx[CpuRegister.Rdx] = 0x10000;
            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdents(_ctx));

            Assert.Equal(32UL, _ctx[CpuRegister.Rax]);
            Assert.Equal((ushort)16, ReadUInt16(bufferAddress + 4));
            Assert.Equal((byte)4, ReadByte(bufferAddress + 6));
            Assert.Equal(".", ReadCString(bufferAddress + 8));
            Assert.Equal((ushort)16, ReadUInt16(bufferAddress + 16 + 4));
            Assert.Equal((byte)4, ReadByte(bufferAddress + 16 + 6));
            Assert.Equal("..", ReadCString(bufferAddress + 16 + 8));

            var sentinel = Enumerable.Repeat((byte)0xA5, 32).ToArray();
            Assert.True(_memory.TryWrite(bufferAddress, sentinel));
            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdents(_ctx));
            Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
            var afterEof = new byte[sentinel.Length];
            Assert.True(_memory.TryRead(bufferAddress, afterEof));
            Assert.Equal(sentinel, afterEof);
        }
        finally
        {
            Close(fd);
        }
    }

    [Fact]
    public void KernelGetdents_FailedGuestWriteDoesNotAdvanceDirectoryCursor()
    {
        const ulong validBufferAddress = Base + 0x1000;
        const ulong invalidBufferAddress = Base + 0x30000;
        var fd = OpenRootDirectory();

        try
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = invalidBufferAddress;
            _ctx[CpuRegister.Rdx] = 16;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                KernelMemoryCompatExports.KernelGetdents(_ctx));

            _ctx[CpuRegister.Rsi] = validBufferAddress;
            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdents(_ctx));
            Assert.Equal(16UL, _ctx[CpuRegister.Rax]);
            Assert.Equal(".", ReadCString(validBufferAddress + 8));
        }
        finally
        {
            Close(fd);
        }
    }

    [Fact]
    public void KernelGetdirentries_ReportsByteOffsetForEachSplitRead()
    {
        const ulong bufferAddress = Base + 0x1000;
        const ulong basePointerAddress = Base + 0x800;
        var fd = OpenRootDirectory();

        try
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = bufferAddress;
            _ctx[CpuRegister.Rdx] = 16;
            _ctx[CpuRegister.Rcx] = basePointerAddress;

            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdirentries(_ctx));
            Assert.Equal(16UL, _ctx[CpuRegister.Rax]);
            Assert.Equal(0UL, ReadUInt64(basePointerAddress));
            Assert.Equal(".", ReadCString(bufferAddress + 8));

            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdirentries(_ctx));
            Assert.Equal(16UL, _ctx[CpuRegister.Rax]);
            Assert.Equal(16UL, ReadUInt64(basePointerAddress));
            Assert.Equal("..", ReadCString(bufferAddress + 8));

            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdirentries(_ctx));
            Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
            Assert.Equal(32UL, ReadUInt64(basePointerAddress));
        }
        finally
        {
            Close(fd);
        }
    }

    [Fact]
    public void KernelGetdents_PacksHostEntriesUsingRecordLengths()
    {
        const ulong bufferAddress = Base + 0x1000;
        File.WriteAllText(Path.Combine(_root, "asset.bin"), "asset");
        Directory.CreateDirectory(Path.Combine(_root, "folder"));
        var fd = OpenRootDirectory();

        try
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = bufferAddress;
            _ctx[CpuRegister.Rdx] = 0x10000;
            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdents(_ctx));

            var bytesWritten = checked((int)_ctx[CpuRegister.Rax]);
            var names = new List<string>();
            var types = new List<byte>();
            var offset = 0;
            while (offset < bytesWritten)
            {
                var recordLength = ReadUInt16(bufferAddress + unchecked((ulong)offset) + 4);
                Assert.True(recordLength >= 16);
                names.Add(ReadCString(bufferAddress + unchecked((ulong)offset) + 8));
                types.Add(ReadByte(bufferAddress + unchecked((ulong)offset) + 6));
                offset += recordLength;
            }

            Assert.Equal(bytesWritten, offset);
            Assert.Equal([".", "..", "asset.bin", "folder"], names);
            Assert.Equal([(byte)4, (byte)4, (byte)8, (byte)4], types);
        }
        finally
        {
            Close(fd);
        }
    }

    private ulong AllocateTracked(int length)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)length);
        Assert.Equal(0, KernelMemoryCompatExports.Malloc(_ctx));
        Assert.NotEqual(0UL, _ctx[CpuRegister.Rax]);
        return _ctx[CpuRegister.Rax];
    }

    private void FreeTracked(ulong address)
    {
        _ctx[CpuRegister.Rdi] = address;
        Assert.Equal(0, KernelMemoryCompatExports.Free(_ctx));
    }

    private int OpenRootDirectory()
    {
        Assert.True(_memory.TryWrite(PathAddress, "/savedata0\0"u8));
        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = 0x00020000;
        _ctx[CpuRegister.Rdx] = 0x1FF;
        Assert.Equal(0, KernelMemoryCompatExports.KernelOpenUnderscore(_ctx));
        var fd = unchecked((int)_ctx[CpuRegister.Rax]);
        Assert.True(fd > 2);
        return fd;
    }

    private void Close(int fd)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        Assert.Equal(0, KernelMemoryCompatExports.KernelClose(_ctx));
    }

    private byte ReadByte(ulong address)
    {
        Span<byte> value = stackalloc byte[1];
        Assert.True(_memory.TryRead(address, value));
        return value[0];
    }

    private ushort ReadUInt16(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(ushort)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt16LittleEndian(value);
    }

    private ulong ReadUInt64(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }

    private string ReadCString(ulong address)
    {
        Span<byte> value = stackalloc byte[256];
        Assert.True(_memory.TryRead(address, value));
        var length = value.IndexOf((byte)0);
        Assert.True(length >= 0);
        return Encoding.UTF8.GetString(value[..length]);
    }
}
