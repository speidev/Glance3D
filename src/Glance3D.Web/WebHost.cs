using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("browser")]

namespace Glance3D
{
    // The browser host's C# half: a thin [JSExport] surface over Glance3D.Core. main.js owns the
    // WebGL2 context, DOM events and the frame loop; everything that decides *what* the viewer
    // does (loading, camera, input mapping, culling, draw order, picking, shader source) runs here,
    // the same code the desktop app runs.
    //
    // Bulk data (vertex/index/texture bytes) crosses as MemoryViews over pinned managed arrays.
    // JS must copy each view (view.slice()) and dispose it before the next call. Small per-frame
    // data crosses as double[].
    public static partial class WebHost
    {
        private static readonly Viewer        _viewer   = new();
        private static readonly List<int>     _opaque   = new(), _blended = new();
        private static readonly List<Texture> _textures = new();

        // Keeps the most recent bulk array alive while its MemoryView is being copied.
        private static byte[] _scratch = [];

        public static void Main() { } // the runtime needs an entry point; main.js drives everything

        [JSExport]
        public static string Shader(bool vertex) => vertex ? Shaders.Vertex(es: true) : Shaders.Fragment(es: true);

        // Returns "" on success, or a message to show the user.
        [JSExport]
        public static string Load(byte[] data, string name, double aspect)
        {
            Model model;
            try
            {
                model = MeshLoader.Load(data, name);
            }
#pragma warning disable CA1031 // any failure to parse a user-supplied file is reported, never fatal
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return $"could not open {name}: {ex.Message}";
            }

            _viewer.SetModel(model, (float)aspect);
            _textures.Clear();
            foreach (var mat in model.Materials)
                if (mat.BaseColorTexture is { } t && !_textures.Contains(t)) _textures.Add(t);
            return "";
        }

        [JSExport]
        public static string Status()
        {
            var m = _viewer.Model;
            return m == null ? "" : $"{m.Name} · {m.TriangleCount:N0} triangles · {_viewer.ModeName}";
        }

        [JSExport] public static int MeshCount()    => _viewer.Model?.Meshes.Length ?? 0;
        [JSExport] public static int TextureCount() => _textures.Count;

        // Mesh -1 is the floor grid.
        private static Mesh MeshAt(int i) => i < 0 ? _viewer.Grid! : _viewer.Model!.Meshes[i];

        [JSExport]
        [return: JSMarshalAs<JSType.MemoryView>]
        public static ArraySegment<byte> MeshVertices(int i) => Bytes(RenderPlan.Interleave(MeshAt(i)));

        [JSExport]
        [return: JSMarshalAs<JSType.MemoryView>]
        public static ArraySegment<byte> MeshIndices(int i) => Bytes(MeshAt(i).Indices);

        [JSExport]
        [return: JSMarshalAs<JSType.MemoryView>]
        public static ArraySegment<byte> MeshEdges(int i) => Bytes(RenderPlan.EdgeIndices(MeshAt(i)));

        // [r, g, b, a, alphaMode, alphaCutoff, doubleSided, unlit, textureIndex or -1]
        [JSExport]
        public static double[] MeshMaterial(int i)
        {
            var m = _viewer.Model!.Materials[MeshAt(i).MaterialIndex];
            int tex = m.BaseColorTexture is { } t ? _textures.IndexOf(t) : -1;
            return [m.BaseColor[0], m.BaseColor[1], m.BaseColor[2], m.BaseColor[3],
                    (int)m.Alpha, m.AlphaCutoff, m.DoubleSided ? 1 : 0, m.Unlit ? 1 : 0, tex];
        }

        // [width, height, nearest, wrapS, wrapT]
        [JSExport]
        public static double[] TextureInfo(int t)
        {
            var tex = _textures[t];
            return [tex.Width, tex.Height, tex.Nearest ? 1 : 0, tex.WrapS, tex.WrapT];
        }

        [JSExport]
        [return: JSMarshalAs<JSType.MemoryView>]
        public static ArraySegment<byte> TexturePixels(int t) => new(_scratch = _textures[t].RgbaData);

        // Input layout (keep in sync with main.js, Input): see InputFrame for meanings.
        //   0 dragX  1 dragY  2 left  3 right  4 middle  5 shift  6 wheel  7 viewportHeight
        //   8 moveForward  9 moveRight  10 moveUp  11 turnYaw  12 turnPitch
        //   13 frame  14 reset  15 toggleMode  16 toggleWireframe  17 toggleGrid
        //   18 doubleClick  19 clickNdcX  20 clickNdcY
        //
        // Returns: [0..15 view matrix (column-major), 16 halfTanY, 17 halfTanZ, 18 near, 19 far,
        //           20 wireframe, 21 showGrid, 22 opaqueCount, 23 blendedCount, then the opaque
        //           mesh indices followed by the blended ones (back to front)].
        [JSExport]
        public static double[] Frame(double[] input, double dt, double aspect)
        {
            static bool B(double v) => v != 0;
            var f = new InputFrame
            {
                DragX = (float)input[0], DragY = (float)input[1],
                Left = B(input[2]), Right = B(input[3]), Middle = B(input[4]), Shift = B(input[5]),
                Wheel = (float)input[6], ViewportHeight = (float)input[7],
                MoveForward = (float)input[8], MoveRight = (float)input[9], MoveUp = (float)input[10],
                TurnYaw = (float)input[11], TurnPitch = (float)input[12],
                Frame = B(input[13]), Reset = B(input[14]), ToggleMode = B(input[15]),
                ToggleWireframe = B(input[16]), ToggleGrid = B(input[17]),
                DoubleClick = B(input[18]), ClickNdcX = (float)input[19], ClickNdcY = (float)input[20],
            };
            _viewer.Update(f, (float)Math.Min(dt, 0.1), (float)aspect);

            var model = _viewer.Model;
            if (model == null) return [];

            var cam     = _viewer.Camera;
            var frustum = RenderPlan.FrustumFor(cam, model, (float)aspect);
            RenderPlan.DrawOrder(model, cam, frustum, _opaque, _blended);

            var view = RenderPlan.ViewMatrix(cam);
            var outp = new double[24 + _opaque.Count + _blended.Count];
            for (int k = 0; k < 16; k++) outp[k] = view[k];
            outp[16] = frustum.HalfTanY; outp[17] = frustum.HalfTanZ;
            outp[18] = frustum.Near;     outp[19] = frustum.Far;
            outp[20] = _viewer.Wireframe ? 1 : 0;
            outp[21] = _viewer.ShowGrid ? 1 : 0;
            outp[22] = _opaque.Count;
            outp[23] = _blended.Count;
            int o = 24;
            foreach (int i in _opaque)  outp[o++] = i;
            foreach (int i in _blended) outp[o++] = i;
            return outp;
        }

        private static ArraySegment<byte> Bytes<T>(T[] data) where T : struct =>
            new(_scratch = MemoryMarshal.AsBytes(data.AsSpan()).ToArray());
    }
}
