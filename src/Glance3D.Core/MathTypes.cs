namespace Glance3D
{
    // Hand-rolled vector/matrix types. Deliberately not System.Numerics or OpenTK.Mathematics:
    // the whole renderer is written in its own camera convention (X=forward, Y=right, Z=up)
    // and keeping the math in one small, readable file makes that convention easy to audit.
    // GL only ever sees the result as a float[16] (see RenderPlan.ViewMatrix).
    public struct Point
    {
        public float x, y, z;

        public Point(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static Point operator +(Point a, Point b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Point operator -(Point a, Point b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Point operator -(Point a)          => new(-a.x, -a.y, -a.z);
        public static Point operator *(Point a, float s) => new(a.x * s, a.y * s, a.z * s);
        public static Point operator *(float s, Point a) => new(a.x * s, a.y * s, a.z * s);

        public static float Len(Point v) => MathF.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        public static Point Min(Point a, Point b) => new(MathF.Min(a.x, b.x), MathF.Min(a.y, b.y), MathF.Min(a.z, b.z));
        public static Point Max(Point a, Point b) => new(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y), MathF.Max(a.z, b.z));

        public override readonly string ToString() => $"({x:0.###}, {y:0.###}, {z:0.###})";
    }

    // Column vectors: Mul(M, v) = M * v. A camera's Rot has its world-space basis vectors as
    // columns (fwd, right, up), so Rot maps camera space -> world and its transpose the reverse.
    public struct Mat3
    {
        public float m00, m01, m02, m10, m11, m12, m20, m21, m22;

        public static readonly Mat3 Identity = new() { m00 = 1, m11 = 1, m22 = 1 };

        public static Point Mul(Mat3 M, Point v) => new()
        {
            x = M.m00 * v.x + M.m01 * v.y + M.m02 * v.z,
            y = M.m10 * v.x + M.m11 * v.y + M.m12 * v.z,
            z = M.m20 * v.x + M.m21 * v.y + M.m22 * v.z
        };

        public static Mat3 Transpose(Mat3 A) => new()
        {
            m00 = A.m00, m01 = A.m10, m02 = A.m20,
            m10 = A.m01, m11 = A.m11, m12 = A.m21,
            m20 = A.m02, m21 = A.m12, m22 = A.m22
        };

        public static Mat3 Mul(Mat3 A, Mat3 B) => new()
        {
            m00 = A.m00 * B.m00 + A.m01 * B.m10 + A.m02 * B.m20,
            m01 = A.m00 * B.m01 + A.m01 * B.m11 + A.m02 * B.m21,
            m02 = A.m00 * B.m02 + A.m01 * B.m12 + A.m02 * B.m22,
            m10 = A.m10 * B.m00 + A.m11 * B.m10 + A.m12 * B.m20,
            m11 = A.m10 * B.m01 + A.m11 * B.m11 + A.m12 * B.m21,
            m12 = A.m10 * B.m02 + A.m11 * B.m12 + A.m12 * B.m22,
            m20 = A.m20 * B.m00 + A.m21 * B.m10 + A.m22 * B.m20,
            m21 = A.m20 * B.m01 + A.m21 * B.m11 + A.m22 * B.m21,
            m22 = A.m20 * B.m02 + A.m21 * B.m12 + A.m22 * B.m22,
        };

        public static Point Normalize(Point v)
        {
            float len = Point.Len(v);
            if (len < 1e-6f) return new Point(0, 0, 0);
            return v * (1f / len);
        }

        public static float Dot(Point a, Point b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public static Point Cross(Point a, Point b) => new()
        {
            x = a.y * b.z - a.z * b.y,
            y = a.z * b.x - a.x * b.z,
            z = a.x * b.y - a.y * b.x
        };

        public static Mat3 FromBasis(Point fwd, Point right, Point up) => new()
        {
            m00 = fwd.x,   m01 = right.x, m02 = up.x,
            m10 = fwd.y,   m11 = right.y, m12 = up.y,
            m20 = fwd.z,   m21 = right.z, m22 = up.z
        };
    }
}
