# Glance3D

A tiny, standalone glTF / GLB model viewer. Open a model, spin around it, fly through it, and
close it again. No editor, no scene graph UI, no installer.

**Download:** [spei.dev/glance3d](https://spei.dev/glance3d/) or the
[latest release](https://github.com/speidev/Glance3D/releases/latest).

- **~1,500 lines of C#** on a hand-written OpenGL 3.3 renderer. The only dependencies are OpenTK,
  SharpGLTF and StbImageSharp.
- **Movable camera.** It orbits, pans and zooms around the model, or you can switch to free-fly. Double-click
  any surface to orbit around that point.
- **Reads real-world files:** `.glb` and `.gltf`, node hierarchies, mirrored instances, base
  color textures and factors, vertex colors, alpha mask/blend, double-sided and unlit
  materials. Skinned characters are shown in their bind pose.
- **Pixel-art friendly.** It honors `NEAREST` texture samplers, so palette-textured low-poly models
  stay crisp.
- **Headless thumbnails.** `--capture out.png` renders one frame offscreen and exits.

## Usage

```
glance3d model.glb
```

Or start it with no arguments and drop a `.glb`/`.gltf` onto the window.

| Input | Action |
|---|---|
| Left drag | Orbit (in fly mode: look around) |
| Right / middle drag, or Shift + left drag | Pan |
| Mouse wheel | Zoom (in fly mode: step forward) |
| Double-click | Orbit around the surface under the cursor |
| W A S D, Q / E | Move; down / up. Hold Shift for 4× speed |
| Arrow keys | Turn |
| Tab | Toggle orbit / fly |
| F | Frame the model |
| R | Reset the view |
| Z | Wireframe |
| G | Floor grid |
| F11 | Fullscreen |
| Esc | Quit |

Options:

```
--size WxH         window (or capture) size, default 1280x720
--view YAW,PITCH   initial heading in degrees, default -120,-20
--no-grid          start with the floor grid hidden
--capture out.png  render one frame offscreen, write it, and exit
```

For example, `glance3d chair.glb --capture chair.png --size 1024x1024 --view -90,-10`
writes a square, front-on thumbnail.

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) and a GPU with OpenGL 3.3.

```
dotnet run --project src/Glance3D -- path/to/model.glb
dotnet test
```

A single-file binary:

```
# ~6 MB, needs the .NET 8 runtime installed
dotnet publish src/Glance3D -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true

# ~36 MB, runs anywhere
dotnet publish src/Glance3D -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Swap `win-x64` for `linux-x64` or `osx-arm64`. Pushing a `v*` tag builds all three and
attaches them to a GitHub release (`.github/workflows/release.yml`).

## How it works

| File | Role |
|---|---|
| `MathTypes.cs` | `Point` and `Mat3`. The renderer's own small vector math. |
| `MeshLoader.cs` | glTF to world-space meshes. Bakes node transforms, skinning and the Y-up to Z-up conversion at load. |
| `Camera.cs` | The orbit/fly rig, and `Frustum`, which holds the projection, unprojection and culling. |
| `ViewerWindow.cs` | The GL frame loop, shaders, input, grid and offscreen capture. |
| `Program.cs` | Command-line parsing. |

The renderer works in its own camera convention: **X is forward, Y is right, Z is up**. Its
projection is written by hand to match (clip `w` is camera-space `x`) instead of going through
a look-at/projection matrix pair. glTF's Y-up, right-handed coordinates are converted by swapping Y
and Z. That swap is a reflection, so it also cancels the change of handedness. The tests in
`MeshLoaderTests` pin down that models come out upright, unmirrored and front-face-out.

Not supported (yet): animation playback, morph targets, PBR metal/roughness shading,
KTX2/WebP textures, and texture transforms. Contributions are welcome.

## Origins

Glance3D is the original rendering core of a hobby game engine, taken from the point where it
was still just a renderer and turned into a standalone viewer.

## License

[MIT](LICENSE)
