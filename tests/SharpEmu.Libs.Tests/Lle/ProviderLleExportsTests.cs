// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Lle;
using Xunit;

namespace SharpEmu.Libs.Tests.Lle;

public sealed class ProviderLleExportsTests
{
    private const string SemanticHsOffchipNid = "MM4IZSEYytQ";

    private static readonly IReadOnlyDictionary<string, int> ExpectedCounts =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["libSceAgc"] = 119,
            ["libSceAgcDriver"] = 25,
            ["libSceAjm"] = 1,
            ["libSceAmpr"] = 46,
            ["libSceContentDelete"] = 3,
            ["libSceContentExport"] = 5,
            ["libSceContentSearch"] = 7,
            ["libSceGameLiveStreaming"] = 2,
            ["libSceImeDialog"] = 6,
            ["libSceJson2"] = 17,
            ["libSceNet"] = 5,
            ["libSceNetCtl"] = 3,
            ["libSceNpAuth"] = 5,
            ["libSceNpCommerce"] = 7,
            ["libSceNpEntitlementAccess"] = 5,
            ["libSceNpGameIntent"] = 3,
            ["libSceNpManager"] = 5,
            ["libSceNpUniversalDataSystem"] = 5,
            ["libSceNpUtility"] = 5,
            ["libSceNpWebApi2"] = 17,
            ["libScePlayerInvitationDialog"] = 2,
            ["libSceRemoteplay"] = 3,
            ["libSceRtc"] = 2,
            ["libSceSaveData_native"] = 3,
            ["libSceShare"] = 2,
            ["libSceSharePlay"] = 2,
            ["libSceSigninDialog"] = 4,
            ["libSceSystemService"] = 5,
            ["libSceVideoRecordingP"] = 9,
            ["libSceVoice"] = 15,
            ["libSceWebBrowserDialog"] = 6,
        };

    [Fact]
    public void GtaProviderCatalogs_RegisterAll344ExactGen5NidsAsLlePreferred()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5)
            .Where(IsGeneratedProviderExport)
            .ToArray();

        Assert.Equal(344, exports.Length);
        Assert.Equal(344, exports.Select(export => export.Nid).Distinct(StringComparer.Ordinal).Count());
        Assert.All(exports, export =>
        {
            Assert.Equal(Generation.Gen5, export.Target);
            Assert.True(export.PreferLle);
        });

        foreach (var expected in ExpectedCounts)
        {
            Assert.Equal(expected.Value, exports.Count(export => export.LibraryName == expected.Key));
        }

        var privateVideoExport = Assert.Single(exports, export => export.Nid == "iQS6DUtLybE");
        Assert.Equal("iQS6DUtLybE#L#A", privateVideoExport.Name);
        Assert.Equal("libSceVideoRecordingP", privateVideoExport.LibraryName);

        var semanticHsOffchip = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            export => export.Nid == SemanticHsOffchipNid);
        Assert.True(semanticHsOffchip.PreferLle);
        Assert.Equal("libSceAgcDriver", semanticHsOffchip.LibraryName);
    }

    [Fact]
    public void GtaProviderCatalogs_DoNotProjectRegistrationsToGen4()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4)
            .Where(IsGeneratedProviderExport)
            .ToArray();

        Assert.Empty(exports);
    }

    [Fact]
    public void MissingGuestProviderFallbacks_AreExplicitlyFailClosed()
    {
        var fallbacks = new Func<CpuContext, int>[]
        {
            AgcLleExports.MissingGuestProvider,
            AgcDriverLleExports.MissingGuestProvider,
            AjmLleExports.MissingGuestProvider,
            AmprLleExports.MissingGuestProvider,
            ContentDeleteLleExports.MissingGuestProvider,
            ContentExportLleExports.MissingGuestProvider,
            ContentSearchLleExports.MissingGuestProvider,
            GameLiveStreamingLleExports.MissingGuestProvider,
            ImeDialogLleExports.MissingGuestProvider,
            Json2LleExports.MissingGuestProvider,
            NetLleExports.MissingGuestProvider,
            NetCtlLleExports.MissingGuestProvider,
            NpAuthLleExports.MissingGuestProvider,
            NpCommerceLleExports.MissingGuestProvider,
            NpEntitlementAccessLleExports.MissingGuestProvider,
            NpGameIntentLleExports.MissingGuestProvider,
            NpManagerLleExports.MissingGuestProvider,
            NpUniversalDataSystemLleExports.MissingGuestProvider,
            NpUtilityLleExports.MissingGuestProvider,
            NpWebApi2LleExports.MissingGuestProvider,
            PlayerInvitationDialogLleExports.MissingGuestProvider,
            RemoteplayLleExports.MissingGuestProvider,
            RtcLleExports.MissingGuestProvider,
            SaveDataNativeLleExports.MissingGuestProvider,
            ShareLleExports.MissingGuestProvider,
            SharePlayLleExports.MissingGuestProvider,
            SigninDialogLleExports.MissingGuestProvider,
            SystemServiceLleExports.MissingGuestProvider,
            VideoRecordingPrivateLleExports.MissingGuestProvider,
            VoiceLleExports.MissingGuestProvider,
            WebBrowserDialogLleExports.MissingGuestProvider,
        };

        foreach (var fallback in fallbacks)
        {
            var context = new CpuContext(new NullMemory(), Generation.Gen5);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED,
                fallback(context));
            Assert.Equal(
                unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED),
                context[CpuRegister.Rax]);
        }
    }

    private static bool IsGeneratedProviderExport(ExportedFunction export) =>
        ExpectedCounts.ContainsKey(export.LibraryName) &&
        export.PreferLle &&
        export.Nid != SemanticHsOffchipNid;

    private sealed class NullMemory : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }
}
