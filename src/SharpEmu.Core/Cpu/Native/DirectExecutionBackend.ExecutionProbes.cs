// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Threading;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
	private const string GuestExecutionProbeVariable = "SHARPEMU_TRACE_GUEST_EXEC_ADDRS";
	private ulong[] _guestExecutionProbeAddresses = Array.Empty<ulong>();
	private byte[] _guestExecutionProbeOriginalBytes = Array.Empty<byte>();
	private int[] _guestExecutionProbeHits = Array.Empty<int>();

	private unsafe void InstallGuestExecutionProbes()
	{
		RestoreGuestExecutionProbes();
		List<ulong> requestedAddresses = ParseDiagnosticAddresses(
			Environment.GetEnvironmentVariable(GuestExecutionProbeVariable));
		if (requestedAddresses.Count == 0)
		{
			return;
		}

		var installedAddresses = new List<ulong>(requestedAddresses.Count);
		var originalBytes = new List<byte>(requestedAddresses.Count);
		foreach (ulong address in requestedAddresses)
		{
			byte[] original = new byte[1];
			if (!TryReadHostBytes(address, original) || original[0] == 0xCC)
			{
				Console.Error.WriteLine(
					$"[LOADER][WARNING] guest-exec-probe install skipped address=0x{address:X16} readable={original[0] != 0} opcode=0x{original[0]:X2}");
				continue;
			}

			if (!TryWriteGuestExecutionProbeByte(address, 0xCC))
			{
				Console.Error.WriteLine(
					$"[LOADER][WARNING] guest-exec-probe install failed address=0x{address:X16}");
				continue;
			}

			installedAddresses.Add(address);
			originalBytes.Add(original[0]);
			Console.Error.WriteLine(
				$"[LOADER][TRACE] guest-exec-probe armed address=0x{address:X16} opcode=0x{original[0]:X2}");
		}

		_guestExecutionProbeAddresses = installedAddresses.ToArray();
		_guestExecutionProbeOriginalBytes = originalBytes.ToArray();
		_guestExecutionProbeHits = new int[installedAddresses.Count];
	}

	private unsafe bool TryRecoverGuestExecutionProbe(
		uint exceptionCode,
		ulong exceptionAddress,
		void* contextRecord,
		ulong rip)
	{
		if (exceptionCode != 0x80000003u || _guestExecutionProbeAddresses.Length == 0)
		{
			return false;
		}

		ulong candidateAddress = exceptionAddress;
		for (var i = 0; i < _guestExecutionProbeAddresses.Length; i++)
		{
			ulong probeAddress = _guestExecutionProbeAddresses[i];
			if (probeAddress != candidateAddress && probeAddress + 1 != rip)
			{
				continue;
			}

			if (Interlocked.Exchange(ref _guestExecutionProbeHits[i], 1) == 0)
			{
				TryWriteGuestExecutionProbeByte(probeAddress, _guestExecutionProbeOriginalBytes[i]);
			}
			WriteCtxU64(contextRecord, CTX_RIP, probeAddress);
			ulong stackPointer = ReadCtxU64(contextRecord, CTX_RSP);
			_ = TryReadHostQword(stackPointer, out ulong returnAddress);
			Console.Error.WriteLine(
				$"[LOADER][TRACE] guest-exec-probe hit address=0x{probeAddress:X16} " +
				$"rax=0x{ReadCtxU64(contextRecord, CTX_RAX):X16} rcx=0x{ReadCtxU64(contextRecord, CTX_RCX):X16} " +
				$"rdx=0x{ReadCtxU64(contextRecord, CTX_RDX):X16} rbx=0x{ReadCtxU64(contextRecord, CTX_RBX):X16} " +
				$"rsp=0x{stackPointer:X16} ret=0x{returnAddress:X16} rbp=0x{ReadCtxU64(contextRecord, CTX_RBP):X16} " +
				$"rsi=0x{ReadCtxU64(contextRecord, CTX_RSI):X16} rdi=0x{ReadCtxU64(contextRecord, CTX_RDI):X16} " +
				$"r8=0x{ReadCtxU64(contextRecord, CTX_R8):X16} r9=0x{ReadCtxU64(contextRecord, CTX_R9):X16} " +
				$"r12=0x{ReadCtxU64(contextRecord, CTX_R12):X16} r13=0x{ReadCtxU64(contextRecord, CTX_R13):X16} " +
				$"r14=0x{ReadCtxU64(contextRecord, CTX_R14):X16} r15=0x{ReadCtxU64(contextRecord, CTX_R15):X16}");
			Console.Error.Flush();
			return true;
		}

		return false;
	}

	private unsafe void RestoreGuestExecutionProbes()
	{
		for (var i = 0; i < _guestExecutionProbeAddresses.Length; i++)
		{
			if (Volatile.Read(ref _guestExecutionProbeHits[i]) == 0)
			{
				TryWriteGuestExecutionProbeByte(
					_guestExecutionProbeAddresses[i],
					_guestExecutionProbeOriginalBytes[i]);
			}
		}

		_guestExecutionProbeAddresses = Array.Empty<ulong>();
		_guestExecutionProbeOriginalBytes = Array.Empty<byte>();
		_guestExecutionProbeHits = Array.Empty<int>();
	}

	private unsafe bool TryWriteGuestExecutionProbeByte(ulong address, byte value)
	{
		uint oldProtect = 0;
		if (!VirtualProtect((void*)address, 1u, 64u, &oldProtect))
		{
			return false;
		}

		try
		{
			*(byte*)address = value;
		}
		finally
		{
			VirtualProtect((void*)address, 1u, oldProtect, &oldProtect);
			FlushInstructionCache(GetCurrentProcess(), (void*)address, 1u);
		}

		return true;
	}
}
