// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Json;
using Xunit;

namespace SharpEmu.Libs.Tests.Json;

// These NIDs came back "unresolved" in the Quake (PPSA01880) import log right before its
// access violation. This asserts they now resolve to the Json handlers and dispatch cleanly,
// which is the plumbing the direct-call tests cannot cover.
[Collection("JsonObjectHeap")]
public sealed class JsonExportRegistrationTests
{
    private static readonly (string Nid, string Name)[] ExpectedExports =
    {
        ("qBMjqyBn3OM", "_ZN3sce4Json5ValueC1Ev"),
        ("5yHuiWXo2gg", "_ZN3sce4Json5Value3setEb"),
        ("QxVVYhP-mvg", "_ZN3sce4Json5Value3setEl"),
        ("SIe1ZmW7e7s", "_ZN3sce4Json5Value3setEm"),
        ("BSmWDIkV4w4", "_ZN3sce4Json5Value3setEd"),
        ("IKQimvG9Wqs", "_ZN3sce4Json5Value3setENS0_9ValueTypeE"),
        ("6l3Bv2gysNc", "_ZN3sce4Json5Value3setERKNS0_6StringE"),
        ("wLsJlmgEIaI", "_ZN3sce4Json5Value10referValueERKNS0_6StringE"),
        ("9KUZFjI1IxA", "_ZN3sce4Json6StringC1EPKc"),
        ("cG1VE2HMl6c", "_ZN3sce4Json6StringD1Ev"),
        ("+drDFyAS6u4", "_ZN3sce4Json11Initializer27setGlobalNullAccessCallbackEPFRKNS0_5ValueENS0_9ValueTypeEPS3_PvES7_"),
        ("00oCq0RwSAY", "_ZN3sce4Json11Initializer27setGlobalNullAccessCallBackEPFRKNS0_5ValueENS0_9ValueTypeEPS3_PvES7_"),
        ("IXW-z8pggfg", "_ZN3sce4Json11Initializer10initializeEPKNS0_14InitParameter2E"),
    };

