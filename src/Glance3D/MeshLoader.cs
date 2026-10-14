using System.Numerics;
using SharpGLTF.Schema2;
using StbImageSharp;
using GltfMaterial  = SharpGLTF.Schema2.Material;
using GltfTexture   = SharpGLTF.Schema2.Texture;
using GltfAlphaMode = SharpGLTF.Schema2.AlphaMode;

namespace Glance3D
{
    // Reads a .glb or .gltf into a world-space Model. Everything the renderer would otherwise
    // have to do per frame (node hierarchy, skinning, axis conversion) happens here, once.
    public static class MeshLoader
    {
        public static Model Load(string path) =>
            FromGltf(ModelRoot.Load(path), Path.GetFileName(path));

        internal static Model FromGltf(ModelRoot root, string name)
        {
            var textures  = new Dictionary<GltfTexture, Texture>();
            var materials = root.LogicalMaterials.Select(m => ConvertMaterial(m, textures)).ToList();

            // Primitives without a material use the glTF default (opaque white), appended once at
            // the end so real material indices stay unchanged.
            int defaultMaterial = materials.Count;
            materials.Add(new Material { Name = "(default)" });

            var meshes = new List<Mesh>();
            foreach (var node in SceneNodes(root))
            {
                if (node.Mesh == null) continue;
                foreach (var prim in node.Mesh.Primitives)
                {
                    var mesh = ConvertPrimitive(node, prim, prim.Material?.LogicalIndex ?? defaultMaterial);
                    if (mesh != null) meshes.Add(mesh);
                }
            }

            var model = new Model { Name = name, Meshes = meshes.ToArray(), Materials = materials.ToArray() };
            ComputeBounds(model);
            return model;
        }

        // glTF is Y-up, right-handed. This engine's world is Z-up, and its camera convention
        // (X=forward, Y=right, Z=up, mapped straight onto NDC x/y/depth) makes it left-handed.
        // Swapping Y and Z does both jobs at once: up becomes Z, and the swap is a reflection, so
        // it cancels the handedness change instead of rendering every model mirrored. A plain
        // 90-degree rotation (the obvious "Y-up to Z-up" fix) would stand the model upright but
        // leave it mirrored. MeshLoaderTests.FrontFacingTriangle_StaysCounterClockwiseOnScreen
        // pins this down together with the renderer's FrontFace(Ccw).
        internal static Point ToWorld(Vector3 v) => new(v.X, v.Z, v.Y);

        // Nodes reachable from the default scene, which is what a glTF viewer is meant to show.
        // Files with no scene at all fall back to every node.
        private static IEnumerable<Node> SceneNodes(ModelRoot root)
        {
            var scene = root.DefaultScene ?? root.LogicalScenes.FirstOrDefault();
            if (scene == null) return root.LogicalNodes;

            var result = new List<Node>();
            var stack  = new Stack<Node>(scene.VisualChildren);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                result.Add(n);
                foreach (var child in n.VisualChildren) stack.Push(child);
            }
            return result;
        }

