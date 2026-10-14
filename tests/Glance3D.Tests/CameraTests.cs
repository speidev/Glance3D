using Xunit;

namespace Glance3D.Tests
{
    public class CameraTests
    {
        private const float Eps = 1e-3f;

        [Fact]
        public void Frame_FitsTheWholeBoundingSphereOnScreen()
        {
            foreach (float aspect in new[] { 0.5f, 1f, 16f / 9f, 3f })
            {
                var cam    = new Camera();
                var center = new Point(3, -2, 1);
                cam.Frame(center, 2f, aspect);

                var frustum = new Frustum(cam.FovY, aspect, 0.01f, 1000f);
                // Extreme points of the sphere along the camera's right and up axes.
                foreach (var dir in new[] { cam.Right, -cam.Right, cam.Up, -cam.Up })
                {
                    var (x, y) = frustum.ProjectNdc(Mat3.Mul(cam.InvRot, center + dir * 2f - cam.Position));
                    Assert.InRange(x, -1f, 1f);
                    Assert.InRange(y, -1f, 1f);
                }
            }
        }

        [Fact]
        public void Orbit_KeepsTheEyeAtDistanceFromThePivot()
        {
            var cam = new Camera { Target = new Point(1, 2, 3), Distance = 7f };
            cam.Turn(1.3f, 0.4f);
            Assert.Equal(7f, Point.Len(cam.Position - cam.Target), 3);
            // ...and looking at it.
            var toTarget = Mat3.Normalize(cam.Target - cam.Position);
            Assert.Equal(1f, Mat3.Dot(toTarget, cam.Forward), 3);
        }

        [Fact]
        public void Pitch_IsClampedShortOfVertical()
        {
            var cam = new Camera();
            cam.Turn(0f, 10f);
            Assert.Equal(Camera.MaxPitch, cam.Pitch, 4);
            cam.Turn(0f, -20f);
            Assert.Equal(-Camera.MaxPitch, cam.Pitch, 4);
        }

        [Fact]
        public void Basis_IsOrthonormal_WithWorldUpAboveTheHorizon()
        {
            var cam = new Camera();
            cam.Turn(0.7f, -0.3f);
            Assert.Equal(0f, Mat3.Dot(cam.Forward, cam.Right), 4);
            Assert.Equal(0f, Mat3.Dot(cam.Forward, cam.Up), 4);
            Assert.Equal(0f, Mat3.Dot(cam.Right, cam.Up), 4);
            Assert.Equal(0f, cam.Right.z, 4); // no roll
            Assert.True(cam.Up.z > 0f);
        }

        [Fact]
        public void FlyLook_KeepsTheEyeInPlace()
        {
            var cam = new Camera();
            cam.Frame(new Point(0, 0, 0), 1f, 1f);
            cam.ToggleMode();
            var before = cam.Position;
            cam.Turn(0.8f, 0.2f);
            Assert.True(Point.Len(cam.Position - before) < Eps);
        }

        [Fact]
        public void ToggleMode_DoesNotJumpTheView()
        {
            var cam = new Camera();
            cam.Frame(new Point(0, 0, 0), 1f, 1f);
            cam.ToggleMode();
            cam.Move(0.5f, 0.2f, 0.1f);
            var pos = cam.Position;
            var fwd = cam.Forward;

            cam.ToggleMode();
            cam.Turn(0f, 0f); // force a rebuild in orbit mode
            Assert.True(Point.Len(cam.Position - pos) < Eps);
            Assert.True(Point.Len(cam.Forward - fwd) < Eps);
        }

        [Fact]
        public void OrbitAround_KeepsTheEyeAndAimsAtThePoint()
        {
            var cam = new Camera();
            cam.Frame(new Point(0, 0, 0), 1f, 1f);
            var eye    = cam.Position;
            var target = new Point(0.3f, -0.2f, 0.1f);

            cam.OrbitAround(target);

            Assert.True(Point.Len(cam.Position - eye) < Eps);
            Assert.True(Point.Len(cam.Target - target) < Eps);
        }

        [Fact]
        public void Dolly_NeverPassesThePivot()
        {
            var cam = new Camera();
            cam.Frame(new Point(0, 0, 0), 1f, 1f);
            cam.Dolly(1000f);
            Assert.True(cam.Distance >= cam.MinDistance);
            Assert.True(cam.Distance > 0f);
        }

        [Fact]
        public void Pan_MovesEyeAndPivotTogether()
        {
            var cam = new Camera();
            cam.Frame(new Point(0, 0, 0), 1f, 1f);
            var offset = cam.Position - cam.Target;
            cam.Pan(120f, -40f, 720f);
            Assert.True(Point.Len(cam.Position - cam.Target - offset) < Eps);
            Assert.True(Point.Len(cam.Target) > 0f);
        }
    }

    public class FrustumTests
    {
        [Fact]
        public void Unproject_InvertsTheShaderProjection()
        {
            var f   = new Frustum(1f, 1.5f, 0.05f, 200f);
            var cam = new Point(12f, -3f, 2f);

            var (x, y) = f.ProjectNdc(cam);
            // Same depth mapping as the vertex shader, then to a [0,1] depth-buffer value.
            float ndcZ = (f.Far + f.Near) / (f.Far - f.Near) - 2f * f.Far * f.Near / ((f.Far - f.Near) * cam.x);
            var back = f.Unproject(x, y, (ndcZ + 1f) / 2f);

            Assert.Equal(cam.x, back.x, 2);
            Assert.Equal(cam.y, back.y, 2);
            Assert.Equal(cam.z, back.z, 2);
        }

        [Fact]
        public void Culls_KeepsASphereThatStillPokesIntoView()
        {
            // Regression: the original test compared the raw y offset past the side plane to the
            // radius instead of the perpendicular distance, culling spheres that were still partly
            // on screen. This one sits just outside the right edge but overlaps it.
            var f = new Frustum(MathF.PI / 2f, 1f, 0.1f, 100f); // half-tan 1: side plane y = x
            Assert.False(f.Culls(new Point(10f, 11.2f, 0f), 1f));
        }

        [Fact]
        public void Culls_RejectsSpheresFullyOutside()
        {
            var f = new Frustum(MathF.PI / 2f, 1f, 0.1f, 100f);
            Assert.True(f.Culls(new Point(10f, 13f, 0f), 1f));   // right
            Assert.True(f.Culls(new Point(-5f, 0f, 0f), 1f));    // behind
            Assert.True(f.Culls(new Point(200f, 0f, 0f), 1f));   // past far
            Assert.False(f.Culls(new Point(10f, 0f, 0f), 1f));   // dead ahead
        }
    }
}
