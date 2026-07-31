# Valve OpenVR bindings

VoxLink vendors the official Valve OpenVR C# binding and Windows x64 native runtime from the `v2.15.6` tag:

- Binding: `headers/openvr_api.cs`
- Native runtime: `bin/win64/openvr_api.dll`
- License: `LICENSE`
- Upstream: <https://github.com/ValveSoftware/openvr/tree/v2.15.6>

The binding has one local source-only change: `#nullable disable` was added before the generated file so it can compile under VoxLink's nullable and warnings-as-errors settings. No OpenVR API behavior was changed.

Upstream SHA-256 values:

- `openvr_api.cs`: `c17e878b7b3b925d1f22ef5382561389c47db8b92019de840705ff5ff28c317a`
- `openvr_api.dll`: `bab8ac6ef64e68a9ca53315b0014d131088584b2efdfa6db511d67ec03cfcb4a`
- `LICENSE`: `f56ff606104d4ef18e617921a75c73ad73b5a1a1d70c69590c29de16919e04ad`
