# AGENTS.md

Notes for anyone (human or AI agent) changing this repo.

## Commands

```
dotnet build Glance3D.sln -c Release      # warning-free: TreatWarningsAsErrors is on
dotnet test Glance3D.sln -c Release
dotnet publish src/Glance3D.Web -c Release -o out   # browser build; serve out/wwwroot
# See a change without a person at the keyboard: renders offscreen, writes a PNG, exits.
dotnet src/Glance3D/bin/Release/net8.0/glance3d.dll model.glb --capture out.png --view -90,-10
```

## Layout

`src/Glance3D.Core` holds all viewer logic. `src/Glance3D` (desktop, OpenTK) and `src/Glance3D.Web`
(WebAssembly + WebGL2) are thin hosts. A behavior change belongs in Core so both hosts get it.
Hosts only translate input into an `InputFrame` and draw what `RenderPlan` returns. The two GL
draw loops (`ViewerWindow.DrawScene`, `main.js frame()`) mirror each other. Change them together.

## Rules

- **Stay small.** The point of the project is a viewer you can read in an afternoon. Prefer
  doing work once at load (MeshLoader) over per-frame machinery, and think twice before adding
  a dependency. Desktop builds aren't trimmed, because SharpGLTF and OpenTK aren't trim-safe. The web
  build trims everything except the two assemblies it roots.
- **Camera space is X=forward, Y=right, Z=up; world is Z-up.** The projection divides by camera
  `x`. `Frustum`, `Picker.RayThrough` and `Shaders.Vertex` implement the same mapping. Change
  them together. There is one copy of the GLSL (`Shaders.cs`). Keep it inside the common subset of
  GLSL 330 and GLSL ES 300.
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
- Shader sources must stay pure ASCII (`CoreTests.Shaders_ArePureAscii...` checks).
- **WebAssembly host:** `SharpGLTF.Core` and `System.Text.Json` are kept whole on purpose (see
  `Glance3D.Web.csproj`), and bulk data crosses to JS as MemoryViews, which JS copies and disposes
  right away. Check browser changes in headless Edge/Chrome with real input over the DevTools
  protocol. Plain `--screenshot` captures of a WebGL page are unreliable.
