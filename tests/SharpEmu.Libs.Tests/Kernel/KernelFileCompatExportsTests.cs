// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
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

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
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
}
