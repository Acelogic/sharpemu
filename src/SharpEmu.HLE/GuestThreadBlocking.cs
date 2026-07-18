// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Support for HLE synchronization primitives that block the guest thread's
/// host thread in place (inside the HLE call, on a host primitive) instead of
/// capturing a continuation and re-scheduling through the cooperative wake-key
/// machinery. In-place blocking makes block-and-wake atomic — the host
/// primitive owns the race — which removes the lost-wakeup window the
/// continuation path had between block registration and wake delivery.
/// </summary>
public static class GuestThreadBlocking
{
    /// <summary>
    /// Upper bound on a single host wait while a guest thread is parked. Waits
    /// are sliced so parked threads observe <see cref="ShutdownRequested"/>
    /// promptly at teardown; a wake via Monitor.Pulse still lands immediately.
    /// </summary>
    public const int WaitSliceMilliseconds = 50;

    private static volatile bool _shutdownRequested;

    /// <summary>True once emulator teardown has begun; parked guest threads unwind.</summary>
    public static bool ShutdownRequested => _shutdownRequested;

    /// <summary>Called by the execution backend when guest execution is being torn down.</summary>
    public static void RequestShutdown() => _shutdownRequested = true;
}
