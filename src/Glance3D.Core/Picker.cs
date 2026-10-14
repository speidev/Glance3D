namespace Glance3D
{
    // Finds the surface under the cursor by casting a ray against the model's triangles, for
    // double-click re-pivoting. It replaced reading the depth buffer, which WebGL can't do, so
    // both hosts now pick the same way. Meshes whose bounding sphere the ray misses are skipped
    // whole, which keeps it fast enough even in the browser's interpreter for a click (not for a
    // per-frame hover).
    public static class Picker
    {
        // World-space ray from the eye through a point in NDC ([-1, 1], y up).
        public static (Point origin, Point dir) RayThrough(Camera cam, Frustum frustum, float ndcX, float ndcY)
        {
            var camDir = new Point(1f, ndcX * frustum.HalfTanY, ndcY * frustum.HalfTanZ);
            return (cam.Position, Mat3.Normalize(Mat3.Mul(cam.Rot, camDir)));
        }

        // Nearest hit of a ray on any triangle (either side), or null.
        public static Point? Raycast(Model model, Point origin, Point dir)
        {
            float best = float.PositiveInfinity;
            foreach (var mesh in model.Meshes)
            {
                // Ray vs bounding sphere: skip the mesh unless the ray passes within its radius in
                // front of the eye.
                var   oc   = mesh.BoundCenter - origin;
                float tc   = Mat3.Dot(oc, dir);
                float dSq  = Mat3.Dot(oc, oc) - tc * tc;
                float rSq  = mesh.BoundRadius * mesh.BoundRadius;
                if (dSq > rSq) continue;
                if (tc + mesh.BoundRadius < 0f) continue;
                if (tc - MathF.Sqrt(MathF.Max(0f, rSq - dSq)) > best) continue;

                var p   = mesh.Positions;
                var idx = mesh.Indices;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int ia = (int)idx[t] * 3, ib = (int)idx[t + 1] * 3, ic = (int)idx[t + 2] * 3;
                    float hit = IntersectTriangle(origin, dir,
                        new Point(p[ia], p[ia + 1], p[ia + 2]),
                        new Point(p[ib], p[ib + 1], p[ib + 2]),
                        new Point(p[ic], p[ic + 1], p[ic + 2]));
                    if (hit < best) best = hit;
                }
            }
            return float.IsPositiveInfinity(best) ? null : origin + dir * best;
        }

        // Moller-Trumbore, two-sided. Returns the ray parameter, or +infinity for a miss.
        internal static float IntersectTriangle(Point o, Point d, Point a, Point b, Point c)
        {
            var   e1  = b - a;
            var   e2  = c - a;
            var   pv  = Mat3.Cross(d, e2);
            float det = Mat3.Dot(e1, pv);
            if (MathF.Abs(det) < 1e-12f) return float.PositiveInfinity;

            float inv = 1f / det;
            var   tv  = o - a;
            float u   = Mat3.Dot(tv, pv) * inv;
            if (u < 0f || u > 1f) return float.PositiveInfinity;

            var   qv = Mat3.Cross(tv, e1);
            float v  = Mat3.Dot(d, qv) * inv;
            if (v < 0f || u + v > 1f) return float.PositiveInfinity;

            float t = Mat3.Dot(e2, qv) * inv;
            return t > 1e-6f ? t : float.PositiveInfinity;
        }
    }
}
