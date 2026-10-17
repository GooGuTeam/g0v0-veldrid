using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Veldrid.D3D11
{
    internal class D3D11Swapchain : Swapchain
    {
        public override Framebuffer Framebuffer => framebuffer;

        public PresentFlags PresentFlags
        {
            get
            {
                if ((flags & SwapChainFlags.AllowTearing) != 0 && !exclusiveFullscreen && !SyncToVerticalBlank)
                    return PresentFlags.AllowTearing;

                return PresentFlags.None;
            }
        }

        public IDXGISwapChain DxgiSwapChain { get; private set; }

        public int SyncInterval { get; private set; }

        public override bool IsDisposed => disposed;

        public override string Name
        {
            get
            {
                unsafe
                {
                    byte* pname = stackalloc byte[1024];
                    int size = 1024 - 1;
                    DxgiSwapChain.GetPrivateData(CommonGuid.DebugObjectName, ref size, new IntPtr(pname));
                    pname[size] = 0;
                    return Marshal.PtrToStringAnsi(new IntPtr(pname));
                }
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                    DxgiSwapChain.SetPrivateData(CommonGuid.DebugObjectName, 0, IntPtr.Zero);
                else
                {
                    IntPtr namePtr = Marshal.StringToHGlobalAnsi(value);
                    DxgiSwapChain.SetPrivateData(CommonGuid.DebugObjectName, value.Length, namePtr);
                    Marshal.FreeHGlobal(namePtr);
                }
            }
        }

        public override bool SyncToVerticalBlank
        {
            get => vsync;
            set
            {
                vsync = value;
                SyncInterval = D3D11Util.GetSyncInterval(value);
            }
        }

        public bool AllowTearing
        {
            get => allowTearing;
            set
            {
                if (allowTearing == value)
                    return;

                allowTearing = value;

                if (!canTear)
                    return;

                recreateSwapchain();
            }
        }

        private readonly D3D11GraphicsDevice gd;
        private readonly SwapchainDescription description;
        private readonly PixelFormat? depthFormat;

        private readonly object referencedCLsLock = new object();

        private readonly bool canTear;
        private readonly bool canCreateFrameLatencyWaitableObject;
        private readonly bool lowLatencyPresentation;
        private readonly Format colorFormat;
        private bool vsync;
        private D3D11Framebuffer framebuffer;
        private D3D11Texture depthTexture;
        private float pixelScale = 1f;
        private SwapChainFlags flags;
        private bool disposed;
        private FrameLatencyWaitHandle frameLatencyWaitHandle;
        public bool FrameLatencyWaitActive => frameLatencyWaitHandle != null;
        private readonly HashSet<D3D11CommandList> referencedCLs = new HashSet<D3D11CommandList>();

        private bool allowTearing;
        private bool exclusiveFullscreen;
        private long nextExclusiveAttempt;

        private uint width;
        private uint height;

        private ID3D11Texture2D backBufferTexture;

        public D3D11Swapchain(D3D11GraphicsDevice gd, ref SwapchainDescription description)
        {
            this.gd = gd;
            this.description = description;
            depthFormat = description.DepthFormat;
            SyncToVerticalBlank = description.SyncToVerticalBlank;

            colorFormat = description.ColorSrgb
                ? Format.B8G8R8A8_UNorm_SRgb
                : Format.B8G8R8A8_UNorm;

            // previously we had an extension method ("GetParentOrNull") which makes this read a lot better,
            // but it resulted in AOT compilation crashes on iOS, therefore we can only do this inline.
            using (var dxgiFactory5 = this.gd.Adapter.GetParent(out IDXGIFactory5 f).Success ? f : null)
                canTear = dxgiFactory5?.PresentAllowTearing == true;

            // previously we had an extension method ("GetParentOrNull") which makes this read a lot better,
            // but it resulted in AOT compilation crashes on iOS, therefore we can only do this inline.
            using (var dxgiFactory3 = this.gd.Adapter.GetParent(out IDXGIFactory3 f).Success ? f : null)
                canCreateFrameLatencyWaitableObject = dxgiFactory3 != null;

            width = description.Width;
            height = description.Height;
            lowLatencyPresentation = this.gd.LowLatencyPresentation;

            recreateSwapchain();
        }

        #region Disposal

        public override void Dispose()
        {
            if (!disposed)
            {
                depthTexture?.Dispose();
                framebuffer.Dispose();
                backBufferTexture?.Dispose();
                if (exclusiveFullscreen)
                    DxgiSwapChain.SetFullscreenState(false, null);
                DxgiSwapChain.Dispose();
                frameLatencyWaitHandle?.Dispose();
                frameLatencyWaitHandle = null;

                disposed = true;
            }
        }

        #endregion

        public override void Resize(uint width, uint height)
        {
            this.width = width;
            this.height = height;

            lock (referencedCLsLock)
            {
                foreach (var cl in referencedCLs) cl.Reset();

                referencedCLs.Clear();
            }

            bool resizeBuffers = false;

            if (framebuffer != null)
            {
                resizeBuffers = true;
                depthTexture?.Dispose();

                backBufferTexture.Dispose();
                framebuffer.Dispose();
            }

            uint actualWidth = (uint)(width * pixelScale);
            uint actualHeight = (uint)(height * pixelScale);
            if (resizeBuffers) DxgiSwapChain.ResizeBuffers(2, (int)actualWidth, (int)actualHeight, colorFormat, flags).CheckError();

            // Get the backbuffer from the swapchain
            backBufferTexture = DxgiSwapChain.GetBuffer<ID3D11Texture2D>(0);

            if (depthFormat != null)
            {
                var depthDesc = new TextureDescription(
                    actualWidth, actualHeight, 1, 1, 1,
                    depthFormat.Value,
                    TextureUsage.DepthStencil,
                    TextureType.Texture2D);
                depthTexture = new D3D11Texture(gd.Device, ref depthDesc);
            }

            var backBufferVdTexture = new D3D11Texture(
                backBufferTexture,
                TextureType.Texture2D,
                D3D11Formats.ToVdFormat(colorFormat));

            var desc = new FramebufferDescription(depthTexture, backBufferVdTexture);
            framebuffer = new D3D11Framebuffer(gd.Device, ref desc)
            {
                Swapchain = this
            };
        }

        public override void WaitForNextFrameReady()
        {
            frameLatencyWaitHandle?.WaitOne(1000);
        }

        public void AddCommandListReference(D3D11CommandList cl)
        {
            lock (referencedCLsLock) referencedCLs.Add(cl);
        }

        public void RemoveCommandListReference(D3D11CommandList cl)
        {
            lock (referencedCLsLock) referencedCLs.Remove(cl);
        }

        private void recreateSwapchain()
        {
            if (exclusiveFullscreen)
                DxgiSwapChain?.SetFullscreenState(false, null);
            exclusiveFullscreen = false;
            gd.PrepareSwapchainResize();
            lock (referencedCLsLock)
            {
                foreach (var cl in referencedCLs) cl.Reset();
                referencedCLs.Clear();
            }
            framebuffer?.Dispose();
            framebuffer = null;

            depthTexture?.Dispose();
            depthTexture = null;
            backBufferTexture?.Dispose();
            backBufferTexture = null;
            // Dispose releases the owned COM reference; an additional Release would double-release it.
            DxgiSwapChain?.Dispose();
            DxgiSwapChain = null;

            frameLatencyWaitHandle?.Dispose();
            frameLatencyWaitHandle = null;

            // FlipDiscard is only supported on DXGI 1.4+
            bool canUseFlipDiscard;

            // previously we had an extension method ("GetParentOrNull") which makes this read a lot better,
            // but it resulted in AOT compilation crashes on iOS, therefore we can only do this inline.
            using (var dxgiFactory4 = gd.Adapter.GetParent(out IDXGIFactory4 f).Success ? f : null)
                canUseFlipDiscard = dxgiFactory4 != null;

            var swapEffect = canUseFlipDiscard ? SwapEffect.FlipDiscard : SwapEffect.Discard;

            flags = gd.PreferExclusiveFullscreen ? SwapChainFlags.AllowModeSwitch : SwapChainFlags.None;

            if (!gd.PreferExclusiveFullscreen && AllowTearing && canTear && canUseFlipDiscard)
                flags |= SwapChainFlags.AllowTearing;

            // ALLOW_TEARING and FRAME_LATENCY_WAITABLE_OBJECT can coexist on a
            // windowed flip-model swapchain. Do not disable pacing simply because
            // the caller permits tearing (e.g. borderless fullscreen).
            if (!gd.PreferExclusiveFullscreen && canCreateFrameLatencyWaitableObject && canUseFlipDiscard
                && (lowLatencyPresentation || !(AllowTearing && canTear)))
                flags |= SwapChainFlags.FrameLatencyWaitableObject;

            if (description.Source is Win32SwapchainSource win32Source)
            {
                var dxgiScDesc = new SwapChainDescription
                {
                    BufferCount = 2,
                    Windowed = true,
                    BufferDescription = new ModeDescription(
                        (int)width, (int)height, colorFormat),
                    OutputWindow = win32Source.Hwnd,
                    SampleDescription = new SampleDescription(1, 0),
                    SwapEffect = swapEffect,
                    BufferUsage = Usage.RenderTargetOutput,
                    Flags = flags
                };

                using (var dxgiFactory = gd.Adapter.GetParent<IDXGIFactory>())
                {
                    DxgiSwapChain = dxgiFactory.CreateSwapChain(gd.Device, dxgiScDesc);
                    dxgiFactory.MakeWindowAssociation(win32Source.Hwnd, WindowAssociationFlags.IgnoreAltEnter);
                }
            }
            else if (description.Source is UwpSwapchainSource uwpSource)
            {
                pixelScale = uwpSource.LogicalDpi / 96.0f;

                // Properties of the swap chain
                var swapChainDescription = new SwapChainDescription1
                {
                    AlphaMode = AlphaMode.Ignore,
                    BufferCount = 2,
                    Format = colorFormat,
                    Height = (int)(height * pixelScale),
                    Width = (int)(width * pixelScale),
                    SampleDescription = new SampleDescription(1, 0),
                    SwapEffect = SwapEffect.FlipSequential,
                    BufferUsage = Usage.RenderTargetOutput,
                    Flags = flags
                };

                // Get the Vortice.DXGI factory automatically created when initializing the Direct3D device.
                using (var dxgiFactory = gd.Adapter.GetParent<IDXGIFactory2>())
                {
                    // Create the swap chain and get the highest version available.
                    using (var swapChain1 = dxgiFactory.CreateSwapChainForComposition(gd.Device, swapChainDescription)) DxgiSwapChain = swapChain1.QueryInterface<IDXGISwapChain2>();
                }

#pragma warning disable CA1416 // Validate platform compatibility
                var co = new ComObject(uwpSource.SwapChainPanelNative);
#pragma warning restore CA1416

                var swapchainPanelNative = co.QueryInterfaceOrNull<ISwapChainPanelNative>();

                if (swapchainPanelNative != null)
                    swapchainPanelNative.SetSwapChain(DxgiSwapChain);
                else
                {
                    var bgPanelNative = co.QueryInterfaceOrNull<ISwapChainBackgroundPanelNative>();
                    if (bgPanelNative != null) bgPanelNative.SetSwapChain(DxgiSwapChain);
                }
            }

            if ((flags & SwapChainFlags.FrameLatencyWaitableObject) > 0)
            {
                using (var swapChain2 = DxgiSwapChain.QueryInterfaceOrNull<IDXGISwapChain2>())
                {
                    if (swapChain2 != null)
                    {
                        swapChain2.MaximumFrameLatency = 1;
                        IntPtr waitableObject = swapChain2.FrameLatencyWaitableObject;
                        if (waitableObject != IntPtr.Zero && waitableObject != new IntPtr(-1))
                            frameLatencyWaitHandle = new FrameLatencyWaitHandle(waitableObject);
                    }
                }
            }

            Resize(width, height);
        }

        internal void UpdateExclusiveFullscreen()
        {
            if (!gd.PreferExclusiveFullscreen || !(description.Source is Win32SwapchainSource win32))
                return;

            var monitor = MonitorFromWindow(win32.Hwnd, 2);
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            bool eligible = gd.ExclusiveFullscreenAllowed && GetForegroundWindow() == win32.Hwnd && !IsIconic(win32.Hwnd)
                            && GetWindowRect(win32.Hwnd, out var rect) && GetMonitorInfo(monitor, ref info)
                            && rect.Left == info.Bounds.Left && rect.Top == info.Bounds.Top
                            && rect.Right == info.Bounds.Right && rect.Bottom == info.Bounds.Bottom;

            var stateResult = DxgiSwapChain.GetFullscreenState(out var actual, out var output);
            output?.Dispose();
            if (stateResult.Success)
                exclusiveFullscreen = actual;
            if (eligible == exclusiveFullscreen || (eligible && Environment.TickCount64 < nextExclusiveAttempt))
                return;

            nextExclusiveAttempt = Environment.TickCount64 + 2000;
            if (eligible)
            {
                tryEnterExclusive(monitor);
                return;
            }

            if (DxgiSwapChain.SetFullscreenState(false, null).Success)
            {
                exclusiveFullscreen = false;
                // Flip-model DXGI requires ResizeBuffers after changing fullscreen ownership.
                gd.PrepareSwapchainResize();
                Resize(width, height);
            }
        }

        private bool tryEnterExclusive(IntPtr monitor)
        {
            double requestedRate = gd.FullscreenRefreshRate;
            if (!double.IsFinite(requestedRate) || requestedRate <= 0)
                return false;

            for (int index = 0; gd.Adapter.EnumOutputs(index, out var target).Success; index++)
            {
                using (target)
                {
                    if (target.Description.Monitor != monitor)
                        continue;

                    // Select from advertised progressive modes and preserve the exact rational refresh rate.
                    ModeDescription[] modes;
                    try
                    {
                        modes = target.GetDisplayModeList(colorFormat, 0);
                    }
                    catch (SharpGenException ex)
                    {
                        Console.WriteLine($"[DXGI FSE] display mode enumeration failed: {ex.ResultCode}");
                        return false;
                    }

                    ModeDescription? selected = null;
                    double bestDifference = double.MaxValue;
                    for (int i = 0; i < modes.Length; i++)
                    {
                        var mode = modes[i];
                        if (mode.Width != width || mode.Height != height || mode.RefreshRate.Denominator == 0
                            || mode.RefreshRate.Numerator == 0)
                            continue;
                        double rate = (double)mode.RefreshRate.Numerator / mode.RefreshRate.Denominator;
                        double difference = Math.Abs(rate - requestedRate);
                        if (difference < bestDifference)
                        {
                            selected = mode;
                            bestDifference = difference;
                        }
                    }

                    if (!selected.HasValue)
                        return false;
                    var chosen = selected.Value;
                    var enter = DxgiSwapChain.SetFullscreenState(true, target);
                    if (enter.Failure)
                        return false;
                    var resize = DxgiSwapChain.ResizeTarget(chosen);
                    Console.WriteLine($"[DXGI FSE] requested {width}x{height}@{requestedRate:0.###} Hz; selected {chosen.RefreshRate.Numerator}/{chosen.RefreshRate.Denominator} Hz; ResizeTarget: {resize}");
                    if (resize.Failure)
                    {
                        // Do not retain an exclusive mode whose timing was not successfully requested.
                        var rollback = DxgiSwapChain.SetFullscreenState(false, null);
                        exclusiveFullscreen = rollback.Failure;
                    }
                    else
                        exclusiveFullscreen = true;
                    // Both entry and rollback require rebuilding flip-model backbuffers.
                    gd.PrepareSwapchainResize();
                    Resize(width, height);
                    return resize.Success;
                }
            }
            return false;
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

        private class FrameLatencyWaitHandle : WaitHandle
        {
            public FrameLatencyWaitHandle(IntPtr ptr)
            {
                SafeWaitHandle = new SafeWaitHandle(ptr, true);
            }
        }
    }
}