    private static ModuleManager CreateRegisteredManager()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        return manager;
    }

    [Fact]
    public void QuakeUnresolvedJsonNids_ResolveToJsonExports()
    {
        var manager = CreateRegisteredManager();

        foreach (var (nid, name) in ExpectedExports)
        {
            Assert.True(manager.TryGetExport(nid, out var export), $"NID {nid} did not register.");
            Assert.Equal(name, export.Name);
            Assert.Equal(
                nid is "00oCq0RwSAY" or "IXW-z8pggfg" ? "libSceJson2" : "libSceJson",
                export.LibraryName);
        }
    }

    [Fact]
    public void SetGlobalNullAccessCallback_StoresHookAndReturnsOk()
    {
        JsonObjectHeap.ResetForTests();
        var manager = CreateRegisteredManager();
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0x1_0000_0000; // Initializer instance
        ctx[CpuRegister.Rsi] = 0x8_0012_3456; // guest callback
        ctx[CpuRegister.Rdx] = 0x1_0000_0800; // user context

        Assert.True(manager.TryDispatch("+drDFyAS6u4", ctx, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(0x8_0012_3456UL, JsonObjectHeap.GlobalNullAccessCallback);
        Assert.Equal(0x1_0000_0800UL, JsonObjectHeap.GlobalNullAccessCallbackContext);
    }

    [Fact]
    public void SetGlobalNullAccessCallBack_StoresOnlyTheFirstValidHook()
    {
        const ulong initializerAddress = 0x1_0000_0000;
        JsonObjectHeap.ResetForTests();
        var memory = new FakeCpuMemory(initializerAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        InitializeJson2(ctx, initializerAddress, initializerAddress + 0x100);
        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = 0x8_0012_3456;
        ctx[CpuRegister.Rdx] = initializerAddress + 0x800;

        Assert.Equal(0, JsonExports.InitializerSetGlobalNullAccessCallBack(ctx));
        Assert.Equal(0x8_0012_3456UL, JsonObjectHeap.GlobalNullAccessCallback);
        Assert.Equal(initializerAddress + 0x800, JsonObjectHeap.GlobalNullAccessCallbackContext);

        ctx[CpuRegister.Rsi] = 0x8_0065_4321;
        ctx[CpuRegister.Rdx] = initializerAddress + 0x900;
        Assert.Equal(unchecked((int)0x80848112), JsonExports.InitializerSetGlobalNullAccessCallBack(ctx));
        Assert.Equal(0x8_0012_3456UL, JsonObjectHeap.GlobalNullAccessCallback);
        Assert.Equal(initializerAddress + 0x800, JsonObjectHeap.GlobalNullAccessCallbackContext);
    }

    [Fact]
    public void SetGlobalNullAccessCallBack_RejectsForgedSelfWhenGlobalStateIsNotReady()
    {
        const ulong initializerAddress = 0x1_0000_0000;
        JsonObjectHeap.ResetForTests();
        var memory = new FakeCpuMemory(initializerAddress, 0x1000);
        Assert.True(memory.TryWrite(initializerAddress, new byte[] { 1 }));
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = 0x8_0012_3456;
        ctx[CpuRegister.Rdx] = initializerAddress + 0x800;

        Assert.Equal(unchecked((int)0x80848110), JsonExports.InitializerSetGlobalNullAccessCallBack(ctx));
        Assert.Equal(0UL, JsonObjectHeap.GlobalNullAccessCallback);
        Assert.Equal(0UL, JsonObjectHeap.GlobalNullAccessCallbackContext);
    }

    [Fact]
    public void SetGlobalNullAccessCallBack_RejectsUninitializedSelfWhenGlobalStateIsReady()
    {
        const ulong initializedAddress = 0x1_0000_0000;
        const ulong uninitializedAddress = initializedAddress + 0x40;
        JsonObjectHeap.ResetForTests();
        var memory = new FakeCpuMemory(initializedAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        InitializeJson2(ctx, initializedAddress, initializedAddress + 0x100);
        ctx[CpuRegister.Rdi] = uninitializedAddress;
        Assert.Equal(0, JsonExports.InitializerConstructor(ctx));
        ctx[CpuRegister.Rsi] = 0x8_0012_3456;
        ctx[CpuRegister.Rdx] = initializedAddress + 0x800;

        Assert.Equal(unchecked((int)0x80848110), JsonExports.InitializerSetGlobalNullAccessCallBack(ctx));
        Assert.Equal(0UL, JsonObjectHeap.GlobalNullAccessCallback);
    }

    [Fact]
    public void SetGlobalNullAccessCallBack_RejectsNullCallbackAfterRealInitialization()
    {
        const ulong initializerAddress = 0x1_0000_0000;
        JsonObjectHeap.ResetForTests();
        var memory = new FakeCpuMemory(initializerAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        InitializeJson2(ctx, initializerAddress, initializerAddress + 0x100);
        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = 0;

        Assert.Equal(unchecked((int)0x80848120), JsonExports.InitializerSetGlobalNullAccessCallBack(ctx));
        Assert.Equal(0UL, JsonObjectHeap.GlobalNullAccessCallback);
    }

    [Fact]
    public void InitializerInitialize2_ConstructorToCallbackFlowSetsSharedLifecycle()
    {
        const ulong initializerAddress = 0x1_0000_0000;
        const ulong parameterAddress = initializerAddress + 0x100;
        const ulong callback = 0x8_0012_3456;
        JsonObjectHeap.ResetForTests();
        var memory = new FakeCpuMemory(initializerAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = initializerAddress;
        Assert.Equal(0, JsonExports.InitializerConstructor(ctx));
        ctx[CpuRegister.Rdi] = parameterAddress;
        Assert.Equal(0, JsonExports.InitParameter2Constructor(ctx));
        ctx[CpuRegister.Rdi] = parameterAddress;
        ctx[CpuRegister.Rsi] = initializerAddress + 0x300;
        ctx[CpuRegister.Rdx] = initializerAddress + 0x400;
        Assert.Equal(0, JsonExports.InitParameter2SetAllocator(ctx));
        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = parameterAddress;
        Assert.Equal(0, JsonExports.InitializerInitialize2(ctx));

        Span<byte> initialized = stackalloc byte[1];
        Assert.True(memory.TryRead(initializerAddress, initialized));
        Assert.Equal(1, initialized[0]);
        Assert.Equal(unchecked((int)0x80848111), JsonExports.InitializerInitialize2(ctx));

        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = callback;
        ctx[CpuRegister.Rdx] = initializerAddress + 0x800;
        Assert.Equal(0, JsonExports.InitializerSetGlobalNullAccessCallBack(ctx));
        Assert.Equal(callback, JsonObjectHeap.GlobalNullAccessCallback);
    }

    [Fact]
    public void InitializerInitialize2_RejectsInvalidModeAndAllocationFailureWithoutInitializing()
    {
        const ulong initializerAddress = 0x1_0000_0000;
        const ulong parameterAddress = initializerAddress + 0x100;
        JsonObjectHeap.ResetForTests();
        var memory = new FakeCpuMemory(initializerAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = initializerAddress;
        Assert.Equal(0, JsonExports.InitializerConstructor(ctx));
        ctx[CpuRegister.Rdi] = parameterAddress;
        Assert.Equal(0, JsonExports.InitParameter2Constructor(ctx));

        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = parameterAddress;
        Assert.Equal(unchecked((int)0x80848120), JsonExports.InitializerInitialize2(ctx));

        ctx[CpuRegister.Rdi] = parameterAddress;
        ctx[CpuRegister.Rsi] = initializerAddress + 0x300;
        ctx[CpuRegister.Rdx] = initializerAddress + 0x400;
        Assert.Equal(0, JsonExports.InitParameter2SetAllocator(ctx));
        Assert.True(ctx.TryWriteUInt32(parameterAddress + 0x18, 3));
        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = parameterAddress;
        Assert.Equal(unchecked((int)0x80848120), JsonExports.InitializerInitialize2(ctx));

        Assert.True(ctx.TryWriteUInt32(parameterAddress + 0x18, 2));
        JsonExports.SetInitializerInitialize2AllocationFailureForTests(true);
        Assert.Equal(unchecked((int)0x80848102), JsonExports.InitializerInitialize2(ctx));

        Span<byte> initialized = stackalloc byte[1];
        Assert.True(memory.TryRead(initializerAddress, initialized));
        Assert.Equal(0, initialized[0]);
    }

    [Fact]
    public void DispatchValueConstructor_RunsHandlerAndReturnsThis()
    {
        JsonObjectHeap.ResetForTests();
        var manager = CreateRegisteredManager();
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0x1_0000_0000;

        Assert.True(manager.TryDispatch("qBMjqyBn3OM", ctx, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0x1_0000_0000UL, ctx[CpuRegister.Rax]);
        Assert.Equal(JsonValueKind.Null, JsonObjectHeap.Values[0x1_0000_0000].Kind);
    }

    private static void InitializeJson2(CpuContext ctx, ulong initializerAddress, ulong parameterAddress)
    {
        ctx[CpuRegister.Rdi] = initializerAddress;
        Assert.Equal(0, JsonExports.InitializerConstructor(ctx));
        Span<byte> state = stackalloc byte[1];
        Assert.True(ctx.Memory.TryRead(initializerAddress, state));
        Assert.Equal(0, state[0]);
        Assert.True(ctx.TryWriteUInt64(parameterAddress, parameterAddress + 0x100));
        Assert.True(ctx.TryWriteUInt64(parameterAddress + 8, parameterAddress + 0x200));
        Assert.True(ctx.TryWriteUInt64(parameterAddress + 16, 0));
        ctx[CpuRegister.Rsi] = parameterAddress;
        Assert.Equal(0, JsonExports.InitializerInitialize(ctx));
        Assert.True(ctx.Memory.TryRead(initializerAddress, state));
        Assert.Equal(1, state[0]);
    }
}
