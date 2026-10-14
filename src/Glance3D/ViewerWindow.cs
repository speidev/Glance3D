using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Glance3D
{
    public sealed class ViewerOptions
    {
        public string? ModelPath;
        public int     Width  = 1280;
        public int     Height = 720;
        public float?  YawDeg, PitchDeg;   // initial heading; defaults to Camera.DefaultYaw/Pitch
        public string? CapturePath;        // render one frame offscreen to this PNG and exit
        public bool    ShowGrid = true;
    }

    // The GL frame loop plus input. Owns every GL object; Model/Mesh/Material only carry handles.
    //
    // Frame: opaque + alpha-masked meshes -> reference grid -> alpha-blended meshes (sorted back to
    // front, depth writes off). All meshes are world space, so the only transform is the camera's.
    internal sealed class ViewerWindow : GameWindow
    {
        private const float OrbitSensitivity = 0.006f;  // rad per pixel of drag
        private const float TurnSpeed        = 1.8f;    // rad/s for the arrow keys
        private const double DoubleClickSec  = 0.35;
        private const int   CaptureSamples   = 4;

        private readonly ViewerOptions _opts;
        private readonly Camera        _cam = new();

        private Model? _model;
        private Mesh?  _grid;
        private string _status = "drop a .glb or .gltf file here";

        private int _prog, _whiteTex;
        private int _uView, _uHalfTanY, _uHalfTanZ, _uNear, _uFar, _uTex,
                    _uBaseColor, _uAlphaMode, _uAlphaCutoff, _uUnlit;

        private bool   _wireframe, _showGrid;
        private double _lastClickTime = -1;
        private Vector2 _lastClickPos;
        private Vector2? _pendingPick;     // a double-click waiting for this frame's depth buffer

        public ViewerWindow(ViewerOptions opts)
            : base(new GameWindowSettings { UpdateFrequency = 0 },
                   new NativeWindowSettings
                   {
                       ClientSize       = new Vector2i(opts.Width, opts.Height),
                       Title            = "Glance3D",
                       APIVersion       = new Version(3, 3),
                       Profile          = ContextProfile.Core,
                       Flags            = ContextFlags.ForwardCompatible,
                       NumberOfSamples  = 4,
                       StartVisible     = opts.CapturePath == null,
                   })
        {
            _opts     = opts;
            _showGrid = opts.ShowGrid;
            VSync     = VSyncMode.On; // a viewer has no reason to spin the GPU at 100%
        }

        // ----- lifecycle -----------------------------------------------------

        protected override void OnLoad()
        {
            base.OnLoad();
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Multisample);
            GL.CullFace(CullFaceMode.Back);
            // Counter-clockwise, the glTF convention. That only holds because MeshLoader.ToWorld
            // swaps Y/Z (a reflection) rather than rotating; the original engine read raw glTF
            // coordinates, rendered every model mirrored, and needed FrontFace(Cw) to compensate.
            GL.FrontFace(FrontFaceDirection.Ccw);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.ClearColor(0.12f, 0.13f, 0.15f, 1f);

            BuildShader();
            _whiteTex = CreateTexture(new Texture { RgbaData = [255, 255, 255, 255], Width = 1, Height = 1 });

            if (_opts.YawDeg   is float y) _cam.Yaw   = y * MathF.PI / 180f;
            if (_opts.PitchDeg is float p) _cam.Pitch = Math.Clamp(p * MathF.PI / 180f, -Camera.MaxPitch, Camera.MaxPitch);

            if (_opts.ModelPath != null) LoadModel(_opts.ModelPath);
            else UpdateTitle();

            if (_opts.CapturePath != null)
            {
                Capture(_opts.CapturePath);
                Close();
            }
        }

        protected override void OnUnload()
        {
            UnloadModel();
            FreeMesh(_grid);
            GL.DeleteTexture(_whiteTex);
            GL.DeleteProgram(_prog);
            base.OnUnload();
        }

        protected override void OnFileDrop(FileDropEventArgs e)
        {
            base.OnFileDrop(e);
            var path = e.FileNames.FirstOrDefault(f =>
                f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase));
            if (path != null) LoadModel(path);
            else { _status = "only .glb and .gltf files can be opened"; UpdateTitle(); }
        }

        protected override void OnFramebufferResize(FramebufferResizeEventArgs e)
        {
            base.OnFramebufferResize(e);
            GL.Viewport(0, 0, e.Width, e.Height);
        }

        private void LoadModel(string path)
        {
            Model model;
            try
            {
                model = MeshLoader.Load(path);
            }
#pragma warning disable CA1031 // any failure to parse a user-supplied file is reported, never fatal
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Console.Error.WriteLine($"[glance3d] failed to load {path}: {ex.Message}");
                _status = $"could not open {Path.GetFileName(path)}: {ex.Message}";
                UpdateTitle();
                return;
            }

            UnloadModel();
            _model = model;
            foreach (var mesh in model.Meshes) UploadMesh(mesh);
            foreach (var mat in model.Materials)
                if (mat.BaseColorTexture is { GlHandle: 0 } tex)
                    tex.GlHandle = CreateTexture(tex);

            FreeMesh(_grid);
            _grid = BuildGrid(model);
            UploadMesh(_grid);

            FrameModel();
            _status = "";
            UpdateTitle();
        }

        private void UnloadModel()
        {
            if (_model == null) return;
            foreach (var mesh in _model.Meshes) FreeMesh(mesh);
            foreach (var mat in _model.Materials)
                if (mat.BaseColorTexture is { GlHandle: not 0 } tex)
                {
                    GL.DeleteTexture(tex.GlHandle);
                    tex.GlHandle = 0;
                }
            _model = null;
        }

        private void FrameModel()
        {
            if (_model == null) return;
            _cam.Frame(_model.Center, _model.Radius, Aspect);
        }

        private float Aspect => FramebufferSize.Y > 0 ? (float)FramebufferSize.X / FramebufferSize.Y : 16f / 9f;

        private void UpdateTitle()
        {
            string mode = _cam.Mode == CameraMode.Orbit ? "orbit" : "fly";
            Title = _model == null
                ? $"Glance3D - {_status}"
                : $"Glance3D - {_model.Name} - {_model.TriangleCount:N0} triangles - {mode}" +
                  (_status.Length > 0 ? $" - {_status}" : "");
        }

        // ----- input ---------------------------------------------------------

        protected override void OnUpdateFrame(FrameEventArgs e)
        {
            base.OnUpdateFrame(e);
            if (!IsFocused) return;

            float dt = MathF.Min((float)e.Time, 0.1f);
            var   kb = KeyboardState;
            var   ms = MouseState;

            if (kb.IsKeyPressed(Keys.Escape)) { Close(); return; }
            if (kb.IsKeyPressed(Keys.F11))
                WindowState = WindowState == WindowState.Fullscreen ? WindowState.Normal : WindowState.Fullscreen;
            if (kb.IsKeyPressed(Keys.F)) FrameModel();
            if (kb.IsKeyPressed(Keys.R)) { _cam.ResetHeading(); FrameModel(); }
            if (kb.IsKeyPressed(Keys.Z)) _wireframe = !_wireframe;
            if (kb.IsKeyPressed(Keys.G)) _showGrid  = !_showGrid;
            if (kb.IsKeyPressed(Keys.Tab)) { _cam.ToggleMode(); UpdateTitle(); }

            // Mouse: left drag turns (orbit / look), right or middle drag pans, Shift+left pans too
            // for trackpads. Double-click re-centers the orbit on the surface under the cursor.
            bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
            var  d     = ms.Delta;
            bool panning = ms.IsButtonDown(MouseButton.Right) || ms.IsButtonDown(MouseButton.Middle)
                        || (shift && ms.IsButtonDown(MouseButton.Left));

            if (panning && d != Vector2.Zero)
                _cam.Pan(d.X, d.Y, ClientSize.Y);
            else if (ms.IsButtonDown(MouseButton.Left) && d != Vector2.Zero)
                _cam.Turn(d.X * OrbitSensitivity, -d.Y * OrbitSensitivity);

            if (ms.ScrollDelta.Y != 0f) _cam.Dolly(ms.ScrollDelta.Y);

            if (ms.IsButtonPressed(MouseButton.Left))
            {
                double now = GLFW.GetTime();
                if (now - _lastClickTime < DoubleClickSec && (ms.Position - _lastClickPos).Length < 4f)
                {
                    _pendingPick   = ms.Position;
                    _lastClickTime = -1;
                }
                else
                {
                    _lastClickTime = now;
                    _lastClickPos  = ms.Position;
                }
            }

            // Keyboard: WASD + Q/E (down/up), Shift for 4x. Speed scales with the model, so a
            // building and a ring both cross the screen in about the same time.
            float speed = (_model?.Radius ?? 1f) * (shift ? 4f : 1f) * dt;
            float f = Axis(kb, Keys.W, Keys.S), r = Axis(kb, Keys.D, Keys.A), u = Axis(kb, Keys.E, Keys.Q);
            if (f != 0 || r != 0 || u != 0) _cam.Move(f * speed, r * speed, u * speed);

            float yaw = Axis(kb, Keys.Right, Keys.Left), pitch = Axis(kb, Keys.Up, Keys.Down);
            if (yaw != 0 || pitch != 0) _cam.Turn(yaw * TurnSpeed * dt, pitch * TurnSpeed * dt);
        }

        private static float Axis(KeyboardState kb, Keys pos, Keys neg) =>
            (kb.IsKeyDown(pos) ? 1f : 0f) - (kb.IsKeyDown(neg) ? 1f : 0f);

        // ----- rendering -----------------------------------------------------

        protected override void OnRenderFrame(FrameEventArgs e)
        {
            base.OnRenderFrame(e);
            var frustum = DrawScene(FramebufferSize.X, FramebufferSize.Y);

            if (_pendingPick is Vector2 click)
            {
                _pendingPick = null;
                PickPivot(click, frustum);
            }

            SwapBuffers();
        }

        private Frustum DrawScene(int width, int height)
        {
            GL.Viewport(0, 0, width, height);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            var (near, far) = _model != null ? _cam.ClipRange(_model.Center, _model.Radius) : (0.01f, 100f);
            var frustum     = new Frustum(_cam.FovY, height > 0 ? (float)width / height : 1f, near, far);
            if (_model == null) return frustum;

            GL.UseProgram(_prog);
            var view = ViewMatrix();
            GL.UniformMatrix4(_uView, false, ref view);
            GL.Uniform1(_uHalfTanY, frustum.HalfTanY);
            GL.Uniform1(_uHalfTanZ, frustum.HalfTanZ);
            GL.Uniform1(_uNear, frustum.Near);
            GL.Uniform1(_uFar, frustum.Far);
            GL.Uniform1(_uTex, 0);
            GL.ActiveTexture(TextureUnit.Texture0);

            GL.PolygonMode(MaterialFace.FrontAndBack, _wireframe ? PolygonMode.Line : PolygonMode.Fill);

            var blended = new List<(Mesh mesh, float depth)>();
            foreach (var mesh in _model.Meshes)
            {
                var c = ToCamera(mesh.BoundCenter);
                if (frustum.Culls(c, mesh.BoundRadius)) continue;
                if (MaterialOf(mesh).Alpha == AlphaMode.Blend) blended.Add((mesh, c.x));
                else DrawMesh(mesh);
            }

            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);

            // Grid and blended surfaces read depth but don't write it, so they never hide each
            // other's far side. Blended meshes are sorted per mesh, not per triangle: good enough
            // for glass panes and foliage cards, not for interpenetrating transparent volumes.
            GL.Enable(EnableCap.Blend);
            GL.DepthMask(false);
            if (_showGrid && _grid != null) DrawGrid(_grid);
            GL.PolygonMode(MaterialFace.FrontAndBack, _wireframe ? PolygonMode.Line : PolygonMode.Fill);
            foreach (var (mesh, _) in blended.OrderByDescending(b => b.depth)) DrawMesh(mesh);
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);

            return frustum;
        }

        private Material MaterialOf(Mesh mesh) => _model!.Materials[mesh.MaterialIndex];

        private void DrawMesh(Mesh mesh)
        {
            var mat = MaterialOf(mesh);
            if (mat.DoubleSided || _wireframe) GL.Disable(EnableCap.CullFace);
            else GL.Enable(EnableCap.CullFace);

            GL.BindTexture(TextureTarget.Texture2D, mat.BaseColorTexture is { GlHandle: not 0 } t ? t.GlHandle : _whiteTex);
            GL.Uniform4(_uBaseColor, mat.BaseColor[0], mat.BaseColor[1], mat.BaseColor[2], mat.BaseColor[3]);
            GL.Uniform1(_uAlphaMode, (int)mat.Alpha);
            GL.Uniform1(_uAlphaCutoff, mat.AlphaCutoff);
            GL.Uniform1(_uUnlit, mat.Unlit ? 1 : 0);

            GL.BindVertexArray(mesh.Vao);
            GL.DrawElements(PrimitiveType.Triangles, mesh.Indices.Length, DrawElementsType.UnsignedInt, 0);
        }

        private void DrawGrid(Mesh grid)
        {
            GL.Disable(EnableCap.CullFace);
            GL.BindTexture(TextureTarget.Texture2D, _whiteTex);
            GL.Uniform4(_uBaseColor, 1f, 1f, 1f, 1f);
            GL.Uniform1(_uAlphaMode, (int)AlphaMode.Blend);
            GL.Uniform1(_uUnlit, 1);
            GL.BindVertexArray(grid.Vao);
            GL.DrawElements(PrimitiveType.Lines, grid.Indices.Length, DrawElementsType.UnsignedInt, 0);
        }

        private Point ToCamera(Point world) => Mat3.Mul(_cam.InvRot, world - _cam.Position);

        // World -> camera as a GL matrix: cam = InvRot * (p - Position).
        // OpenTK's Matrix4 is row-major and uploaded with transpose=false, so OpenTK row i becomes
        // GLSL column i. Columns 0..2 are InvRot's columns, column 3 the translation.
        private Matrix4 ViewMatrix()
        {
            var R = _cam.InvRot;
            var t = Mat3.Mul(R, -_cam.Position);
            return new Matrix4(
                R.m00, R.m10, R.m20, 0f,
                R.m01, R.m11, R.m21, 0f,
                R.m02, R.m12, R.m22, 0f,
                t.x,   t.y,   t.z,   1f);
        }

        // Read the depth under a double-click and orbit around that surface point from now on.
        // Must run after drawing and before SwapBuffers: the back buffer is undefined after a swap.
        private void PickPivot(Vector2 clientPos, Frustum frustum)
        {
            var fb = FramebufferSize;
            int px = (int)(clientPos.X * fb.X / MathF.Max(1, ClientSize.X));
            int py = fb.Y - 1 - (int)(clientPos.Y * fb.Y / MathF.Max(1, ClientSize.Y));
            if (px < 0 || py < 0 || px >= fb.X || py >= fb.Y) return;

            float depth = 1f;
            GL.ReadPixels(px, py, 1, 1, PixelFormat.DepthComponent, PixelType.Float, ref depth);
            if (depth >= 1f) return; // background: keep the current pivot

            float ndcX = (px + 0.5f) / fb.X * 2f - 1f;
            float ndcY = (py + 0.5f) / fb.Y * 2f - 1f;
            var cam = frustum.Unproject(ndcX, ndcY, depth);
            _cam.OrbitAround(_cam.Position + Mat3.Mul(_cam.Rot, cam));
            UpdateTitle();
        }

        // ----- offscreen capture ---------------------------------------------

        // Renders one frame into a multisampled FBO, resolves it, and writes a PNG. Doesn't use the
        // default framebuffer because the window is hidden in capture mode, and a hidden window's
        // pixels fail the ownership test on some drivers.
        private void Capture(string path)
        {
            int w = _opts.Width, h = _opts.Height;

            int msFbo = GL.GenFramebuffer(), msColor = GL.GenRenderbuffer(), msDepth = GL.GenRenderbuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, msFbo);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msColor);
            GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, CaptureSamples, RenderbufferStorage.Rgba8, w, h);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, msColor);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msDepth);
            GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, CaptureSamples, RenderbufferStorage.DepthComponent24, w, h);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, msDepth);

            int fbo = GL.GenFramebuffer(), color = GL.GenRenderbuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, color);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, w, h);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, color);

            // Frame for the capture's aspect, not the hidden window's.
            if (_model != null) _cam.Frame(_model.Center, _model.Radius, (float)w / h);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, msFbo);
            DrawScene(w, h);

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, msFbo);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
            GL.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);

            var pixels = new byte[w * h * 4];
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadPixels(0, 0, w, h, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            GL.DeleteFramebuffer(msFbo); GL.DeleteFramebuffer(fbo);
            GL.DeleteRenderbuffer(msColor); GL.DeleteRenderbuffer(msDepth); GL.DeleteRenderbuffer(color);

            PngWriter.Write(path, w, h, pixels, flipVertically: true);
            Console.WriteLine($"[glance3d] wrote {path}");
        }

        // ----- grid ----------------------------------------------------------

        // A floor grid under the model: lines on the plane of its lowest point, spaced at a power
        // of ten picked so there are roughly 5-50 lines each side whatever the model's scale.
        // Drawn with the mesh shader in unlit mode, alpha from the vertex color.
        private static Mesh BuildGrid(Model model)
        {
            float step  = MathF.Pow(10f, MathF.Floor(MathF.Log10(model.Radius * 0.5f)));
            int   lines = (int)MathF.Ceiling(model.Radius * 1.5f / step);
            float half  = lines * step;
            float cx    = MathF.Round(model.Center.x / step) * step;
            float cy    = MathF.Round(model.Center.y / step) * step;
            float z     = model.BoundsMin.z;

            var pos = new List<float>();
            var col = new List<float>();
            void Line(float x0, float y0, float x1, float y1, float a)
            {
                pos.AddRange([x0, y0, z, x1, y1, z]);
                for (int k = 0; k < 2; k++) col.AddRange([0.75f, 0.78f, 0.85f, a]);
            }

            for (int i = -lines; i <= lines; i++)
            {
                float a = i == 0 ? 0.45f : i % 10 == 0 ? 0.3f : 0.14f;
                Line(cx + i * step, cy - half, cx + i * step, cy + half, a);
                Line(cx - half, cy + i * step, cx + half, cy + i * step, a);
            }

            int n = pos.Count / 3;
            return new Mesh
            {
                Positions = pos.ToArray(),
                Colors    = col.ToArray(),
                Normals   = new float[n * 3],
                UVs       = new float[n * 2],
                Indices   = Enumerable.Range(0, n).Select(i => (uint)i).ToArray(),
            };
        }

        // ----- GL resources --------------------------------------------------

        private void BuildShader()
        {
            // Camera space is X=forward, Y=right, Z=up, so clip w is the camera-space x (depth) and
            // depth is the standard GL perspective mapping applied to x. Keep in sync with
            // Frustum.ProjectNdc / Unproject.
            const string vert = """
                #version 330 core
                layout(location=0) in vec3 aPos;
                layout(location=1) in vec2 aUV;
                layout(location=2) in vec3 aNormal;
                layout(location=3) in vec4 aColor;
                uniform mat4  uView;
                uniform float uHalfTanY;
                uniform float uHalfTanZ;
                uniform float uNear;
                uniform float uFar;
                out vec2 vUV;
                out vec3 vNormal;
                out vec4 vColor;
                void main() {
                    vec3 cam = (uView * vec4(aPos, 1.0)).xyz;
                    float ndcZ = (uFar + uNear) / (uFar - uNear) * cam.x
                               - 2.0 * uFar * uNear / (uFar - uNear);
                    gl_Position = vec4(cam.y / uHalfTanY, cam.z / uHalfTanZ, ndcZ, cam.x);
                    vUV     = aUV;
                    vNormal = mat3(uView) * aNormal;
                    vColor  = aColor;
                }
                """;

            // A key light from over the viewer's left shoulder plus a sky/ground fill, both fixed
            // to the camera so the side you're looking at is always lit. That's the point of a
            // viewer: inspecting shape, not staging a scene. glTF UVs have their origin at the
            // image's top-left, which is also row 0 of the decoded image, so V is used as-is.
            const string frag = """
                #version 330 core
                in vec2 vUV;
                in vec3 vNormal;
                in vec4 vColor;
                uniform sampler2D uTex;
                uniform vec4  uBaseColor;
                uniform int   uAlphaMode;
                uniform float uAlphaCutoff;
                uniform int   uUnlit;
                out vec4 fragColor;
                void main() {
                    vec4 c = texture(uTex, vUV) * uBaseColor * vColor;
                    if (uAlphaMode == 1 && c.a < uAlphaCutoff) discard;
                    if (uAlphaMode != 2) c.a = 1.0;
                    if (uUnlit == 0) {
                        vec3 n = normalize(vNormal);
                        if (!gl_FrontFacing) n = -n;
                        vec3  keyDir = normalize(vec3(-1.0, -0.45, 0.75));
                        float key    = max(dot(n, keyDir), 0.0);
                        float sky    = 0.5 + 0.5 * n.z;
                        c.rgb *= 0.22 + 0.18 * sky + 0.75 * key;
                    }
                    fragColor = c;
                }
                """;

            int vs = Compile(ShaderType.VertexShader, vert);
            int fs = Compile(ShaderType.FragmentShader, frag);
            _prog = GL.CreateProgram();
            GL.AttachShader(_prog, vs);
            GL.AttachShader(_prog, fs);
            GL.LinkProgram(_prog);
            GL.GetProgram(_prog, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(_prog));
            GL.DetachShader(_prog, vs); GL.DeleteShader(vs);
            GL.DetachShader(_prog, fs); GL.DeleteShader(fs);

            _uView        = GL.GetUniformLocation(_prog, "uView");
            _uHalfTanY    = GL.GetUniformLocation(_prog, "uHalfTanY");
            _uHalfTanZ    = GL.GetUniformLocation(_prog, "uHalfTanZ");
            _uNear        = GL.GetUniformLocation(_prog, "uNear");
            _uFar         = GL.GetUniformLocation(_prog, "uFar");
            _uTex         = GL.GetUniformLocation(_prog, "uTex");
            _uBaseColor   = GL.GetUniformLocation(_prog, "uBaseColor");
            _uAlphaMode   = GL.GetUniformLocation(_prog, "uAlphaMode");
            _uAlphaCutoff = GL.GetUniformLocation(_prog, "uAlphaCutoff");
            _uUnlit       = GL.GetUniformLocation(_prog, "uUnlit");
        }

        private static int Compile(ShaderType type, string src)
        {
            int sh = GL.CreateShader(type);
            GL.ShaderSource(sh, src);
            GL.CompileShader(sh);
            GL.GetShader(sh, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(sh));
            return sh;
        }

        private static void UploadMesh(Mesh mesh)
        {
            const int floats = 12; // pos 3, uv 2, normal 3, color 4
            int n = mesh.Positions.Length / 3;
            var interleaved = new float[n * floats];
            for (int i = 0; i < n; i++)
            {
                int o = i * floats;
                Array.Copy(mesh.Positions, i * 3, interleaved, o,     3);
                Array.Copy(mesh.UVs,       i * 2, interleaved, o + 3, 2);
                Array.Copy(mesh.Normals,   i * 3, interleaved, o + 5, 3);
                Array.Copy(mesh.Colors,    i * 4, interleaved, o + 8, 4);
            }

            mesh.Vao = GL.GenVertexArray();
            mesh.Vbo = GL.GenBuffer();
            mesh.Ebo = GL.GenBuffer();

            GL.BindVertexArray(mesh.Vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, mesh.Vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, interleaved.Length * sizeof(float), interleaved, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, mesh.Ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, mesh.Indices.Length * sizeof(uint), mesh.Indices, BufferUsageHint.StaticDraw);

            const int stride = floats * sizeof(float);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, 5 * sizeof(float));
            GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
            for (int a = 0; a < 4; a++) GL.EnableVertexAttribArray(a);
            GL.BindVertexArray(0);
        }

        private static void FreeMesh(Mesh? mesh)
        {
            if (mesh == null || mesh.Vao == 0) return;
            GL.DeleteVertexArray(mesh.Vao);
            GL.DeleteBuffer(mesh.Vbo);
            GL.DeleteBuffer(mesh.Ebo);
            mesh.Vao = mesh.Vbo = mesh.Ebo = 0;
        }

        private static int CreateTexture(Texture tex)
        {
            int handle = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, handle);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                tex.Width, tex.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, tex.RgbaData);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

            // Pixel-art and palette-texture models ask for NEAREST in their sampler; honoring it keeps
            // them crisp instead of smearing every palette cell into its neighbors.
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)(tex.Nearest ? TextureMinFilter.NearestMipmapLinear : TextureMinFilter.LinearMipmapLinear));
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)(tex.Nearest ? TextureMagFilter.Nearest : TextureMagFilter.Linear));
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, tex.WrapS == 0 ? (int)TextureWrapMode.Repeat : tex.WrapS);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, tex.WrapT == 0 ? (int)TextureWrapMode.Repeat : tex.WrapT);
            return handle;
        }
    }
}
