using System;
using System.Runtime.InteropServices;
using Vulkan;

namespace Veldrid.Vk
{
    // Bind locally because the pinned Vulkan bindings predate this optional Win32 extension.
    internal sealed unsafe class VkFullScreenExclusive
    {
        internal const int ModeLost = -1000255000;
        private readonly VkGraphicsDevice gd;
        private readonly IntPtr hwnd;
        private readonly GetCapabilities getCapabilities;
        private readonly GetPresentModes getPresentModes;
        private readonly ExclusiveMode acquire;
        private readonly ExclusiveMode release;
        private bool acquired;
        private long nextAttempt;
        internal bool IsAcquired => acquired;
        internal IntPtr Monitor { get; private set; }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult GetCapabilities(VkPhysicalDevice device, SurfaceInfo* info, Capabilities* capabilities);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult GetPresentModes(VkPhysicalDevice device, SurfaceInfo* info, uint* count, VkPresentModeKHR* modes);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult ExclusiveMode(VkDevice device, VkSwapchainKHR swapchain);

        [StructLayout(LayoutKind.Sequential)]
        internal struct ExclusiveInfo
        {
            public int SType;
            public void* Next;
            public int Mode;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Win32Info
        {
            public int SType;
            public void* Next;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SurfaceInfo
        {
            public int SType;
            public void* Next;
            public VkSurfaceKHR Surface;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Capabilities
        {
            public int SType;
            public void* Next;
            public VkSurfaceCapabilitiesKHR Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExclusiveCapabilities
        {
            public int SType;
            public void* Next;
            public uint Supported;
        }

        internal VkFullScreenExclusive(VkGraphicsDevice gd, IntPtr hwnd)
        {
            this.gd = gd;
            this.hwnd = hwnd;
            getCapabilities = bind<GetCapabilities>(gd.getInstanceProcAddr("vkGetPhysicalDeviceSurfaceCapabilities2KHR"));
            getPresentModes = bind<GetPresentModes>(gd.getInstanceProcAddr("vkGetPhysicalDeviceSurfacePresentModes2EXT"));
            acquire = bind<ExclusiveMode>(gd.getDeviceProcAddr("vkAcquireFullScreenExclusiveModeEXT"));
            release = bind<ExclusiveMode>(gd.getDeviceProcAddr("vkReleaseFullScreenExclusiveModeEXT"));
            if (!IsAvailable)
                Console.WriteLine("[VK FSE] required extension entry points unavailable; using ordinary Vulkan presentation.");
        }

        internal bool IsAvailable => getCapabilities != null && getPresentModes != null && acquire != null && release != null;

        private static T bind<T>(IntPtr address) where T : Delegate =>
            address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);

        internal bool Query(VkSurfaceKHR surface)
        {
            if (!IsAvailable)
                return false;
            Monitor = MonitorFromWindow(hwnd, 2);
            var win32 = new Win32Info { SType = 1000255001, Monitor = Monitor };
            var exclusive = new ExclusiveInfo { SType = 1000255000, Next = &win32, Mode = 3 };
            var info = new SurfaceInfo { SType = 1000119000, Next = &exclusive, Surface = surface };
            var support = new ExclusiveCapabilities { SType = 1000255002 };
            var caps = new Capabilities { SType = 1000119001, Next = &support };
            var result = getCapabilities(gd.PhysicalDevice, &info, &caps);
            Console.WriteLine($"[VK FSE] capabilities: {result}, supported={support.Supported}, monitor=0x{Monitor.ToInt64():X}");
            return result == VkResult.Success && support.Supported != 0;
        }

        internal VkPresentModeKHR[] PresentModes(VkSurfaceKHR surface)
        {
            var win32 = new Win32Info { SType = 1000255001, Monitor = Monitor };
            var exclusive = new ExclusiveInfo { SType = 1000255000, Next = &win32, Mode = 3 };
            var info = new SurfaceInfo { SType = 1000119000, Next = &exclusive, Surface = surface };
            // This optional extension must not prevent ordinary Vulkan presentation.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint count = 0;
                if (getPresentModes(gd.PhysicalDevice, &info, &count, null) != VkResult.Success || count == 0)
                    return Array.Empty<VkPresentModeKHR>();
                var modes = new VkPresentModeKHR[count];
                VkResult result;
                fixed (VkPresentModeKHR* data = modes)
                    result = getPresentModes(gd.PhysicalDevice, &info, &count, data);
                if (result == VkResult.Incomplete)
                    continue;
                if (result != VkResult.Success)
                    return Array.Empty<VkPresentModeKHR>();
                Array.Resize(ref modes, (int)count);
                return modes;
            }
            return Array.Empty<VkPresentModeKHR>();
        }

        internal void Update(VkSwapchainKHR swapchain)
        {
            // Do not take exclusive ownership for a windowed, minimised, unfocused or moved window.
            var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            bool eligible = gd.ExclusiveFullscreenAllowed && GetForegroundWindow() == hwnd && !IsIconic(hwnd)
                            && MonitorFromWindow(hwnd, 2) == Monitor
                            && GetWindowRect(hwnd, out var rect) && GetMonitorInfo(Monitor, ref monitorInfo)
                            && rect.Left == monitorInfo.Bounds.Left && rect.Top == monitorInfo.Bounds.Top
                            && rect.Right == monitorInfo.Bounds.Right && rect.Bottom == monitorInfo.Bounds.Bottom;
            if (!eligible)
            {
                Release(swapchain);
                return;
            }

            if (acquired || Environment.TickCount64 < nextAttempt)
                return;

            nextAttempt = Environment.TickCount64 + 2000;
            gd.WaitForIdle();
            var result = acquire(gd.Device, swapchain);
            acquired = result == VkResult.Success;
            Console.WriteLine($"[VK FSE] acquire: {result} ({(int)result}), acquired={acquired}");
        }

        internal void Release(VkSwapchainKHR swapchain)
        {
            if (!acquired)
                return;

            gd.WaitForIdle();
            var result = release(gd.Device, swapchain);
            acquired = false;
            nextAttempt = Environment.TickCount64 + 500;
            Console.WriteLine($"[VK FSE] release: {result} ({(int)result})");
        }

        internal void Lost()
        {
            acquired = false;
            nextAttempt = Environment.TickCount64 + 2000;
            Console.WriteLine("[VK FSE] exclusive ownership lost; retry deferred.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo { public uint Size; public Rect Bounds, Work; public uint Flags; }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    }
}
