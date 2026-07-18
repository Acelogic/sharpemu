// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

internal static class KernelPthreadState
{
    private const int ThreadObjectSize = 0x1000;

    private static readonly ConcurrentDictionary<ulong, ThreadIdentity> Threads = new();
    private static readonly byte[] ZeroThreadObject = new byte[ThreadObjectSize];
    // Thread identities are append-only, so a host thread can safely retain the
    // guest identity it used on its preceding HLE call. This avoids a concurrent
    // dictionary lookup in hot mutex/TLS exports while still revalidating after
    // every guest-thread switch. Set to 0 for regression isolation.
    private static readonly bool GuestIdentityCacheEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_PTHREAD_IDENTITY_CACHE"),
        "0",
        StringComparison.OrdinalIgnoreCase);
    private static long _nextUniqueThreadId = 1;

    [ThreadStatic]
    private static ulong _currentThreadHandle;

    [ThreadStatic]
    private static ulong _currentThreadUniqueId;

    [ThreadStatic]
    private static ulong _cachedGuestThreadHandle;

    [ThreadStatic]
    private static ulong _cachedGuestThreadUniqueId;

    internal readonly record struct ThreadIdentity(ulong UniqueId, string Name);

    internal static ulong GetCurrentThreadHandle()
    {
        if (TryGetCurrentGuestIdentity(out var guestThreadHandle, out _))
        {
            return guestThreadHandle;
        }

        EnsureCurrentThreadRegistered();
        return _currentThreadHandle;
    }

    internal static ulong GetCurrentThreadUniqueId()
    {
        if (TryGetCurrentGuestIdentity(out _, out var guestThreadUniqueId))
        {
            return guestThreadUniqueId;
        }

        EnsureCurrentThreadRegistered();
        return _currentThreadUniqueId;
    }

    internal static ulong CreateThreadHandle(string name)
    {
        var uniqueId = unchecked((ulong)Interlocked.Increment(ref _nextUniqueThreadId));
        return AllocateThreadHandle(uniqueId, name);
    }

    internal static bool TryGetThreadIdentity(ulong threadHandle, out ThreadIdentity identity)
    {
        return Threads.TryGetValue(threadHandle, out identity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetCurrentGuestIdentity(
        out ulong guestThreadHandle,
        out ulong guestThreadUniqueId)
    {
        guestThreadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
        if (guestThreadHandle == 0)
        {
            guestThreadUniqueId = 0;
            return false;
        }

        if (GuestIdentityCacheEnabled && guestThreadHandle == _cachedGuestThreadHandle)
        {
            guestThreadUniqueId = _cachedGuestThreadUniqueId;
            return true;
        }

        if (!Threads.TryGetValue(guestThreadHandle, out var identity))
        {
            guestThreadUniqueId = 0;
            return false;
        }

        if (GuestIdentityCacheEnabled)
        {
            _cachedGuestThreadHandle = guestThreadHandle;
            _cachedGuestThreadUniqueId = identity.UniqueId;
        }
        guestThreadUniqueId = identity.UniqueId;
        return true;
    }

    private static void EnsureCurrentThreadRegistered()
    {
        if (_currentThreadHandle != 0)
        {
            return;
        }

        var uniqueId = unchecked((ulong)Interlocked.Increment(ref _nextUniqueThreadId));
        var name = $"Thread-{uniqueId:X}";
        _currentThreadHandle = AllocateThreadHandle(uniqueId, name);
        _currentThreadUniqueId = uniqueId;
    }

    private static ulong AllocateThreadHandle(ulong uniqueId, string name)
    {
        var pointer = Marshal.AllocHGlobal(ThreadObjectSize);
        Marshal.Copy(ZeroThreadObject, 0, pointer, ThreadObjectSize);

        var handle = unchecked((ulong)pointer.ToInt64());
        Threads[handle] = new ThreadIdentity(uniqueId, string.IsNullOrWhiteSpace(name) ? $"Thread-{uniqueId:X}" : name);

        return handle;
    }
}
