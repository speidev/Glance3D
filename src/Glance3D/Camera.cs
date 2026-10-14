namespace Glance3D
{
    public enum CameraMode { Orbit, Fly }

    // One camera rig, two ways of driving it. The rig is a pivot (Target), a distance, and a
    // yaw/pitch heading; Position is derived from those. Orbit mode keeps the pivot fixed and
    // swings the eye around it, while Fly mode keeps the eye fixed and drags the pivot along in
    // front of it. Because both modes share the same state, toggling between them never jumps
    // the view: you can fly up to a detail and switch back to orbiting around whatever is now
    // straight ahead.
    //
    // Yaw/pitch replaced the original engine's incremental ApplyLook (multiplying small
    // rotations into Rot every frame and re-orthonormalizing). That was right for a free-look
    // FPS camera, but an orbit camera needs to know its absolute heading to place the eye, and
    // angles also make Reset and the pitch clamp exact instead of approximate.
    //
    // World is Z-up, and camera space is X=forward, Y=right, Z=up (see Frustum).
    public sealed class Camera
    {
        public const float MaxPitch     = 89f * MathF.PI / 180f;
        public const float DefaultYaw   = -120f * MathF.PI / 180f; // front three-quarter view: glTF's +Z "front" maps to world +Y
        public const float DefaultPitch = -20f * MathF.PI / 180f;  // looking slightly down
        public const float DollyFactor  = 0.85f;                   // distance multiplier per wheel notch

        public CameraMode Mode     = CameraMode.Orbit;
        public float      FovY     = 50f * MathF.PI / 180f;
        public float      Yaw      = DefaultYaw;
        public float      Pitch    = DefaultPitch;
        public float      Distance = 5f;
        public Point      Target;

        public Point Position { get; private set; }
        public Mat3  Rot      { get; private set; } = Mat3.Identity; // camera -> world (columns: fwd, right, up)
        public Mat3  InvRot   { get; private set; } = Mat3.Identity; // world -> camera

        public Point Forward => new(Rot.m00, Rot.m10, Rot.m20);
        public Point Right   => new(Rot.m01, Rot.m11, Rot.m21);
        public Point Up      => new(Rot.m02, Rot.m12, Rot.m22);

        // Closest the orbit may get to its pivot, so a wheel spin can't push the eye through it.
        // Set from the model's size by Frame.
        public float MinDistance = 1e-3f;

        public Camera() => Update();

        // Point the pivot at a bounding sphere and back off until the sphere fits the narrower of
        // the two fields of view, with a small margin. Keeps the current heading.
        public void Frame(Point center, float radius, float aspect)
        {
            radius = MathF.Max(radius, 1e-4f);
            float halfTanZ = MathF.Tan(FovY / 2f);
            float halfTan  = MathF.Min(halfTanZ, halfTanZ * aspect);
            float halfAng  = MathF.Atan(halfTan);

            Target      = center;
            Distance    = radius / MathF.Sin(halfAng) * 1.05f;
            MinDistance = radius * 0.01f;
            Mode        = CameraMode.Orbit;
            Update();
        }

        public void ResetHeading()
        {
            Yaw   = DefaultYaw;
            Pitch = DefaultPitch;
            Update();
        }

        // Orbit mode: swing the eye around the pivot. Fly mode: look around from where you stand.
        public void Turn(float yawDelta, float pitchDelta)
        {
            Yaw   = WrapAngle(Yaw + yawDelta);
            Pitch = Math.Clamp(Pitch + pitchDelta, -MaxPitch, MaxPitch);
            Update();
        }

        // Drag the view in the screen plane, scaled so the point under the pivot tracks the
        // cursor 1:1 regardless of zoom.
        public void Pan(float dxPixels, float dyPixels, float viewportHeight)
        {
            float worldPerPixel = 2f * Distance * MathF.Tan(FovY / 2f) / MathF.Max(1f, viewportHeight);
            Translate(Right * (-dxPixels * worldPerPixel) + Up * (dyPixels * worldPerPixel));
        }

        // Mouse wheel. Orbit zooms toward the pivot geometrically, so every notch feels the same
        // at any scale. Fly has no pivot to approach, so it steps forward instead.
        public void Dolly(float notches)
        {
            if (Mode == CameraMode.Orbit)
            {
                Distance = MathF.Max(MinDistance, Distance * MathF.Pow(DollyFactor, notches));
                Update();
            }
            else
            {
                Translate(Forward * (notches * Distance * 0.15f));
            }
        }

        // Keyboard movement. Moves eye and pivot together in both modes, so in Orbit mode WASD
        // walks the whole rig. Forward follows the view direction (this is a viewer, not a game,
        // so flying into the floor is allowed), while up/down are always world-vertical.
        public void Move(float forward, float right, float up)
        {
            Translate(Forward * forward + Right * right + new Point(0, 0, up));
        }

        // Make a world point the new pivot without moving the eye: re-aim at it and orbit around
        // it from here on. Used by double-click, which picks the point from the depth buffer.
        public void OrbitAround(Point target)
        {
            var   dir  = target - Position;
            float dist = Point.Len(dir);
            if (dist < 1e-6f) return;

            var fwd  = dir * (1f / dist);
            Yaw      = MathF.Atan2(fwd.y, fwd.x);
            Pitch    = Math.Clamp(MathF.Asin(Math.Clamp(fwd.z, -1f, 1f)), -MaxPitch, MaxPitch);
            Target   = target;
            Distance = dist;
            Mode     = CameraMode.Orbit;
            Update();
        }

        public void ToggleMode() => Mode = Mode == CameraMode.Orbit ? CameraMode.Fly : CameraMode.Orbit;

        // Depth range for a scene's bounding sphere. Both planes are derived from the far plane, so
        // the near/far ratio (and therefore depth precision) is the same for a 1 mm part as for a
        // 1 km terrain. The 2e-4 ratio keeps z-fighting off a 24-bit buffer while still letting
        // the eye get very close to a surface.
        public (float near, float far) ClipRange(Point sceneCenter, float sceneRadius)
        {
            float far  = Point.Len(Position - sceneCenter) + MathF.Max(sceneRadius, 1e-3f) * 2f;
            float near = far * 2e-4f;
            return (near, far);
        }

        private void Translate(Point delta)
        {
            Target   += delta;
            Position += delta;
        }

        private void Update()
        {
            float cp = MathF.Cos(Pitch);
            var fwd   = new Point(cp * MathF.Cos(Yaw), cp * MathF.Sin(Yaw), MathF.Sin(Pitch));
            var right = Mat3.Normalize(Mat3.Cross(new Point(0, 0, 1), fwd)); // never degenerate: pitch is clamped short of vertical
            var up    = Mat3.Cross(fwd, right);

            Rot    = Mat3.FromBasis(fwd, right, up);
            InvRot = Mat3.Transpose(Rot);

            if (Mode == CameraMode.Orbit)
                Position = Target - fwd * Distance;
            else
                Target = Position + fwd * Distance;
        }

        private static float WrapAngle(float a)
        {
            const float twoPi = 2f * MathF.PI;
            a %= twoPi;
            return a > MathF.PI ? a - twoPi : a < -MathF.PI ? a + twoPi : a;
        }
    }

    // The original engine's hand-rolled perspective, pulled out of the window so it can be tested
    // without a GL context. Camera space is X=forward, Y=right, Z=up, so the projection divides by
    // x (depth) rather than -z, and the vertex shader in RenderWindow builds gl_Position as
    // (y / HalfTanY, z / HalfTanZ, depth, x). Keep ProjectNdc and that shader in sync.
    public readonly struct Frustum
    {
        public readonly float HalfTanY; // horizontal: tan(fovX / 2)
        public readonly float HalfTanZ; // vertical:   tan(fovY / 2)
        public readonly float Near, Far;

        // |(1, -HalfTan)|: converts "how far past the side plane y = HalfTan * x" measured along y
        // into a true perpendicular distance. The original engine compared the raw y offset to the
        // radius, which culls spheres that still poke into view at the screen edges.
        private readonly float _sideScaleY, _sideScaleZ;

        public Frustum(float fovY, float aspect, float near, float far)
        {
            HalfTanZ    = MathF.Tan(fovY / 2f);
            HalfTanY    = HalfTanZ * aspect;
            Near        = near;
            Far         = far;
            _sideScaleY = MathF.Sqrt(1f + HalfTanY * HalfTanY);
            _sideScaleZ = MathF.Sqrt(1f + HalfTanZ * HalfTanZ);
        }

        public (float x, float y) ProjectNdc(Point cam) =>
            (cam.y / (HalfTanY * cam.x), cam.z / (HalfTanZ * cam.x));

        // Inverse of the vertex shader: NDC x/y plus a [0,1] depth-buffer value back to camera
        // space. The shader writes ndcZ * x = A*x - B, so ndcZ = A - B/x and x = B / (A - ndcZ).
        public Point Unproject(float ndcX, float ndcY, float depth01)
        {
            float a     = (Far + Near) / (Far - Near);
            float b     = 2f * Far * Near / (Far - Near);
            float depth = b / (a - (depth01 * 2f - 1f));
            return new Point(depth, ndcX * HalfTanY * depth, ndcY * HalfTanZ * depth);
        }

        // Bounding-sphere test against all six planes. True only when the sphere is entirely
        // outside one of them, so it never culls something visible.
        public bool Culls(Point c, float r)
        {
            if (c.x + r < Near) return true;
            if (c.x - r > Far)  return true;

            float ry = r * _sideScaleY, rz = r * _sideScaleZ;
            if (c.y - ry >  HalfTanY * c.x) return true;
            if (c.y + ry < -HalfTanY * c.x) return true;
            if (c.z - rz >  HalfTanZ * c.x) return true;
            if (c.z + rz < -HalfTanZ * c.x) return true;
            return false;
        }
    }
}
