// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class DirectExecutionBackendLlePreferenceTests
{
    [Theory]
    [InlineData("_Getptolower")]
    [InlineData("_ZNSt6locale5_InitEv")]
    [InlineData("_ZNSt6locale16_GetgloballocaleEv")]
    [InlineData("sceLibcMspaceCreate")]
    [InlineData("sceLibcMspaceDestroy")]
    [InlineData("sceLibcMspaceMalloc")]
    [InlineData("sceLibcMspaceMallocStatsFast")]
    [InlineData("qsort")]
    public void FirmwareOwnedLibcStateAndCallbacks_PreferMappedLle(string exportName)
    {
        Assert.True(DirectExecutionBackend.IsSafeLleLibcExport(exportName));
    }

    [Theory]
    [InlineData("_ZNSt6locale5_InitEb")]
    [InlineData("fputs")]
    [InlineData("malloc")]
    [InlineData("free")]
    [InlineData("calloc")]
    [InlineData("realloc")]
    [InlineData("memalign")]
    [InlineData("aligned_alloc")]
    [InlineData("posix_memalign")]
    [InlineData("malloc_usable_size")]
    [InlineData("setlocale")]
    public void StatefulOrUnavailableLibcExports_DoNotEnterSafeLleSet(string exportName)
    {
        Assert.False(DirectExecutionBackend.IsSafeLleLibcExport(exportName));
    }
}
