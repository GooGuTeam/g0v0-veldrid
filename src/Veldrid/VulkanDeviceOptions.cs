namespace Veldrid
{
    /// <summary>
    ///     A structure describing Vulkan-specific device creation options.
    /// </summary>
    public struct VulkanDeviceOptions
    {
        /// <summary>
        ///     An array of required Vulkan instance extensions. Entries in this array will be enabled in the GraphicsDevice's
        ///     created VkInstance.
        /// </summary>
        public string[] InstanceExtensions;

        /// <summary>
        ///     An array of required Vulkan device extensions. Entries in this array will be enabled in the GraphicsDevice's
        ///     created VkDevice.
        /// </summary>
        public string[] DeviceExtensions;

        /// <summary>
        /// Requests the Win32 external-memory/keyed-mutex extension bundle if all
        /// dependencies are available. Missing support never prevents device creation.
        /// </summary>
        public bool EnableWin32CaptureInterop;

        /// <summary>
        /// Requests sampled-image non-uniform indexing and runtime descriptor arrays when the
        /// Vulkan 1.0 extension dependency bundle is available. Unsupported devices keep scalar rendering.
        /// This does not enable partially bound descriptors or updates after binding.
        /// </summary>
        public bool EnableTextureDescriptorIndexing;

        /// <summary>
        ///     Constructs a new VulkanDeviceOptions.
        /// </summary>
        /// <param name="instanceExtensions">
        ///     An array of required Vulkan instance extensions. Entries in this array will be
        ///     enabled in the GraphicsDevice's created VkInstance.
        /// </param>
        /// <param name="deviceExtensions">
        ///     An array of required Vulkan device extensions. Entries in this array will be enabled
        ///     in the GraphicsDevice's created VkDevice.
        /// </param>
        public VulkanDeviceOptions(string[] instanceExtensions, string[] deviceExtensions)
        {
            InstanceExtensions = instanceExtensions;
            DeviceExtensions = deviceExtensions;
            EnableWin32CaptureInterop = false;
            EnableTextureDescriptorIndexing = false;
        }
    }
}
