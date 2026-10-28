using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vulkan;

namespace Veldrid.Vk
{
    internal static unsafe class VkTextureDescriptorIndexing
    {
        internal const int sampled_image_non_uniform = 4;
        internal const int runtime_descriptor_array = 19;

        internal static Features QueryEnabledFeatures(VkGraphicsDevice device, HashSet<string> availableExtensions,
                                                      HashSet<string> requestedExtensions)
        {
            // The device currently requests Vulkan 1.0, so advertised physical-device core versions
            // do not replace the instance and device extension dependencies.
            if (!availableExtensions.Contains("VK_EXT_descriptor_indexing") || !availableExtensions.Contains("VK_KHR_maintenance3"))
                return default;

            IntPtr address = device.getInstanceProcAddr("vkGetPhysicalDeviceFeatures2KHR");
            if (address == IntPtr.Zero)
                return default;

            Features supported = new Features { SType = 1000161001 };
            PhysicalDeviceFeatures2 query = new PhysicalDeviceFeatures2 { SType = 1000059000, Next = (IntPtr)(&supported) };
            Marshal.GetDelegateForFunctionPointer<GetFeatures2>(address)(device.PhysicalDevice.Handle, &query);
            if (supported.Flags[sampled_image_non_uniform] == 0 || supported.Flags[runtime_descriptor_array] == 0)
                return default;

            Features enabled = new Features { SType = 1000161001 };
            enabled.Flags[sampled_image_non_uniform] = 1;
            enabled.Flags[runtime_descriptor_array] = 1;
            requestedExtensions.Add("VK_EXT_descriptor_indexing");
            requestedExtensions.Add("VK_KHR_maintenance3");
            return enabled;
        }

        // Vulkan ABI: VkPhysicalDeviceDescriptorIndexingFeatures contains 20 VkBool32 fields.
        [StructLayout(LayoutKind.Sequential)]
        internal struct Features
        {
            public uint SType;
            public IntPtr Next;
            public fixed uint Flags[20];
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PhysicalDeviceFeatures2
        {
            public uint SType;
            public IntPtr Next;
            public VkPhysicalDeviceFeatures Features;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void GetFeatures2(IntPtr physicalDevice, PhysicalDeviceFeatures2* features);
    }
}
