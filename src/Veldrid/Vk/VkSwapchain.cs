using System;
using System.Linq;
using Vulkan;
using static Vulkan.VulkanNative;
using static Veldrid.Vk.VulkanUtil;

namespace Veldrid.Vk
{
    internal unsafe class VkSwapchain : Swapchain
    {
        public override Framebuffer Framebuffer => framebuffer;

        public override bool IsDisposed => disposed;

        public VkSwapchainKHR DeviceSwapchain => deviceSwapchain;
        public uint ImageIndex => currentImageIndex;
        internal VkSemaphore PresentSemaphore => framebuffer.PresentSemaphore;

        internal void InitialiseAcquiredImage() => framebuffer.InitialiseAcquiredImage();
        public Vulkan.VkFence ImageAvailableFence => imageAvailableFence;
        public VkSurfaceKHR Surface { get; }

        public VkQueue PresentQueue => presentQueue;
        public uint PresentQueueIndex => presentQueueIndex;
        public ResourceRefCount RefCount { get; }
        public bool FullScreenExclusiveSupported => fullScreenExclusiveSupported;
        public bool FullScreenExclusiveAcquired => fullScreenExclusive?.IsAcquired ?? false;
        public VkPresentModeKHR CurrentPresentMode => currentPresentMode;
        public uint ImageCount => framebuffer.ImageCount;
        public bool SupportsFrameCapture { get; private set; }
        public bool AcquirePacingActive => lowLatencyPresentation;

        public override string Name
        {
            get => name;
            set
            {
                name = value;
                gd.SetResourceName(this, value);
            }
        }

        public override bool SyncToVerticalBlank
        {
            get => newSyncToVBlank ?? syncToVBlank;
            set
            {
                newSyncToVBlank = syncToVBlank == value ? null : value;
            }
        }

        private bool allowTearing;

        public bool AllowTearing
        {
            get => allowTearing;
            set
            {
                if (allowTearing == value)
                    return;

                allowTearing = value;

                recreateAndReacquire(framebuffer.Width, framebuffer.Height);
            }
        }

        private readonly VkGraphicsDevice gd;
        private readonly VkSwapchainFramebuffer framebuffer;
        private readonly uint presentQueueIndex;
        private readonly VkQueue presentQueue;
        private readonly bool colorSrgb;
        private readonly bool lowLatencyPresentation;
        private VkPresentModeKHR currentPresentMode;
        private bool acquisitionPending;
        private VkSwapchainKHR deviceSwapchain;
        private Vulkan.VkFence imageAvailableFence;
        private bool syncToVBlank;
        private bool? newSyncToVBlank;
        private uint currentImageIndex;
        private string name;
        private bool disposed;
        private readonly VkFullScreenExclusive fullScreenExclusive;
        private bool fullScreenExclusiveSupported;

        public VkSwapchain(VkGraphicsDevice gd, ref SwapchainDescription description)
            : this(gd, ref description, VkSurfaceKHR.Null)
        {
        }

        public VkSwapchain(VkGraphicsDevice gd, ref SwapchainDescription description, VkSurfaceKHR existingSurface)
        {
            this.gd = gd;
            lowLatencyPresentation = gd.LowLatencyPresentation;
            syncToVBlank = description.SyncToVerticalBlank;
            colorSrgb = description.ColorSrgb;

            SwapchainSource swapchainSource = description.Source;
            if (gd.FullScreenExclusiveEnabled && swapchainSource is Win32SwapchainSource win32)
                fullScreenExclusive = new VkFullScreenExclusive(gd, win32.Hwnd);

            Surface = existingSurface == VkSurfaceKHR.Null
                ? VkSurfaceUtil.CreateSurface(gd, gd.Instance, swapchainSource)
                : existingSurface;

            if (!getPresentQueueIndex(out presentQueueIndex)) throw new VeldridException("The system does not support presenting the given Vulkan surface.");

            vkGetDeviceQueue(this.gd.Device, presentQueueIndex, 0, out presentQueue);

            framebuffer = new VkSwapchainFramebuffer(gd, this, Surface, description.Width, description.Height, description.DepthFormat);

            if (!createSwapchain(description.Width, description.Height))
                throw new VeldridException("The Vulkan surface has no drawable extent during initialisation.");

            var fenceCi = VkFenceCreateInfo.New();
            fenceCi.flags = VkFenceCreateFlags.None;
            CheckResult(vkCreateFence(this.gd.Device, ref fenceCi, null, out imageAvailableFence));

            // A false result means recreation already consumed and reset the acquisition fence.
            if (AcquireNextImage(this.gd.Device, VkSemaphore.Null, imageAvailableFence))
            {
                CheckResult(vkWaitForFences(this.gd.Device, 1, ref imageAvailableFence, true, ulong.MaxValue));
                CheckResult(vkResetFences(this.gd.Device, 1, ref imageAvailableFence));
                InitialiseAcquiredImage();
            }

            RefCount = new ResourceRefCount(disposeCore);
        }

