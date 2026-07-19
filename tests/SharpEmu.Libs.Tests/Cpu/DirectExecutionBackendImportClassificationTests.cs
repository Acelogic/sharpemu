// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class DirectExecutionBackendImportClassificationTests
{
    [Fact]
    public void ScePthreadCondTimedwaitTimeout_IsExpectedPollingResult()
    {
        Assert.True(DirectExecutionBackend.IsExpectedImportResult(
            "BmMjYxmew1w",
            OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT));
        Assert.False(DirectExecutionBackend.IsExpectedImportResult(
            "BmMjYxmew1w",
            OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED));
    }

    [Theory]
    [InlineData("WKAXJ4XBPQ4")]
    [InlineData("BmMjYxmew1w")]
    [InlineData("Op8TBGY5KHg")]
    [InlineData("27bAgiJmOh0")]
    [InlineData("fzyMKs9kim0")]
    public void BlockingWaitImports_AreExcludedFromStallTermination(string nid)
    {
        Assert.True(DirectExecutionBackend.IsExpectedBlockingImportNid(nid));
    }

    [Fact]
    public void UnrelatedImport_IsNotClassifiedAsExpectedBlockingWait()
    {
        Assert.False(DirectExecutionBackend.IsExpectedBlockingImportNid("Zxa0VhQVTsk"));
    }
}
