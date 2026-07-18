// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Lle;
using Xunit;

namespace SharpEmu.Libs.Tests.Lle;

public sealed class ProviderLleExportsTests
{
    private const string SemanticHsOffchipNid = "MM4IZSEYytQ";

    private static readonly IReadOnlyDictionary<string, (string ExportName, string LibraryName)> Provider23Expected =
        new Dictionary<string, (string ExportName, string LibraryName)>(StringComparer.Ordinal)
        {
            ["+k91hoTuoA8"] = ("sceAudioOut2SpeakerArrayCreate", "libSceAudioOut2"),
            ["28QqMnuuJ9Y"] = ("sceAudioOut2GetSpeakerArrayAmbisonicsCoefficients", "libSceAudioOut2"),
            ["39WxhR-ePew"] = ("sceAjmBatchJobDecode", "libSceAjm"),
            ["4fU5yvOkVG4"] = ("sceSysmoduleGetModuleInfoForUnwind", "libSceSysmodule"),
            ["5tOfnaClcqM"] = ("sceAjmBatchStart", "libSceAjm"),
            ["9FowWFMEIM8"] = ("sceRazorCpuJobManagerSequence", "libSceRazorCpu"),
            ["BG26hBGiNlw"] = ("_sceUlobjmgrRegisterObject", "ulobjmgr"),
            ["CgdJ1PkIsE4"] = ("scePlayerSelectionDialogTerminate", "libScePlayerSelectionDialog"),
            ["Dbbkj6YHWdo"] = ("sceCoredumpWriteUserData", "libSceCoredump"),
            ["EwNylPdWUTM"] = ("sceNpTrophy2GetTrophyInfo", "libSceNpTrophy2"),
            ["G1YOKDJYX2Y"] = ("sceAudioOut2GetSpeakerArrayMemorySize", "libSceAudioOut2"),
            ["Gl6w5i0JokY"] = ("sceAppContentDownloadDataGetAvailableSpaceKb", "libSceAppContent"),
            ["HuViW4HnrOw"] = ("sceVideoOutSubmitChangeBufferAttribute2", "libSceVideoOut"),
            ["KP+TBWGHlgs"] = ("sceRazorCpuJobManagerJob", "libSceRazorCpu"),
            ["PI7jIZj4pcE"] = ("sceRandomGetRandomNumber", "libSceRandom"),
            ["PMVehSlfZ94"] = ("sceImeKeyboardClose", "libSceIme"),
            ["Smf+fUNblPc"] = ("_sceUlobjmgrUnregisterObject", "ulobjmgr"),
            ["YuOW3dDAKYc"] = ("sceHttpUriEscape", "libSceHttp"),
            ["dnEdyY4+klQ"] = ("sceRazorCpuJobManagerDispatch", "libSceRazorCpu"),
            ["erCWQR5eKiQ"] = ("sceAudioOut2SpeakerArrayDestroy", "libSceAudioOut2"),
            ["fFkhOgztiCA"] = ("sceCoredumpUnregisterCoredumpHandler", "libSceCoredump"),
            ["rIZnR6eSpvk"] = ("scePadResetOrientation", "libScePad"),
            ["wVwPU50pS1c"] = ("sceAudioOutSetMixLevelPadSpk", "libSceAudioOut"),
        };