        #region Disposal

        public override void Dispose()
        {
            RefCount.Decrement();
        }

        #endregion

        public override void Resize(uint width, uint height)
        {
            recreateAndReacquire(width, height);
        }

        public override void WaitForNextFrameReady()
        {
            if (!acquisitionPending)
                return;

            var fence = imageAvailableFence;
            CheckResult(vkWaitForFences(gd.Device, 1, ref fence, true, ulong.MaxValue));
            CheckResult(vkResetFences(gd.Device, 1, ref fence));
            InitialiseAcquiredImage();
            acquisitionPending = false;
        }

        internal void MarkAcquisitionPending() => acquisitionPending = true;

        internal void EnsureAcquiredImageReady()
        {
            if (acquisitionPending)
                WaitForNextFrameReady();
        }

        public bool AcquireNextImage(VkDevice device, VkSemaphore semaphore, Vulkan.VkFence fence)
        {
            if (newSyncToVBlank != null)
            {
                syncToVBlank = newSyncToVBlank.Value;
                newSyncToVBlank = null;
                recreateAndReacquire(framebuffer.Width, framebuffer.Height);
                return false;
            }

            if (fullScreenExclusiveSupported)
                fullScreenExclusive.Update(deviceSwapchain);

            var result = acquireImage(device, semaphore, fence);
            if ((int)result == VkFullScreenExclusive.ModeLost)
            {
                fullScreenExclusive?.Lost();
                recreateAndReacquire(framebuffer.Width, framebuffer.Height);
                return false;
            }

            if (result == VkResult.ErrorOutOfDateKHR)
            {
                recreateAndReacquire(framebuffer.Width, framebuffer.Height);
                return false;
            }

            // SUBOPTIMAL still acquired an image and signalled the supplied fence. Consume both
            // normally; discarding that acquisition and replacing the swapchain loses ownership.
            if (result != VkResult.Success && result != VkResult.SuboptimalKHR)
                throw new VeldridException($"Could not acquire next image from the Vulkan swapchain: {result}.");

            framebuffer.SetImageIndex(currentImageIndex);
            return true;
        }

        internal void HandleFullScreenExclusiveResult(VkResult result)
        {
            if ((int)result == VkFullScreenExclusive.ModeLost)
                fullScreenExclusive?.Lost();
        }

        private VkResult acquireImage(VkDevice device, VkSemaphore semaphore, Vulkan.VkFence fence) =>
            vkAcquireNextImageKHR(device, deviceSwapchain, ulong.MaxValue, semaphore, fence, ref currentImageIndex);

        private void recreateAndReacquire(uint width, uint height)
        {
            // A pending acquisition has already signalled, or will signal, this fence.
            // Consume it before any path can reuse the fence for another acquisition.
            if (acquisitionPending)
            {
                CheckResult(vkWaitForFences(gd.Device, 1, ref imageAvailableFence, true, ulong.MaxValue));
                CheckResult(vkResetFences(gd.Device, 1, ref imageAvailableFence));
                acquisitionPending = false;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!createSwapchain(width, height))
                    throw new VeldridException("The Vulkan surface has no drawable extent; rendering must be suspended before resizing.");

                // Keep exclusive acquisition out of this recovery loop to avoid repeatedly
                // losing ownership while the operating system is changing the display mode.
                var result = acquireImage(gd.Device, VkSemaphore.Null, imageAvailableFence);
                if ((int)result == VkFullScreenExclusive.ModeLost)
                {
                    fullScreenExclusive?.Lost();
                    continue;
                }
                if (result == VkResult.ErrorOutOfDateKHR)
                    continue;
                if (result != VkResult.Success && result != VkResult.SuboptimalKHR)
                    CheckResult(result);

                framebuffer.SetImageIndex(currentImageIndex);
                CheckResult(vkWaitForFences(gd.Device, 1, ref imageAvailableFence, true, ulong.MaxValue));
                CheckResult(vkResetFences(gd.Device, 1, ref imageAvailableFence));
                InitialiseAcquiredImage();
                return;
            }
            throw new VeldridException("The Vulkan swapchain remained out of date after three recreation attempts.");
        }

