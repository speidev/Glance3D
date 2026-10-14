using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using Xunit;

namespace Glance3D.Tests
{
    public class MeshLoaderTests
    {
        // One triangle in glTF's XY plane, counter-clockwise seen from +Z (glTF's "front").
        private static ModelRoot Triangle(Matrix4x4 transform, Vector4? baseColor = null)
        {
            var material = new MaterialBuilder("m").WithBaseColor(baseColor ?? Vector4.One);
            var mesh     = new MeshBuilder<VertexPosition>("tri");
            mesh.UsePrimitive(material).AddTriangle(
                new VertexPosition(0, 0, 0), new VertexPosition(1, 0, 0), new VertexPosition(0, 1, 0));

            var scene = new SceneBuilder();
            scene.AddRigidMesh(mesh, transform);
            return scene.ToGltf2();
        }

        // Twice the signed screen-space area of a triangle as the camera sees it; > 0 is CCW.
        private static float ScreenWinding(Model model, Camera cam)
        {
            var frustum = new Frustum(cam.FovY, 1f, 0.01f, 100f);
            var mesh    = model.Meshes.Single();
            var ndc     = mesh.Indices.Take(3).Select(i =>
            {
                var p = new Point(mesh.Positions[i * 3], mesh.Positions[i * 3 + 1], mesh.Positions[i * 3 + 2]);
                return frustum.ProjectNdc(Mat3.Mul(cam.InvRot, p - cam.Position));
            }).ToArray();
            return (ndc[1].x - ndc[0].x) * (ndc[2].y - ndc[0].y) - (ndc[2].x - ndc[0].x) * (ndc[1].y - ndc[0].y);
        }

        // A camera looking at the model from wherever glTF's +Z ("front") ended up in the world,
        // or from the opposite side. Derived from ToWorld rather than hard-coded, so these tests
        // check the viewing result of the mapping, not just one particular choice of mapping.
        private static Camera FromFront(Model model, bool back = false)
        {
            var front = MeshLoader.ToWorld(new Vector3(0, 0, 1));
            if (back) front = -front;
            var cam = new Camera { Yaw = MathF.Atan2(-front.y, -front.x), Pitch = 0f };
            cam.Frame(model.Center, model.Radius, 1f);
            return cam;
        }

        [Fact]
        public void FrontFacingTriangle_StaysCounterClockwiseOnScreen()
        {
            // The renderer draws with FrontFace(Ccw); if this flips, every model goes inside out.
            var model = MeshLoader.FromGltf(Triangle(Matrix4x4.Identity), "t");
            Assert.True(ScreenWinding(model, FromFront(model)) > 0f);
            Assert.True(ScreenWinding(model, FromFront(model, back: true)) < 0f);
        }

        [Fact]
        public void ModelIsNotMirrored()
        {
            // glTF is right-handed: seen from the front (+Z) with +Y up, +X is on the right. A
            // mapping that only rotates Y-up to Z-up puts it on the left (text reads backwards).
            // Winding alone can't catch that: mirrored and seen from behind is CCW again.
            var model = MeshLoader.FromGltf(Triangle(Matrix4x4.Identity), "t");
            var cam   = FromFront(model);
            var f     = new Frustum(cam.FovY, 1f, 0.01f, 100f);
            var plusX = MeshLoader.ToWorld(new Vector3(1, 0, 0));
            var (x, _) = f.ProjectNdc(Mat3.Mul(cam.InvRot, plusX - cam.Position));
            var (x0, _) = f.ProjectNdc(Mat3.Mul(cam.InvRot, new Point(0, 0, 0) - cam.Position));
            Assert.True(x > x0);

            var up = MeshLoader.ToWorld(new Vector3(0, 1, 0));
            Assert.True(Mat3.Dot(up, cam.Up) > 0.99f);
        }

        [Fact]
        public void MirroredNode_KeepsFrontFaceWinding()
        {
            // Mirroring in X turns the triangle's winding clockwise in the raw data; the loader
            // must reorder it so the face seen from the front is still the front face.
            var model = MeshLoader.FromGltf(Triangle(Matrix4x4.CreateScale(-1, 1, 1)), "t");
            Assert.True(ScreenWinding(model, FromFront(model)) > 0f);
        }

        [Fact]
        public void ToWorld_MapsGltfUpToWorldZ_AndFrontToWorldY()
        {
            var up    = MeshLoader.ToWorld(new Vector3(0, 1, 0));
            var front = MeshLoader.ToWorld(new Vector3(0, 0, 1));
            Assert.Equal(new Point(0, 0, 1).ToString(), up.ToString());
            Assert.Equal(new Point(0, 1, 0).ToString(), front.ToString());
        }

        [Fact]
        public void NodeTransform_IsBakedIntoPositions()
        {
            // The original engine ignored node transforms, so models wrapped in a corrective
            // rotation or offset node rendered sideways or misplaced.
            var model = MeshLoader.FromGltf(Triangle(Matrix4x4.CreateTranslation(10, 0, 0)), "t");
            Assert.InRange(model.BoundsMin.x, 9.99f, 10.01f);
            Assert.InRange(model.BoundsMax.x, 10.99f, 11.01f);
        }

        [Fact]
        public void BaseColorFactor_IsKeptWithoutATexture()
        {
            var model = MeshLoader.FromGltf(Triangle(Matrix4x4.Identity, new Vector4(0.2f, 0.4f, 0.6f, 1f)), "t");
            var mat   = model.Materials[model.Meshes.Single().MaterialIndex];
            Assert.Null(mat.BaseColorTexture);
            Assert.Equal(0.4f, mat.BaseColor[1], 3);
        }

        [Fact]
        public void MissingNormals_ProduceOutwardFlatNormals()
        {
            // The triangle has no NORMAL attribute; its computed normal must face glTF +Z (world +Y),
            // toward the side the triangle is front-facing from.
            var mesh = MeshLoader.FromGltf(Triangle(Matrix4x4.Identity), "t").Meshes.Single();
            for (int v = 0; v < 3; v++)
                Assert.Equal(1f, mesh.Normals[v * 3 + 1], 3);
        }

        [Fact]
        public void Bounds_ContainEveryVertex()
        {
            var model = MeshLoader.FromGltf(Triangle(Matrix4x4.CreateScale(4)), "t");
            var pos   = model.Meshes.Single().Positions;
            for (int i = 0; i < pos.Length; i += 3)
            {
                var p = new Point(pos[i], pos[i + 1], pos[i + 2]);
                Assert.True(Point.Len(p - model.Center) <= model.Radius + 1e-4f);
            }
        }
    }
}
