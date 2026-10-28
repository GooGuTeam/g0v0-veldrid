# g0v0 Veldrid prerelease

Packages: `g0v0.Veldrid`, `g0v0.Veldrid.MetalBindings`, `g0v0.Veldrid.OpenGLBindings`, `g0v0.Veldrid.SPIRV`.
Existing `ppy.*` assembly names are retained for compatibility; install the fork packages instead of the corresponding ppy packages, including SPIRV. Keeping `ppy.Veldrid.SPIRV` pulls the old renderer back into the dependency graph and can overwrite the fork DLL. Upstream attribution and LICENSE are retained.

The SPIRV managed sources come from `ppy/veldrid-spirv` commit `5911265ec37f81935237b7e5fadf2e6a07f60a96`; original hashes and its MIT licence are in `src/Veldrid.SPIRV/upstream-source.json` and `LICENSE-SPIRV`. Native assets are redistributed byte-for-byte, with unchanged runtime paths, from `ppy.Veldrid.SPIRV` version `1.0.15-g5911265ec3`. This is a build-only asset source: the published fork SPIRV package depends only on `g0v0.Veldrid`, not on the upstream renderer packages.

## Scope

- Vulkan capture/interoperability capability negotiation, presentation preferences and optional Windows FSE.
- Queue-family selection, surface format/extent/composite-alpha handling, bounded enumeration retries and suitable-adapter filtering.
- Bounded swapchain reacquisition instead of recursive recovery; pending acquisition fences are consumed before reuse. Fatal present errors propagate.
- D3D11 fullscreen eligibility follows host window mode, negotiates refresh timing, and does not double-release the native swapchain on recreation.
- Vulkan result checks run in Release as well as Debug. The previous conditional helper removed Vulkan calls passed as arguments in Release; do not use the `4.9.0-g0v0.1` build.

## Build and release

`dotnet pack src/Veldrid.sln -c Release -warnaserror -p:NoWarn=1591 -p:G0V0PackageVersion=4.9.0-g0v0.2 -o artifacts`

CS1591 is the existing incomplete-public-documentation diagnostic; it is suppressed explicitly for release builds, not all warnings. No tests or GPU probes are invoked. `release.yml` builds and publishes only `g0v0.*` packages to NuGet.org and GitHub Packages, triggered by a `v*-g0v0` / `v*-g0v0.*` tag or a manual version input. Prerelease suffixes are preserved. Configure `NUGET_USER` plus a NuGet trusted-publishing policy for this repository's `release.yml` workflow; GitHub Packages uses the workflow's `GITHUB_TOKEN`. Never use an upstream publishing token or push upstream tags.

## Known limits

This is a prerelease. After the Release result-check fix, Khronos Validation Layers 1.4.363.0 (Core and Synchronization) reported no validation errors or warnings during initialisation and first-run UI rendering with Deferred Vulkan on an NVIDIA GeForce RTX 5060 Laptop GPU. This limited observation does not cover gameplay, shutdown, FSE transitions, other drivers or recording interoperability. Package-only renderer integration was separately built and inspected; no manual DLL overlay is required. Zero drawable extents during recovery produce an explicit error; full suspend/resume and surface/device-loss recovery remain unfinished. Adapter filtering does not retry another GPU after logical-device creation fails. Partial initialisation cleanup still needs a broader ownership audit.