        private bool createSwapchain(uint width, uint height)
        {
            // Obtain the surface capabilities first -- this will indicate whether the surface has been lost.
            var result = vkGetPhysicalDeviceSurfaceCapabilitiesKHR(gd.PhysicalDevice, Surface, out var surfaceCapabilities);
            if (result == VkResult.ErrorSurfaceLostKHR) throw new VeldridException("The Swapchain's underlying surface has been lost.");
            CheckResult(result);

            if (surfaceCapabilities.currentExtent.width == 0 || surfaceCapabilities.currentExtent.height == 0)
                return false;

            if (surfaceCapabilities.minImageExtent.width == 0 && surfaceCapabilities.minImageExtent.height == 0
                                                              && surfaceCapabilities.maxImageExtent.width == 0 && surfaceCapabilities.maxImageExtent.height == 0)
                return false;

            if (deviceSwapchain != VkSwapchainKHR.Null) gd.WaitForIdle();

            currentImageIndex = 0;
            var formats = getSurfaceFormats();

            var desiredFormat = colorSrgb
                ? VkFormat.B8g8r8a8Srgb
                : VkFormat.B8g8r8a8Unorm;

            var surfaceFormat = new VkSurfaceFormatKHR();

            if (formats.Length == 1 && formats[0].format == VkFormat.Undefined)
                surfaceFormat = new VkSurfaceFormatKHR { colorSpace = VkColorSpaceKHR.SrgbNonlinearKHR, format = desiredFormat };
            else
            {
                foreach (var format in formats)
                {
                    if (format.colorSpace == VkColorSpaceKHR.SrgbNonlinearKHR && format.format == desiredFormat)
                    {
                        surfaceFormat = format;
                        break;
                    }
                }

                if (surfaceFormat.format == VkFormat.Undefined)
                {
                    var alternateFormat = colorSrgb ? VkFormat.R8g8b8a8Srgb : VkFormat.R8g8b8a8Unorm;
                    foreach (var format in formats)
                    {
                        if (format.colorSpace == VkColorSpaceKHR.SrgbNonlinearKHR && format.format == alternateFormat)
                        {
                            surfaceFormat = format;
                            break;
                        }
                    }

                    if (surfaceFormat.format == VkFormat.Undefined)
                    {
                        if (colorSrgb)
                            throw new VeldridException("Unable to create an sRGB Swapchain for this surface.");
                        surfaceFormat = formats[0];
                    }
                }
            }

            var presentModes = getSurfacePresentModes();

            fullScreenExclusiveSupported = fullScreenExclusive?.Query(Surface) == true;
            if (fullScreenExclusiveSupported)
            {
                var exclusiveModes = fullScreenExclusive.PresentModes(Surface);
                fullScreenExclusiveSupported = exclusiveModes.Length > 0;
                if (fullScreenExclusiveSupported)
                    presentModes = exclusiveModes;
                else
                    Console.WriteLine("[VK FSE] exclusive presentation modes unavailable; using ordinary Vulkan presentation.");
            }

            var presentMode = VkPresentModeKHR.FifoKHR;

            if (syncToVBlank)
            {
                // Strict FIFO preserves the caller's VSync contract in the experiment.
                if (!lowLatencyPresentation && presentModes.Contains(VkPresentModeKHR.FifoRelaxedKHR))
                    presentMode = VkPresentModeKHR.FifoRelaxedKHR;
            }
            else if (allowTearing && presentModes.Contains(VkPresentModeKHR.ImmediateKHR))
                presentMode = VkPresentModeKHR.ImmediateKHR;
            else if (presentModes.Contains(VkPresentModeKHR.MailboxKHR))
                presentMode = VkPresentModeKHR.MailboxKHR;
            else if (presentModes.Contains(VkPresentModeKHR.ImmediateKHR))
                presentMode = VkPresentModeKHR.ImmediateKHR;

            uint maxImageCount = surfaceCapabilities.maxImageCount == 0 ? uint.MaxValue : surfaceCapabilities.maxImageCount;
            // Swapchain image count is not a frames-in-flight limit. Reducing the
            // available images can reduce FIFO queue depth, at a throughput cost.
            // Never request fewer than the surface minimum; keep at least two.
            uint desiredImageCount = lowLatencyPresentation
                ? Math.Max(2u, surfaceCapabilities.minImageCount)
                : surfaceCapabilities.minImageCount + 1;
            uint imageCount = Math.Min(maxImageCount, desiredImageCount);

            var swapchainCi = VkSwapchainCreateInfoKHR.New();
            swapchainCi.surface = Surface;
            if (gd.PreferredPresentMode >= 0 && gd.PreferredPresentMode <= 2)
            {
                var preferred = (VkPresentModeKHR)gd.PreferredPresentMode;
                presentMode = presentModes.Contains(preferred) ? preferred : VkPresentModeKHR.FifoKHR;
            }
            swapchainCi.presentMode = presentMode;
            swapchainCi.imageFormat = surfaceFormat.format;
            swapchainCi.imageColorSpace = surfaceFormat.colorSpace;
            uint clampedWidth = Util.Clamp(width, surfaceCapabilities.minImageExtent.width, surfaceCapabilities.maxImageExtent.width);
            uint clampedHeight = Util.Clamp(height, surfaceCapabilities.minImageExtent.height, surfaceCapabilities.maxImageExtent.height);
            swapchainCi.imageExtent = surfaceCapabilities.currentExtent.width != uint.MaxValue
                ? surfaceCapabilities.currentExtent
                : new VkExtent2D { width = clampedWidth, height = clampedHeight };
            swapchainCi.minImageCount = imageCount;
            swapchainCi.imageArrayLayers = 1;
            // Initialisation clears each acquired image via a transfer operation.
            const VkImageUsageFlags requiredUsage = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferDst;
            if ((surfaceCapabilities.supportedUsageFlags & requiredUsage) != requiredUsage)
                throw new VeldridException("The Vulkan surface requires colour-attachment and transfer-destination support.");
            swapchainCi.imageUsage = requiredUsage;
            // Capture is optional: unsupported surfaces must still be able to present.
            if ((surfaceCapabilities.supportedUsageFlags & VkImageUsageFlags.TransferSrc) != 0)
                swapchainCi.imageUsage |= VkImageUsageFlags.TransferSrc;

            var queueFamilyIndices = new FixedArray2<uint>(gd.GraphicsQueueIndex, presentQueueIndex);

            if (gd.GraphicsQueueIndex != presentQueueIndex)
            {
                swapchainCi.imageSharingMode = VkSharingMode.Concurrent;
                swapchainCi.queueFamilyIndexCount = 2;
                swapchainCi.pQueueFamilyIndices = &queueFamilyIndices.First;
            }
            else
            {
                swapchainCi.imageSharingMode = VkSharingMode.Exclusive;
                swapchainCi.queueFamilyIndexCount = 0;
            }

            swapchainCi.preTransform = (surfaceCapabilities.supportedTransforms & VkSurfaceTransformFlagsKHR.IdentityKHR) != 0
                ? VkSurfaceTransformFlagsKHR.IdentityKHR : surfaceCapabilities.currentTransform;
            // Prefer opaque, then select the first supported compositing mode.
            uint alphaFlags = (uint)surfaceCapabilities.supportedCompositeAlpha;
            if (alphaFlags == 0)
                throw new VeldridException("The Vulkan surface advertises no composite alpha modes.");
            swapchainCi.compositeAlpha = (VkCompositeAlphaFlagsKHR)(alphaFlags & (~alphaFlags + 1u));
            swapchainCi.clipped = true;

            var oldSwapchain = deviceSwapchain;
            if (oldSwapchain != VkSwapchainKHR.Null)
                fullScreenExclusive?.Release(oldSwapchain);
            swapchainCi.oldSwapchain = oldSwapchain;

            var win32Info = new VkFullScreenExclusive.Win32Info { SType = 1000255001, Monitor = fullScreenExclusive?.Monitor ?? IntPtr.Zero };
            var exclusiveInfo = new VkFullScreenExclusive.ExclusiveInfo { SType = 1000255000, Next = &win32Info, Mode = 3 };
            if (fullScreenExclusiveSupported)
                swapchainCi.pNext = &exclusiveInfo;

            result = vkCreateSwapchainKHR(gd.Device, ref swapchainCi, null, out var replacement);
            CheckResult(result);
            deviceSwapchain = replacement;
            if (oldSwapchain != VkSwapchainKHR.Null) vkDestroySwapchainKHR(gd.Device, oldSwapchain, null);

            SupportsFrameCapture = (swapchainCi.imageUsage & VkImageUsageFlags.TransferSrc) != 0;
            currentPresentMode = presentMode;
            framebuffer.SetNewSwapchain(deviceSwapchain, width, height, surfaceFormat, swapchainCi.imageExtent);
            return true;
        }

