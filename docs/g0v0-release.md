# g0v0 Veldrid prerelease

Packages: `g0v0.Veldrid`, `g0v0.Veldrid.MetalBindings`, `g0v0.Veldrid.OpenGLBindings`.
Existing `ppy.*` assembly names are retained for compatibility; install the fork packages instead of the corresponding ppy packages. Upstream attribution and LICENSE are retained.

## Scope

- Vulkan capture/interoperability capability negotiation, presentation preferences and optional Windows FSE.
- Queue-family selection, surface format/extent/composite-alpha handling, bounded enumeration retries and suitable-adapter filtering.
- Bounded swapchain reacquisition instead of recursive recovery; pending acquisition fences are consumed before reuse. Fatal present errors propagate.
- D3D11 fullscreen eligibility follows host window mode, negotiates refresh timing, and does not double-release the native swapchain on recreation.

## Build and release

`dotnet pack src/Veldrid.sln -c Release -warnaserror -p:NoWarn=1591 -p:G0V0PackageVersion=4.9.0-g0v0.1 -o artifacts`

CS1591 is the existing incomplete-public-documentation diagnostic; it is suppressed explicitly for release builds, not all warnings. No tests or GPU probes are invoked. Tagged builds publish only `g0v0.*` packages. Configure `NUGET_API_KEY`, or `NUGET_USER` plus a NuGet trusted-publishing policy for this repository's `dotnet.yml` workflow. Never use an upstream publishing token or push upstream tags.

## Known limits

This is a prerelease with build/static inspection evidence only. Different-driver/hardware coverage, FSE transitions and recording interoperability remain unverified. Zero drawable extents during recovery produce an explicit error; full suspend/resume and surface/device-loss recovery remain unfinished. Adapter filtering does not retry another GPU after logical-device creation fails. Partial initialisation cleanup still needs a broader ownership audit.
