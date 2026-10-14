# AGENTS.md

Notes for anyone (human or AI agent) changing this repo.

## Commands

```
dotnet build Glance3D.sln -c Release      # warning-free: TreatWarningsAsErrors is on
dotnet test Glance3D.sln -c Release
# See a change without a person at the keyboard: renders offscreen, writes a PNG, exits.
dotnet src/Glance3D/bin/Release/net8.0/glance3d.dll model.glb --capture out.png --view -90,-10
```

## Rules

- **Stay small.** The point of the project is a viewer you can read in an afternoon. Prefer
  doing work once at load (MeshLoader) over per-frame machinery, and think twice before adding
  a dependency. Trimming is off on purpose: SharpGLTF and OpenTK aren't trim-safe.
- **Camera space is X=forward, Y=right, Z=up; world is Z-up.** The projection divides by camera
  `x`. `Frustum` (C#) and the vertex shader in `ViewerWindow` implement the same mapping. Change
  them together.
- **glTF -> world is a Y/Z swap (`MeshLoader.ToWorld`), paired with `FrontFace(Ccw)`.** Don't
  "fix" it into a 90-degree rotation: that renders every model mirrored.
  `MeshLoaderTests.ModelIsNotMirrored` will fail if you do.
- **Tests never open a GL context**, so CI runs them headless on all three OSes. Put testable
  logic (math, loading, parsing) outside `ViewerWindow`.
- **Visual changes need a look.** Check them with `--capture` on a few models: textured, skinned,
  pixel-art, transparent. The Khronos glTF-Sample-Assets repo is a good source. Don't commit
  third-party models without their license.
- Comments explain *why*: the constraint, the bug, the rejected alternative. Allman braces,
  `_camelCase` private fields, `var` when the type is apparent.
- Shader sources must stay pure ASCII.
