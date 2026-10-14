namespace Glance3D
{
    // What to draw this frame and with which camera uniforms, decided once in C# so both hosts'
    // GL code is a dumb loop over the result. Draw order: opaque and alpha-masked meshes, then the
    // grid, then alpha-blended meshes sorted back to front (per mesh, not per triangle: good enough
    // for glass panes and foliage cards, not for interpenetrating transparent volumes).
    public static class RenderPlan
    {
        // Floats per vertex in the shared vertex buffer: position 3, uv 2, normal 3, color 4.
        // Attribute locations 0-3 in Shaders.Vertex, at float offsets 0, 3, 5 and 8.
        public const int VertexFloats = 12;

        public static Frustum FrustumFor(Camera cam, Model? model, float aspect)
        {
            var (near, far) = model != null ? cam.ClipRange(model.Center, model.Radius) : (0.01f, 100f);
            return new Frustum(cam.FovY, aspect > 0f ? aspect : 1f, near, far);
        }

        // World -> camera, cam = InvRot * (p - Position), as 16 floats in GL's column-major order:
        // columns 0..2 are InvRot's columns, column 3 is the translation.
        public static float[] ViewMatrix(Camera cam)
        {
            var R = cam.InvRot;
            var t = Mat3.Mul(R, -cam.Position);
            return
            [
                R.m00, R.m10, R.m20, 0f,
                R.m01, R.m11, R.m21, 0f,
                R.m02, R.m12, R.m22, 0f,
                t.x,   t.y,   t.z,   1f,
            ];
        }

        // Indices into model.Meshes that survive frustum culling, split by blending.
        public static void DrawOrder(Model model, Camera cam, Frustum frustum, List<int> opaque, List<int> blended)
        {
            opaque.Clear();
            blended.Clear();
            var depths = new List<(int index, float depth)>();
            for (int i = 0; i < model.Meshes.Length; i++)
            {
                var mesh = model.Meshes[i];
                var c    = Mat3.Mul(cam.InvRot, mesh.BoundCenter - cam.Position);
                if (frustum.Culls(c, mesh.BoundRadius)) continue;
                if (model.Materials[mesh.MaterialIndex].Alpha == AlphaMode.Blend) depths.Add((i, c.x));
                else opaque.Add(i);
            }
            blended.AddRange(depths.OrderByDescending(d => d.depth).Select(d => d.index));
        }

        // Interleaved vertex buffer in the VertexFloats layout.
        public static float[] Interleave(Mesh mesh)
        {
            int n = mesh.Positions.Length / 3;
            var v = new float[n * VertexFloats];
            for (int i = 0; i < n; i++)
            {
                int o = i * VertexFloats;
                Array.Copy(mesh.Positions, i * 3, v, o,     3);
                Array.Copy(mesh.UVs,       i * 2, v, o + 3, 2);
                Array.Copy(mesh.Normals,   i * 3, v, o + 5, 3);
                Array.Copy(mesh.Colors,    i * 4, v, o + 8, 4);
            }
            return v;
        }

        // Each triangle edge once, as a line list. WebGL has no PolygonMode, so the browser's
        // wireframe mode draws these instead. (The desktop host uses PolygonMode(Line), which
        // looks the same.)
        public static uint[] EdgeIndices(Mesh mesh)
        {
            var seen  = new HashSet<ulong>();
            var lines = new List<uint>(mesh.Indices.Length * 2);
            for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    uint a = mesh.Indices[t + k], b = mesh.Indices[t + (k + 1) % 3];
                    ulong key = a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;
                    if (seen.Add(key)) { lines.Add(a); lines.Add(b); }
                }
            return lines.ToArray();
        }

        // A floor grid under the model: lines on the plane of its lowest point, spaced at a power
        // of ten picked so there are roughly 5-50 lines each side whatever the model's scale.
        // Drawn with the mesh shader in unlit, blended mode, with alpha from the vertex color.
        public static Mesh BuildGrid(Model model)
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
    }
}
