// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu;

public readonly struct CpuExecutionOptions
{
    public bool EnableDisasmDiagnostics { get; init; }

    public CpuExecutionEngine CpuEngine { get; init; }

    public bool StrictDynlibResolution { get; init; }

    public int ImportTraceLimit { get; init; }

    /// <summary>
    /// Creates guest pthread objects but leaves their entry points dormant for the
    /// duration of this dispatch. This is used by one-shot bootstrap initializers:
    /// persistent service threads cannot outlive an isolated initializer dispatch.
    /// </summary>
    public bool QuiesceNewGuestThreads { get; init; }
}
