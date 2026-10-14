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

    // The desktop host: an OpenTK window and desktop GL 3.3. It translates OpenTK input into an
    // InputFrame for the shared Viewer and draws what RenderPlan decides; everything about how the
    // viewer behaves lives in Glance3D.Core, shared with the browser host (src/Glance3D.Web).
    // Owns every GL object; Model/Mesh/Texture only carry handles.
    internal sealed class ViewerWindow : GameWindow
    {
        private const double DoubleClickSec = 0.35;
        private const int    CaptureSamples = 4;

        private readonly ViewerOptions _opts;
        private readonly Viewer        _viewer = new();
        private readonly List<int>     _opaque = new(), _blended = new();

        private string _status = "drop a .glb or .gltf file here";

        private int _prog, _whiteTex;
        private int _uView, _uHalfTanY, _uHalfTanZ, _uNear, _uFar, _uTex,
                    _uBaseColor, _uAlphaMode, _uAlphaCutoff, _uUnlit;

        private double  _lastClickTime = -1;
        private Vector2 _lastClickPos;

        private Camera Cam => _viewer.Camera;

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
            _opts            = opts;
            _viewer.ShowGrid = opts.ShowGrid;
            VSync            = VSyncMode.On; // a viewer has no reason to spin the GPU at 100%
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

            if (_opts.YawDeg   is float y) Cam.Yaw   = y * MathF.PI / 180f;
            if (_opts.PitchDeg is float p) Cam.Pitch = Math.Clamp(p * MathF.PI / 180f, -Camera.MaxPitch, Camera.MaxPitch);

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
            _viewer.SetModel(model, Aspect);
            foreach (var mesh in model.Meshes) UploadMesh(mesh);
            foreach (var mat in model.Materials)
                if (mat.BaseColorTexture is { GlHandle: 0 } tex)
                    tex.GlHandle = CreateTexture(tex);
            UploadMesh(_viewer.Grid!);

            _status = "";
            UpdateTitle();
        }

        private void UnloadModel()
        {
            if (_viewer.Model is not Model model) return;
            foreach (var mesh in model.Meshes) FreeMesh(mesh);
            FreeMesh(_viewer.Grid);
            foreach (var mat in model.Materials)
                if (mat.BaseColorTexture is { GlHandle: not 0 } tex)
                {
                    GL.DeleteTexture(tex.GlHandle);
                    tex.GlHandle = 0;
                }
        }

        private float Aspect => FramebufferSize.Y > 0 ? (float)FramebufferSize.X / FramebufferSize.Y : 16f / 9f;

        private void UpdateTitle()
        {
            var model = _viewer.Model;
            Title = model == null
                ? $"Glance3D - {_status}"
                : $"Glance3D - {model.Name} - {model.TriangleCount:N0} triangles - {_viewer.ModeName}" +
                  (_status.Length > 0 ? $" - {_status}" : "");
        }

        // ----- input ---------------------------------------------------------

        protected override void OnUpdateFrame(FrameEventArgs e)
        {
            base.OnUpdateFrame(e);
            if (!IsFocused) return;

            var kb = KeyboardState;
            var ms = MouseState;

            if (kb.IsKeyPressed(Keys.Escape)) { Close(); return; }
            if (kb.IsKeyPressed(Keys.F11))
                WindowState = WindowState == WindowState.Fullscreen ? WindowState.Normal : WindowState.Fullscreen;

            var input = new InputFrame
            {
                DragX           = ms.Delta.X,
                DragY           = ms.Delta.Y,
                Left            = ms.IsButtonDown(MouseButton.Left),
                Right           = ms.IsButtonDown(MouseButton.Right),
                Middle          = ms.IsButtonDown(MouseButton.Middle),
                Shift           = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift),
                Wheel           = ms.ScrollDelta.Y,
                ViewportHeight  = ClientSize.Y,
                MoveForward     = Axis(kb, Keys.W, Keys.S),
                MoveRight       = Axis(kb, Keys.D, Keys.A),
                MoveUp          = Axis(kb, Keys.E, Keys.Q),
                TurnYaw         = Axis(kb, Keys.Right, Keys.Left),
                TurnPitch       = Axis(kb, Keys.Up, Keys.Down),
                Frame           = kb.IsKeyPressed(Keys.F),
                Reset           = kb.IsKeyPressed(Keys.R),
                ToggleMode      = kb.IsKeyPressed(Keys.Tab),
                ToggleWireframe = kb.IsKeyPressed(Keys.Z),
                ToggleGrid      = kb.IsKeyPressed(Keys.G),
            };

            // GLFW has no double-click event, so time consecutive presses here.
            if (ms.IsButtonPressed(MouseButton.Left))
            {
                double now = GLFW.GetTime();
                if (now - _lastClickTime < DoubleClickSec && (ms.Position - _lastClickPos).Length < 4f)
                {
                    input.DoubleClick = true;
                    input.ClickNdcX   = ms.Position.X / MathF.Max(1, ClientSize.X) * 2f - 1f;
                    input.ClickNdcY   = 1f - ms.Position.Y / MathF.Max(1, ClientSize.Y) * 2f;
                    _lastClickTime    = -1;
                }
                else
                {
                    _lastClickTime = now;
                    _lastClickPos  = ms.Position;
                }
            }

            _viewer.Update(input, MathF.Min((float)e.Time, 0.1f), Aspect);
            if (input.ToggleMode || input.DoubleClick) UpdateTitle();
        }

        private static float Axis(KeyboardState kb, Keys pos, Keys neg) =>
            (kb.IsKeyDown(pos) ? 1f : 0f) - (kb.IsKeyDown(neg) ? 1f : 0f);

        // ----- rendering -----------------------------------------------------

        protected override void OnRenderFrame(FrameEventArgs e)
        {
            base.OnRenderFrame(e);
            DrawScene(FramebufferSize.X, FramebufferSize.Y);
            SwapBuffers();
        }

        private void DrawScene(int width, int height)
        {
            GL.Viewport(0, 0, width, height);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            if (_viewer.Model is not Model model) return;

            var frustum = RenderPlan.FrustumFor(Cam, model, height > 0 ? (float)width / height : 1f);
            RenderPlan.DrawOrder(model, Cam, frustum, _opaque, _blended);

            GL.UseProgram(_prog);
            GL.UniformMatrix4(_uView, 1, false, RenderPlan.ViewMatrix(Cam));
            GL.Uniform1(_uHalfTanY, frustum.HalfTanY);
            GL.Uniform1(_uHalfTanZ, frustum.HalfTanZ);
            GL.Uniform1(_uNear, frustum.Near);
            GL.Uniform1(_uFar, frustum.Far);
            GL.Uniform1(_uTex, 0);
            GL.ActiveTexture(TextureUnit.Texture0);

            var fill = _viewer.Wireframe ? PolygonMode.Line : PolygonMode.Fill;
            GL.PolygonMode(MaterialFace.FrontAndBack, fill);
            foreach (int i in _opaque) DrawMesh(model, model.Meshes[i]);

            // Grid and blended surfaces read depth but don't write it, so they never hide each
            // other's far side.
            GL.Enable(EnableCap.Blend);
            GL.DepthMask(false);
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            if (_viewer.ShowGrid && _viewer.Grid != null) DrawGrid(_viewer.Grid);
            GL.PolygonMode(MaterialFace.FrontAndBack, fill);
            foreach (int i in _blended) DrawMesh(model, model.Meshes[i]);
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);
        }

        private void DrawMesh(Model model, Mesh mesh)
        {
            var mat = model.Materials[mesh.MaterialIndex];
            if (mat.DoubleSided || _viewer.Wireframe) GL.Disable(EnableCap.CullFace);
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
            _viewer.FrameModel((float)w / h);

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

        // ----- GL resources --------------------------------------------------

        private void BuildShader()
        {
            int vs = Compile(ShaderType.VertexShader, Shaders.Vertex(es: false));
            int fs = Compile(ShaderType.FragmentShader, Shaders.Fragment(es: false));
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
            var interleaved = RenderPlan.Interleave(mesh);

            mesh.Vao = GL.GenVertexArray();
            mesh.Vbo = GL.GenBuffer();
            mesh.Ebo = GL.GenBuffer();

            GL.BindVertexArray(mesh.Vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, mesh.Vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, interleaved.Length * sizeof(float), interleaved, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, mesh.Ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, mesh.Indices.Length * sizeof(uint), mesh.Indices, BufferUsageHint.StaticDraw);

            const int stride = RenderPlan.VertexFloats * sizeof(float);
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