    private static readonly IReadOnlyDictionary<string, int> ExpectedCounts =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["libSceAgc"] = 119,
            ["libSceAgcDriver"] = 25,
            ["libSceAjm"] = 3,
            ["libSceAmpr"] = 46,
            ["libSceAppContent"] = 1,
            ["libSceAudioOut"] = 1,
            ["libSceAudioOut2"] = 4,
            ["libSceContentDelete"] = 3,
            ["libSceContentExport"] = 5,
            ["libSceContentSearch"] = 7,
            ["libSceCoredump"] = 2,
            ["libSceGameLiveStreaming"] = 2,
            ["libSceHttp"] = 1,
            ["libSceIme"] = 1,
            ["libSceImeDialog"] = 6,
            ["libSceJson2"] = 17,
            ["libSceNet"] = 5,
            ["libSceNetCtl"] = 3,
            ["libSceNpAuth"] = 5,
            ["libSceNpCommerce"] = 7,
            ["libSceNpEntitlementAccess"] = 5,
            ["libSceNpGameIntent"] = 3,
            ["libSceNpManager"] = 5,
            ["libSceNpTrophy2"] = 1,
            ["libSceNpUniversalDataSystem"] = 5,
            ["libSceNpUtility"] = 5,
            ["libSceNpWebApi2"] = 17,
            ["libScePad"] = 1,
            ["libScePlayerInvitationDialog"] = 2,
            ["libScePlayerSelectionDialog"] = 1,
            ["libSceRandom"] = 1,
            ["libSceRazorCpu"] = 3,
            ["libSceRemoteplay"] = 3,
            ["libSceRtc"] = 2,
            ["libSceSaveData_native"] = 3,
            ["libSceShare"] = 2,
            ["libSceSharePlay"] = 2,
            ["libSceSigninDialog"] = 4,
            ["libSceSystemService"] = 5,
            ["libSceSysmodule"] = 1,
            ["libSceVideoOut"] = 1,
            ["libSceVideoRecordingP"] = 9,
            ["libSceVoice"] = 15,
            ["libSceWebBrowserDialog"] = 6,
            ["ulobjmgr"] = 2,
        };

    [Fact]
    public void GtaProviderCatalogs_RegisterAll367ExactGen5NidsAsLlePreferred()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5)
            .Where(IsGeneratedProviderExport)
            .ToArray();

        Assert.Equal(367, exports.Length);
        Assert.Equal(367, exports.Select(export => export.Nid).Distinct(StringComparer.Ordinal).Count());
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
    public void GtaProvider23Catalogs_RegisterExactGen5NamesAndLibrariesAsLlePreferred()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5)
            .Where(export => Provider23Expected.ContainsKey(export.Nid))
            .ToArray();

        Assert.Equal(23, exports.Length);
        Assert.Equal(23, exports.Select(export => export.Nid).Distinct(StringComparer.Ordinal).Count());
        foreach (var export in exports)
        {
            var expected = Provider23Expected[export.Nid];
            Assert.Equal(expected.ExportName, export.Name);
            Assert.Equal(expected.LibraryName, export.LibraryName);
            Assert.Equal(Generation.Gen5, export.Target);
            Assert.True(export.PreferLle);
        }
    }

    [Fact]
    public void GtaProviderCatalogs_DoNotProjectRegistrationsToGen4()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4)
            .Where(IsGeneratedProviderExport)
            .ToArray();

        Assert.Empty(exports);

        Assert.DoesNotContain(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4),
            export => Provider23Expected.ContainsKey(export.Nid));
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

    [Fact]
    public void Provider23MissingGuestProviderFallbacks_AreExplicitlyFailClosed()
    {
        var fallbacks = new Func<CpuContext, int>[]
        {
            AjmNativeLleExports.MissingGuestProvider,
            AppContentLleExports.MissingGuestProvider,
            AudioOutLleExports.MissingGuestProvider,
            AudioOut2LleExports.MissingGuestProvider,
            CoredumpLleExports.MissingGuestProvider,
            HttpLleExports.MissingGuestProvider,
            ImeLleExports.MissingGuestProvider,
            NpTrophy2LleExports.MissingGuestProvider,
            PadLleExports.MissingGuestProvider,
            PlayerSelectionDialogLleExports.MissingGuestProvider,
            RandomLleExports.MissingGuestProvider,
            RazorCpuLleExports.MissingGuestProvider,
            SysmoduleLleExports.MissingGuestProvider,
            UlObjMgrLleExports.MissingGuestProvider,
            VideoOutLleExports.MissingGuestProvider,
        };

        Assert.Equal(15, fallbacks.Length);
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