        private VkSurfaceFormatKHR[] getSurfaceFormats()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint count = 0;
                CheckResult(vkGetPhysicalDeviceSurfaceFormatsKHR(gd.PhysicalDevice, Surface, ref count, null));
                if (count == 0)
                    throw new VeldridException("The Vulkan surface advertises no colour formats.");
                var formats = new VkSurfaceFormatKHR[count];
                var result = vkGetPhysicalDeviceSurfaceFormatsKHR(gd.PhysicalDevice, Surface, ref count, out formats[0]);
                if (result == VkResult.Incomplete)
                    continue;
                CheckResult(result);
                if (count == 0)
                    continue;
                Array.Resize(ref formats, (int)count);
                return formats;
            }
            throw new VeldridException("Vulkan surface format enumeration did not stabilise.");
        }

        private VkPresentModeKHR[] getSurfacePresentModes()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint count = 0;
                CheckResult(vkGetPhysicalDeviceSurfacePresentModesKHR(gd.PhysicalDevice, Surface, ref count, null));
                if (count == 0)
                    throw new VeldridException("The Vulkan surface advertises no presentation modes.");
                var modes = new VkPresentModeKHR[count];
                var result = vkGetPhysicalDeviceSurfacePresentModesKHR(gd.PhysicalDevice, Surface, ref count, out modes[0]);
                if (result == VkResult.Incomplete)
                    continue;
                CheckResult(result);
                if (count == 0)
                    continue;
                Array.Resize(ref modes, (int)count);
                return modes;
            }
            throw new VeldridException("Vulkan presentation mode enumeration did not stabilise.");
        }

        private bool getPresentQueueIndex(out uint queueFamilyIndex)
        {
            uint deviceGraphicsQueueIndex = gd.GraphicsQueueIndex;
            uint devicePresentQueueIndex = gd.PresentQueueIndex;

            if (queueSupportsPresent(deviceGraphicsQueueIndex, Surface))
            {
                queueFamilyIndex = deviceGraphicsQueueIndex;
                return true;
            }

            if (deviceGraphicsQueueIndex != devicePresentQueueIndex && queueSupportsPresent(devicePresentQueueIndex, Surface))
            {
                queueFamilyIndex = devicePresentQueueIndex;
                return true;
            }

            queueFamilyIndex = 0;
            return false;
        }

        private bool queueSupportsPresent(uint queueFamilyIndex, VkSurfaceKHR surface)
        {
            var result = vkGetPhysicalDeviceSurfaceSupportKHR(
                gd.PhysicalDevice,
                queueFamilyIndex,
                surface,
                out var supported);
            CheckResult(result);
            return supported;
        }

        private void disposeCore()
        {
            if (acquisitionPending)
            {
                var fence = imageAvailableFence;
                vkWaitForFences(gd.Device, 1, ref fence, true, ulong.MaxValue);
                acquisitionPending = false;
            }

            fullScreenExclusive?.Release(deviceSwapchain);
            vkDestroyFence(gd.Device, imageAvailableFence, null);
            framebuffer.Dispose();
            vkDestroySwapchainKHR(gd.Device, deviceSwapchain, null);
            vkDestroySurfaceKHR(gd.Instance, Surface, null);

            disposed = true;
        }
    }
}