        private static Mesh? ConvertPrimitive(Node node, MeshPrimitive prim, int materialIndex)
        {
            var posAcc = prim.GetVertexAccessor("POSITION");
            if (posAcc == null) return null;

            // Covers TRIANGLES, TRIANGLE_STRIP and TRIANGLE_FAN, indexed or not. Points and lines
            // yield nothing and are skipped.
            var tris = prim.GetTriangleIndices().ToList();
            if (tris.Count == 0) return null;

            var srcPos   = posAcc.AsVector3Array();
            var srcNorm  = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
            var srcUV    = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
            var colorAcc = prim.GetVertexAccessor("COLOR_0");
            var srcColor = colorAcc == null ? null
                         : colorAcc.Dimensions == DimensionType.VEC3
                             ? colorAcc.AsVector3Array().Select(c => new Vector4(c, 1f)).ToList()
                             : (IList<Vector4>)colorAcc.AsVector4Array();

            var skinning = SkinMatrices(node, prim);
            var world    = node.WorldMatrix;
            var normalMx = NormalMatrix(world);

            int n    = srcPos.Count;
            var pos  = new float[n * 3];
            var norm = new float[n * 3];
            var uvs  = new float[n * 2];
            var col  = new float[n * 4];

            for (int i = 0; i < n; i++)
            {
                // Skinned meshes ignore their node's transform (glTF spec 3.7.3.3) and are placed
                // entirely by their joints. We pose them once, at the joints' current (rest)
                // transforms, so a rigged character shows up in its bind pose instead of as a pile
                // of parts at the origin.
                Matrix4x4 m  = skinning?[i] ?? world;
                Matrix4x4 nm = skinning != null ? m : normalMx;

                var p = ToWorld(Vector3.Transform(srcPos[i], m));
                pos[i * 3] = p.x; pos[i * 3 + 1] = p.y; pos[i * 3 + 2] = p.z;

                if (srcNorm != null)
                {
                    var wn = ToWorld(Vector3.Normalize(Vector3.TransformNormal(srcNorm[i], nm)));
                    norm[i * 3] = wn.x; norm[i * 3 + 1] = wn.y; norm[i * 3 + 2] = wn.z;
                }

                if (srcUV != null)
                {
                    uvs[i * 2]     = srcUV[i].X;
                    uvs[i * 2 + 1] = srcUV[i].Y;
                }

                var c = srcColor?[i] ?? Vector4.One;
                col[i * 4] = c.X; col[i * 4 + 1] = c.Y; col[i * 4 + 2] = c.Z; col[i * 4 + 3] = c.W;
            }

            // A node with a negative-determinant transform (a mirrored instance) flips every
            // triangle's winding; undo that so back-face culling still sees the outside.
            bool mirrored = skinning == null && world.GetDeterminant() < 0f;
            var indices = new uint[tris.Count * 3];
            for (int t = 0; t < tris.Count; t++)
            {
                var (a, b, c) = tris[t];
                indices[t * 3]     = (uint)a;
                indices[t * 3 + 1] = (uint)(mirrored ? c : b);
                indices[t * 3 + 2] = (uint)(mirrored ? b : c);
            }

            if (srcNorm == null)
                ComputeFlatNormals(ref pos, ref norm, ref uvs, ref col, ref indices);

            var mesh = new Mesh
            {
                Positions     = pos,
                Normals       = norm,
                UVs           = uvs,
                Colors        = col,
                Indices       = indices,
                MaterialIndex = materialIndex,
            };
            ComputeMeshBounds(mesh);
            return mesh;
        }

        // Per-vertex blended joint matrices, or null when the primitive isn't skinned.
        // System.Numerics is row-vector, so a joint's contribution is v * inverseBind * jointWorld.
        private static Matrix4x4[]? SkinMatrices(Node node, MeshPrimitive prim)
        {
            var skin = node.Skin;
            var jAcc = prim.GetVertexAccessor("JOINTS_0");
            var wAcc = prim.GetVertexAccessor("WEIGHTS_0");
            if (skin == null || jAcc == null || wAcc == null) return null;

            var jointMx = new Matrix4x4[skin.JointsCount];
            for (int j = 0; j < jointMx.Length; j++)
            {
                var (joint, inverseBind) = skin.GetJoint(j);
                jointMx[j] = inverseBind * joint.WorldMatrix;
            }

            var joints  = jAcc.AsVector4Array();
            var weights = wAcc.AsVector4Array();
            var result  = new Matrix4x4[joints.Count];
            for (int i = 0; i < result.Length; i++)
            {
                Vector4 ji = joints[i], wi = weights[i];
                float sum = wi.X + wi.Y + wi.Z + wi.W;
                if (sum <= 1e-6f) { result[i] = Matrix4x4.Identity; continue; }
                wi /= sum; // exporters don't always normalize; unnormalized weights would scale the vertex

                result[i] = jointMx[(int)ji.X] * wi.X + jointMx[(int)ji.Y] * wi.Y
                          + jointMx[(int)ji.Z] * wi.Z + jointMx[(int)ji.W] * wi.W;
            }
            return result;
        }

        // Inverse-transpose, so normals stay perpendicular under non-uniform node scale.
        private static Matrix4x4 NormalMatrix(Matrix4x4 world) =>
            Matrix4x4.Invert(world, out var inv) ? Matrix4x4.Transpose(inv) : world;

        private static Material ConvertMaterial(GltfMaterial src, Dictionary<GltfTexture, Texture> textures)
        {
            var mat = new Material
            {
                Name        = src.Name ?? "",
                Alpha       = src.Alpha switch
                {
                    GltfAlphaMode.MASK  => AlphaMode.Mask,
                    GltfAlphaMode.BLEND => AlphaMode.Blend,
                    _                   => AlphaMode.Opaque,
                },
                AlphaCutoff = src.AlphaCutoff,
                DoubleSided = src.DoubleSided,
                Unlit       = src.Unlit,
            };

            var ch = src.FindChannel("BaseColor");
            if (ch == null) return mat;

            // The factor applies whether or not there's a texture (the texture is multiplied by
            // it). Ignoring it when untextured is why Blender's solid-color materials used to all
            // render white in the original engine.
            var factor = (Vector4)ch.Value.Parameters[0].Value;
            mat.BaseColor = [factor.X, factor.Y, factor.Z, factor.W];

            if (ch.Value.Texture is { } tex)
            {
                if (!textures.TryGetValue(tex, out var converted))
                {
                    converted = DecodeTexture(tex);
                    textures[tex] = converted!;
                }
                mat.BaseColorTexture = converted;
            }
            return mat;
        }

