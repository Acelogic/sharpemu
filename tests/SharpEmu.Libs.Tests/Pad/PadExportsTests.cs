// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const int InvalidArgument = unchecked((int)0x80920001);
    private const int InvalidHandle = unchecked((int)0x80920003);
    private const int NotInitialized = unchecked((int)0x80920005);

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public PadExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        PadExports.ResetTriggerEffectStateForTests();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void SetTiltCorrectionState_ValidatesHandle(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadSetTiltCorrectionState(_ctx));
    }

    [Fact]
    public void GetTriggerEffectState_UsesInitNullHandleDeviceValidationOrder()
    {
        var stateAddress = Base + 0x100;
        Span<byte> sentinel = stackalloc byte[8];
        sentinel.Fill(0xA5);
        Assert.True(_memory.TryWrite(stateAddress, sentinel));
        _ctx[CpuRegister.Rdi] = 2;
        _ctx[CpuRegister.Rsi] = stateAddress;

        Assert.Equal(NotInitialized, PadExports.PadGetTriggerEffectState(_ctx));
        AssertBytes(stateAddress, sentinel);

        PadExports.ResetTriggerEffectStateForTests(initialized: true);
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(InvalidArgument, PadExports.PadGetTriggerEffectState(_ctx));

        _ctx[CpuRegister.Rsi] = stateAddress;
        Assert.Equal(InvalidHandle, PadExports.PadGetTriggerEffectState(_ctx));
        AssertBytes(stateAddress, sentinel);

        PadExports.ResetTriggerEffectStateForTests(
            initialized: true,
            deviceState: 3);
        PadExports.SetPrimaryPadOpenForTests(true);
        _ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(InvalidArgument, PadExports.PadGetTriggerEffectState(_ctx));
        AssertBytes(stateAddress, sentinel);
    }

    [Fact]
    public void GetTriggerEffectState_NormalizesUnsupportedBackendToEightZeroBytes()
    {
        var stateAddress = Base + 0x200;
        Span<byte> sentinel = stackalloc byte[12];
        sentinel.Fill(0xCC);
        Assert.True(_memory.TryWrite(stateAddress, sentinel));
        PadExports.ResetTriggerEffectStateForTests(initialized: true);
        PadExports.SetPrimaryPadOpenForTests(true);
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = stateAddress;

        Assert.Equal(0, PadExports.PadGetTriggerEffectState(_ctx));

        Span<byte> actual = stackalloc byte[12];
        Assert.True(_memory.TryRead(stateAddress, actual));
        Assert.Equal(new byte[8], actual[..8].ToArray());
        Assert.Equal(new byte[] { 0xCC, 0xCC, 0xCC, 0xCC }, actual[8..].ToArray());
    }

    [Fact]
    public void PadOpenAndClose_ControlTriggerEffectHandleLifetime()
    {
        var stateAddress = Base + 0x280;
        PadExports.ResetTriggerEffectStateForTests(initialized: true);
        PadExports.SetHostInputForTests(new TestHostInput());
        _ctx[CpuRegister.Rdi] = 0x1000_0000;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = 0;

        Assert.Equal(1, PadExports.PadOpen(_ctx));

        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = stateAddress;
        Assert.Equal(0, PadExports.PadGetTriggerEffectState(_ctx));

        _ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(0, PadExports.PadClose(_ctx));

        Span<byte> sentinel = stackalloc byte[8];
        sentinel.Fill(0xA7);
        Assert.True(_memory.TryWrite(stateAddress, sentinel));
        _ctx[CpuRegister.Rsi] = stateAddress;
        Assert.Equal(InvalidHandle, PadExports.PadGetTriggerEffectState(_ctx));
        AssertBytes(stateAddress, sentinel);
    }

    [Fact]
    public void GetTriggerEffectState_MapsFfAndCopiesSupportedState()
    {
        var stateAddress = Base + 0x300;
        PadExports.ResetTriggerEffectStateForTests(initialized: true);
        PadExports.SetPrimaryPadOpenForTests(true);
        PadExports.SetTriggerEffectStateBackendForTests(
            _ => (0, byte.MaxValue, 7));
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = stateAddress;

        Assert.Equal(0, PadExports.PadGetTriggerEffectState(_ctx));

        Span<byte> actual = stackalloc byte[8];
        Assert.True(_memory.TryRead(stateAddress, actual));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(actual));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(actual[4..]));
    }

    [Fact]
    public void GetTriggerEffectState_PropagatesBackendErrorAfterZeroingOutput()
    {
        const int BackendError = unchecked((int)0x8123_4567);
        var stateAddress = Base + 0x380;
        Span<byte> sentinel = stackalloc byte[8];
        sentinel.Fill(0x5C);
        Assert.True(_memory.TryWrite(stateAddress, sentinel));
        PadExports.ResetTriggerEffectStateForTests(initialized: true);
        PadExports.SetPrimaryPadOpenForTests(true);
        PadExports.SetTriggerEffectStateBackendForTests(
            _ => (BackendError, 4, 5));
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = stateAddress;

        Assert.Equal(BackendError, PadExports.PadGetTriggerEffectState(_ctx));
        AssertBytes(stateAddress, new byte[8]);
    }

    [Fact]
    public void TriggerEffectStateNid_RegistersWithPadIdentity()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("znaWI0gpuo8", out var export));
        Assert.Equal("scePadGetTriggerEffectState", export.Name);
        Assert.Equal("libScePad", export.LibraryName);
    }

    private void AssertBytes(ulong address, ReadOnlySpan<byte> expected)
    {
        Span<byte> actual = stackalloc byte[expected.Length];
        Assert.True(_memory.TryRead(address, actual));
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }

    private sealed class TestHostInput : IHostInput
    {
        public void EnsureStarted()
        {
        }

        public int GetGamepadStates(Span<HostGamepadState> destination) => 0;

        public string? DescribeConnectedGamepad() => null;

        public void SetRumble(byte largeMotor, byte smallMotor)
        {
        }

        public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger)
        {
        }

        public void SetLightbar(byte red, byte green, byte blue)
        {
        }

        public void ResetLightbar()
        {
        }

        public bool IsHostWindowFocused() => false;

        public bool IsKeyDown(int virtualKey) => false;
    }
}
