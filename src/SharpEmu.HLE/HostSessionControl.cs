// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Lets host-facing libraries (VideoOut, AudioOut) request cooperative guest
/// shutdown without taking a dependency on SharpEmu.Core.
/// </summary>
public static class HostSessionControl
{
    private static Action<string>? _shutdownHandler;

    public static void SetShutdownHandler(Action<string>? handler)
    {
        Volatile.Write(ref _shutdownHandler, handler);
    }

    public static void RequestShutdown(string reason)
    {
        try
        {
            Volatile.Read(ref _shutdownHandler)?.Invoke(reason);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] Host shutdown handler failed: {exception.Message}");
        }
    }
}