        private static Texture? DecodeTexture(GltfTexture tex)
        {
            var img = tex.PrimaryImage;
            if (img == null) return null;
            try
            {
                using var stream = img.Content.Open();
                var decoded = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                var sampler = tex.Sampler;
                return new Texture
                {
                    RgbaData = decoded.Data,
                    Width    = decoded.Width,
                    Height   = decoded.Height,
                    Nearest  = sampler?.MagFilter == TextureInterpolationFilter.NEAREST,
                    WrapS    = (int)(sampler?.WrapS ?? TextureWrapMode.REPEAT),
                    WrapT    = (int)(sampler?.WrapT ?? TextureWrapMode.REPEAT),
                };
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or IOException)
            {
                // stb decodes PNG/JPEG (and a few others); KTX2/WebP textures land here. The
                // material keeps its base color factor, so the model still reads, just untextured.
                Console.Error.WriteLine($"[glance3d] texture '{img.Name}' could not be decoded ({e.Message}); using base color only");
                return null;
            }
        }

        // Files without NORMAL get faceted shading, which needs each triangle to own its three
        // vertices (a shared vertex can't carry two face normals), so the mesh is un-indexed first.
        private static void ComputeFlatNormals(ref float[] pos, ref float[] norm, ref float[] uvs, ref float[] col, ref uint[] indices)
        {
            int m = indices.Length;
            var p2 = new float[m * 3];
            var n2 = new float[m * 3];
            var u2 = new float[m * 2];
            var c2 = new float[m * 4];
            var i2 = new uint[m];

            for (int k = 0; k < m; k++)
            {
                int v = (int)indices[k];
                Array.Copy(pos, v * 3, p2, k * 3, 3);
                Array.Copy(uvs, v * 2, u2, k * 2, 2);
                Array.Copy(col, v * 4, c2, k * 4, 4);
                i2[k] = (uint)k;
            }

            for (int t = 0; t < m; t += 3)
            {
                var a  = new Point(p2[t * 3],       p2[t * 3 + 1],       p2[t * 3 + 2]);
                var b  = new Point(p2[(t + 1) * 3], p2[(t + 1) * 3 + 1], p2[(t + 1) * 3 + 2]);
                var c  = new Point(p2[(t + 2) * 3], p2[(t + 2) * 3 + 1], p2[(t + 2) * 3 + 2]);
                // Positions are already in the left-handed world, where a front face (CCW on
                // screen) has its outward normal along (c - a) x (b - a).
                var fn = Mat3.Normalize(Mat3.Cross(c - a, b - a));
                for (int k = 0; k < 3; k++)
                {
                    n2[(t + k) * 3] = fn.x; n2[(t + k) * 3 + 1] = fn.y; n2[(t + k) * 3 + 2] = fn.z;
                }
            }

            (pos, norm, uvs, col, indices) = (p2, n2, u2, c2, i2);
        }

        private static void ComputeMeshBounds(Mesh mesh)
        {
            var (min, max) = Aabb(mesh.Positions);
            var center = (min + max) * 0.5f;
            mesh.BoundCenter = center;
            mesh.BoundRadius = MaxDistance(mesh.Positions, center);
        }

        private static void ComputeBounds(Model model)
        {
            if (model.Meshes.Length == 0)
            {
                model.Center = new Point(0, 0, 0);
                model.Radius = 1f;
                return;
            }

            var min = new Point(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = -min;
            foreach (var mesh in model.Meshes)
            {
                var (mn, mx) = Aabb(mesh.Positions);
                min = Point.Min(min, mn);
                max = Point.Max(max, mx);
            }

            model.BoundsMin = min;
            model.BoundsMax = max;
            model.Center    = (min + max) * 0.5f;
            model.Radius    = MathF.Max(1e-4f, model.Meshes.Max(m => MaxDistance(m.Positions, model.Center)));
        }

        private static (Point min, Point max) Aabb(float[] pos)
        {
            var min = new Point(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = -min;
            for (int i = 0; i < pos.Length; i += 3)
            {
                var p = new Point(pos[i], pos[i + 1], pos[i + 2]);
                min = Point.Min(min, p);
                max = Point.Max(max, p);
            }
            return (min, max);
        }

        private static float MaxDistance(float[] pos, Point c)
        {
            float rSq = 0f;
            for (int i = 0; i < pos.Length; i += 3)
            {
                float dx = pos[i] - c.x, dy = pos[i + 1] - c.y, dz = pos[i + 2] - c.z;
                rSq = MathF.Max(rSq, dx * dx + dy * dy + dz * dz);
            }
            return MathF.Sqrt(rSq);
        }
    }
}
