using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Xunit;

namespace Glance3D.Tests
{
    // The pieces both hosts share: picking, the input-to-camera mapping, the draw plan and the
    // shader source. If these pass, desktop and browser behave the same.
    public class CoreTests
    {
        // A 2x2 quad in glTF's XY plane at z=0, i.e. world XZ at y=0, facing world +Y.
        private static Model Quad(bool blended = false)
        {
            var material = new MaterialBuilder("m").WithBaseColor(Vector4.One);
            if (blended) material = material.WithAlpha(SharpGLTF.Materials.AlphaMode.BLEND);
            var mesh = new MeshBuilder<VertexPosition>("quad");
            var prim = mesh.UsePrimitive(material);
            prim.AddTriangle(new VertexPosition(-1, -1, 0), new VertexPosition(1, -1, 0), new VertexPosition(1, 1, 0));
            prim.AddTriangle(new VertexPosition(-1, -1, 0), new VertexPosition(1, 1, 0), new VertexPosition(-1, 1, 0));
            var scene = new SceneBuilder();
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
            return MeshLoader.FromGltf(scene.ToGltf2(), "quad");
        }

        private static byte[] QuadGlb()
        {
            var material = new MaterialBuilder("m").WithBaseColor(Vector4.One);
            var mesh = new MeshBuilder<VertexPosition>("tri");
            mesh.UsePrimitive(material).AddTriangle(new VertexPosition(0, 0, 0), new VertexPosition(1, 0, 0), new VertexPosition(0, 1, 0));
            var scene = new SceneBuilder();
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
            using var ms = new MemoryStream();
            scene.ToGltf2().WriteGLB(ms);
            return ms.ToArray();
        }

        [Fact]
        public void Raycast_HitsTheNearestSurface()
        {
            var model = Quad();
            var hit   = Picker.Raycast(model, new Point(0.3f, 5f, 0.2f), new Point(0, -1, 0));
            Assert.NotNull(hit);
            Assert.Equal(0f, hit.Value.y, 4);
            Assert.Equal(0.3f, hit.Value.x, 4);

            // Two-sided: the back of the quad picks too.
            Assert.NotNull(Picker.Raycast(model, new Point(0f, -5f, 0f), new Point(0, 1, 0)));
            // Off the edge, and pointing away, both miss.
            Assert.Null(Picker.Raycast(model, new Point(3f, 5f, 0f), new Point(0, -1, 0)));
            Assert.Null(Picker.Raycast(model, new Point(0f, 5f, 0f), new Point(0, 1, 0)));
        }

        [Fact]
        public void RayThroughScreenCenter_IsTheViewDirection()
        {
            var cam = new Camera();
            cam.Turn(0.4f, 0.1f);
            var (o, d) = Picker.RayThrough(cam, new Frustum(cam.FovY, 1.5f, 0.1f, 100f), 0f, 0f);
            Assert.True(Point.Len(o - cam.Position) < 1e-5f);
            Assert.Equal(1f, Mat3.Dot(d, cam.Forward), 4);
        }

        [Fact]
        public void DoubleClick_OnTheModel_MakesThatPointThePivot()
        {
            var viewer = new Viewer();
            viewer.SetModel(Quad(), 1f);
            viewer.Camera.Yaw   = -MathF.PI / 2f; // look along -Y at the quad's front
            viewer.Camera.Pitch = 0f;
            viewer.FrameModel(1f);
            var eye = viewer.Camera.Position;

            // Slightly right of center: should land on the quad at x > 0.
            viewer.Update(new InputFrame { DoubleClick = true, ClickNdcX = 0.2f, ClickNdcY = 0f, ViewportHeight = 600 }, 0.016f, 1f);

            Assert.True(viewer.Camera.Target.x > 0.05f);
            Assert.Equal(0f, viewer.Camera.Target.y, 3);
            Assert.True(Point.Len(viewer.Camera.Position - eye) < 1e-3f); // re-aims, doesn't move
        }

        [Fact]
        public void DoubleClick_OnBackground_KeepsThePivot()
        {
            var viewer = new Viewer();
            viewer.SetModel(Quad(), 1f);
            var target = viewer.Camera.Target;
            viewer.Update(new InputFrame { DoubleClick = true, ClickNdcX = 0.99f, ClickNdcY = 0.99f, ViewportHeight = 600 }, 0.016f, 1f);
            Assert.True(Point.Len(viewer.Camera.Target - target) < 1e-5f);
        }

        [Fact]
        public void Input_LeftDragOrbits_RightDragPans_ShiftLeftPans()
        {
            var v = new Viewer();
            v.SetModel(Quad(), 1f);

            float yaw = v.Camera.Yaw;
            var target = v.Camera.Target;
            v.Update(new InputFrame { Left = true, DragX = 50, ViewportHeight = 600 }, 0.016f, 1f);
            Assert.NotEqual(yaw, v.Camera.Yaw);
            Assert.True(Point.Len(v.Camera.Target - target) < 1e-5f);

            yaw = v.Camera.Yaw;
            v.Update(new InputFrame { Right = true, DragX = 50, ViewportHeight = 600 }, 0.016f, 1f);
            Assert.Equal(yaw, v.Camera.Yaw);
            Assert.True(Point.Len(v.Camera.Target - target) > 1e-3f);

            target = v.Camera.Target;
            v.Update(new InputFrame { Left = true, Shift = true, DragY = 30, ViewportHeight = 600 }, 0.016f, 1f);
            Assert.Equal(yaw, v.Camera.Yaw);
            Assert.True(Point.Len(v.Camera.Target - target) > 1e-3f);
        }

