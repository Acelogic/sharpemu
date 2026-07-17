// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Tls;

public sealed class GuestTlsTemplateTests
{
    [Fact]
    public void StartupReservationAcceptsTlsSpansLargerThanOneHostPage()
    {
        try
        {
            GuestTlsTemplate.Reset();

            var staticOffset = GuestTlsTemplate.RegisterModule(
                moduleId: 1,
                initImage: new byte[0x20],
                memorySize: 0x1870,
                alignment: 0x10);

            Assert.Equal(0x1870UL, staticOffset);
            Assert.True(staticOffset <= GuestTlsTemplate.StartupStaticTlsReservation);
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }

    [Fact]
    public void UpdatedInitImageSeedsOnlyFutureThreadBlocks()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong firstThreadPointer = memoryBase + 0x800;
        const ulong secondThreadPointer = memoryBase + 0x1800;
        var memory = new FakeCpuMemory(memoryBase, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);

        try
        {
            GuestTlsTemplate.Reset();
            var staticOffset = GuestTlsTemplate.RegisterModule(
                moduleId: 1,
                initImage: [0x10, 0x20, 0x30, 0x40],
                memorySize: 8,
                alignment: 8);

            GuestTlsTemplate.SeedThreadBlock(context, firstThreadPointer);
            GuestTlsTemplate.UpdateModuleInitImage(1, [0xA0, 0xB0, 0xC0, 0xD0]);
            GuestTlsTemplate.SeedThreadBlock(context, secondThreadPointer);

            Span<byte> firstImage = stackalloc byte[4];
            Span<byte> secondImage = stackalloc byte[4];
            Assert.True(memory.TryRead(firstThreadPointer - staticOffset, firstImage));
            Assert.True(memory.TryRead(secondThreadPointer - staticOffset, secondImage));
            Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 }, firstImage.ToArray());
            Assert.Equal(new byte[] { 0xA0, 0xB0, 0xC0, 0xD0 }, secondImage.ToArray());
        }
        finally
        {
            GuestTlsTemplate.Reset();
        }
    }
}
