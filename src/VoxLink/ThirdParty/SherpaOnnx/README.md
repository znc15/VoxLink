# sherpa-onnx and ONNX Runtime notices

VoxLink references the following NuGet packages for optional local anonymous speaker labels:

- `org.k2fsa.sherpa.onnx` `1.13.4`
- `org.k2fsa.sherpa.onnx.runtime.win-x64` `1.13.4`
- Microsoft ONNX Runtime `1.27.0`, included by the sherpa Windows x64 runtime

The sherpa package metadata identifies upstream commit
`142807252687d81b40d6315f23470a1512a00de3` and declares Apache-2.0:

- <https://github.com/k2-fsa/sherpa-onnx/tree/142807252687d81b40d6315f23470a1512a00de3>
- <https://www.nuget.org/packages/org.k2fsa.sherpa.onnx/1.13.4>
- <https://www.nuget.org/packages/org.k2fsa.sherpa.onnx.runtime.win-x64/1.13.4>

The ONNX Runtime license and third-party notices are from the `v1.27.0` upstream tag:

- <https://github.com/microsoft/onnxruntime/tree/v1.27.0>

Imported file SHA-256 values:

- `LICENSE.txt`: `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30`
- `ONNXRUNTIME-LICENSE.txt`: `2f07c72751aed99790b8a4869cf2311df85a860b22ded05fa22803587a48922c`
- `ONNXRUNTIME-THIRD-PARTY-NOTICES.txt`: `0e07b95f3a8d6230037707c5c4a2b554d12c4cb67369669ac255635528ffcee2`

`scripts/publish.ps1` copies this provenance record as
`engine/SHERPA-ONNX-NOTICES.md` and copies the legal files next to the native
runtime. The script verifies every required file before creating the archive.

The CAMPPlus speaker-embedding model is not stored in this directory and is
not bundled in VoxLink releases. `LocalSpeakerLabeler` downloads the pinned
model only when local speaker labels are enabled, then verifies its fixed size
and SHA-256 before loading it. The sherpa model release states that each model
has its own license; the applicable model repository terms remain separate
from the sherpa runtime license.
