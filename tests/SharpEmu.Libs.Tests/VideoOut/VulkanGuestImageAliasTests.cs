// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestImageAliasTests
{
    [Fact]
    public void ExactExtentIsCompatibleForLinearImages()
    {
        Assert.True(VulkanVideoPresenter.IsSampledGuestImageExtentCompatible(
            textureWidth: 1920,
            textureHeight: 1080,
            tileMode: 0,
            imageWidth: 1920,
            imageHeight: 1080));
    }

    [Fact]
    public void LargerTiledDescriptorCanViewSmallerDynamicResolutionImage()
    {
        Assert.True(VulkanVideoPresenter.IsSampledGuestImageExtentCompatible(
            textureWidth: 2432,
            textureHeight: 1368,
            tileMode: 27,
            imageWidth: 1920,
            imageHeight: 1080));
    }

    [Fact]
    public void SmallerTiledDescriptorCanViewLargerDynamicResolutionImage()
    {
        Assert.True(VulkanVideoPresenter.IsSampledGuestImageExtentCompatible(
            textureWidth: 960,
            textureHeight: 540,
            tileMode: 27,
            imageWidth: 1920,
            imageHeight: 1080));
    }

    [Fact]
    public void MismatchedLinearExtentsAreNotAliases()
    {
        Assert.False(VulkanVideoPresenter.IsSampledGuestImageExtentCompatible(
            textureWidth: 960,
            textureHeight: 540,
            tileMode: 0,
            imageWidth: 1920,
            imageHeight: 1080));
    }

    [Fact]
    public void GpuDmaCopyRequiresMatchingHostFormats()
    {
        Assert.False(VulkanVideoPresenter.IsGuestImageCopyCompatible(
            sourceWidth: 960,
            sourceHeight: 540,
            sourceFormat: Format.R16G16B16A16Sfloat,
            destinationWidth: 960,
            destinationHeight: 540,
            destinationFormat: Format.B10G11R11UfloatPack32));
    }

    [Fact]
    public void GpuDmaCopyAcceptsMatchingImages()
    {
        Assert.True(VulkanVideoPresenter.IsGuestImageCopyCompatible(
            sourceWidth: 960,
            sourceHeight: 540,
            sourceFormat: Format.B10G11R11UfloatPack32,
            destinationWidth: 960,
            destinationHeight: 540,
            destinationFormat: Format.B10G11R11UfloatPack32));
    }

    [Fact]
    public void GpuDmaCopyAcceptsInitializedGpuAuthoredSource()
    {
        Assert.True(VulkanVideoPresenter.ShouldMirrorGuestImageCopyOnGpu(
            sourceInitialized: true,
            sourceIsCpuBacked: false));
    }

    [Fact]
    public void GpuDmaCopyRejectsCpuBackedSource()
    {
        Assert.False(VulkanVideoPresenter.ShouldMirrorGuestImageCopyOnGpu(
            sourceInitialized: true,
            sourceIsCpuBacked: true));
    }

    [Fact]
    public void GpuDmaCopyRejectsUninitializedSource()
    {
        Assert.False(VulkanVideoPresenter.ShouldMirrorGuestImageCopyOnGpu(
            sourceInitialized: false,
            sourceIsCpuBacked: false));
    }
}