        [Fact]
        public void Input_TogglesFlipState()
        {
            var v = new Viewer();
            v.SetModel(Quad(), 1f);
            v.Update(new InputFrame { ToggleWireframe = true, ToggleGrid = true, ToggleMode = true }, 0.016f, 1f);
            Assert.True(v.Wireframe);
            Assert.False(v.ShowGrid);
            Assert.Equal(CameraMode.Fly, v.Camera.Mode);
            Assert.Equal("fly", v.ModeName);
        }

        [Fact]
        public void ViewMatrix_MatchesTheCameraTransform()
        {
            var cam = new Camera { Target = new Point(1, 2, 3), Distance = 4 };
            cam.Turn(0.7f, -0.2f);
            var m = RenderPlan.ViewMatrix(cam);
            var p = new Point(0.5f, -1f, 2f);

            // Column-major: cam = M * (p, 1).
            var viaMatrix = new Point(
                m[0] * p.x + m[4] * p.y + m[8]  * p.z + m[12],
                m[1] * p.x + m[5] * p.y + m[9]  * p.z + m[13],
                m[2] * p.x + m[6] * p.y + m[10] * p.z + m[14]);
            var expected = Mat3.Mul(cam.InvRot, p - cam.Position);
            Assert.True(Point.Len(viaMatrix - expected) < 1e-4f);
        }

        [Fact]
        public void DrawOrder_SplitsBlendedMeshesAndCullsOffscreenOnes()
        {
            var opaqueModel = Quad();
            var cam = new Camera();
            cam.Frame(opaqueModel.Center, opaqueModel.Radius, 1f);
            var f = RenderPlan.FrustumFor(cam, opaqueModel, 1f);
            var o = new List<int>(); var b = new List<int>();

            RenderPlan.DrawOrder(opaqueModel, cam, f, o, b);
            Assert.Equal([0], o);
            Assert.Empty(b);

            var blendModel = Quad(blended: true);
            RenderPlan.DrawOrder(blendModel, cam, f, o, b);
            Assert.Empty(o);
            Assert.Equal([0], b);

            cam.Turn(MathF.PI, 0f); // swing round: in orbit mode the pivot (the quad) stays in view
            cam.ToggleMode();
            cam.Turn(MathF.PI, 0f); // now look away in fly mode: the quad is behind the eye
            RenderPlan.DrawOrder(opaqueModel, cam, f, o, b);
            Assert.Empty(o);
        }

        [Fact]
        public void EdgeIndices_ListsSharedEdgesOnce()
        {
            // Needs NORMALs: without them the loader un-indexes the mesh for flat shading, and
            // the diagonal's two copies no longer share vertex indices.
            var material = new MaterialBuilder("m").WithBaseColor(Vector4.One);
            var builder  = new MeshBuilder<VertexPositionNormal>("quad");
            var n        = new Vector3(0, 0, 1);
            builder.UsePrimitive(material).AddQuadrangle(
                new VertexPositionNormal(new Vector3(-1, -1, 0), n), new VertexPositionNormal(new Vector3(1, -1, 0), n),
                new VertexPositionNormal(new Vector3(1, 1, 0), n),   new VertexPositionNormal(new Vector3(-1, 1, 0), n));
            var scene = new SceneBuilder();
            scene.AddRigidMesh(builder, Matrix4x4.Identity);
            var mesh = MeshLoader.FromGltf(scene.ToGltf2(), "q").Meshes.Single();

            Assert.Equal(4, mesh.Positions.Length / 3);
            Assert.Equal(5 * 2, RenderPlan.EdgeIndices(mesh).Length); // 4 sides + 1 diagonal
        }

        [Fact]
        public void Interleave_UsesTheSharedVertexLayout()
        {
            var mesh = Quad().Meshes.Single();
            var v = RenderPlan.Interleave(mesh);
            Assert.Equal(mesh.Positions.Length / 3 * RenderPlan.VertexFloats, v.Length);
            Assert.Equal(mesh.Positions[3], v[RenderPlan.VertexFloats]); // vertex 1 position x
            Assert.Equal(mesh.Colors[3], v[11]);                        // vertex 0 alpha
        }

        [Fact]
        public void Grid_SitsUnderTheModel()
        {
            var model = Quad();
            var grid  = RenderPlan.BuildGrid(model);
            Assert.True(grid.Positions.Length > 0);
            for (int i = 2; i < grid.Positions.Length; i += 3)
                Assert.Equal(model.BoundsMin.z, grid.Positions[i]);
        }

        [Fact]
        public void Shaders_ArePureAscii_AndCarryTheRightHeaders()
        {
            foreach (bool es in new[] { false, true })
                foreach (var src in new[] { Shaders.Vertex(es), Shaders.Fragment(es) })
                {
                    Assert.All(src, ch => Assert.True(ch < 128, $"non-ASCII char U+{(int)ch:X4} in shader"));
                    Assert.StartsWith(es ? "#version 300 es" : "#version 330 core", src);
                }
            Assert.Contains("precision highp float", Shaders.Fragment(true));
        }

        [Fact]
        public void LoadFromBytes_ReadsGlb_AndRejectsOtherData()
        {
            var model = MeshLoader.Load(QuadGlb(), "tri.glb");
            Assert.Single(model.Meshes);
            Assert.Equal("tri.glb", model.Name);

            Assert.Throws<InvalidDataException>(() => MeshLoader.Load("{\"asset\":{}}"u8.ToArray(), "x.gltf"));
        }
    }
}
