using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

// A small log cabin in glTF's conventions (Y up, front facing +Z, meters): a stone base, stacked
// log walls with crossed corners, a hip roof, a chimney, a door and windows. Flat-shaded, one
// solid-color material per part, no textures, so it stays a few KB.
//
// Faces are wound by computing each one's normal and flipping it if it points into the part
// (Face/Box below) instead of trusting hand-ordered vertices. The viewer's back-face culling
// would show any mistake as a hole.

var outPath = args.Length > 0 ? args[0] : Path.Combine("samples", "cabin.glb");

var stone   = Mat("stone",   0.46f, 0.46f, 0.48f);
var logA    = Mat("log",     0.62f, 0.40f, 0.22f);
var logB    = Mat("log-dark", 0.54f, 0.34f, 0.18f);
var roof    = Mat("roof",    0.60f, 0.22f, 0.15f);
var door    = Mat("door",    0.30f, 0.18f, 0.10f);
var glass   = Mat("window",  0.62f, 0.78f, 0.86f);

var mesh = new MeshBuilder<VertexPositionNormal>("cabin");

// Stone base.
Box(stone, new(-1.65f, 0f, -1.25f), new(1.65f, 0.35f, 1.25f));

// Log walls: five courses. Front/back logs run along X and stick out past the side walls,
// side logs run along Z and stick out past the front/back, like notched corners.
const int   courses = 5;
const float logH    = 0.3f, baseY = 0.35f;
for (int i = 0; i < courses; i++)
{
    float y0 = baseY + i * logH, y1 = y0 + logH;
    var   m  = i % 2 == 0 ? logA : logB;
    Box(m, new(-1.6f, y0, 0.85f),  new(1.6f, y1, 1.1f));   // front
    Box(m, new(-1.6f, y0, -1.1f),  new(1.6f, y1, -0.85f)); // back
    var s = i % 2 == 0 ? logB : logA;
    Box(s, new(-1.5f, y0, -1.2f),  new(-1.25f, y1, 1.2f)); // left
    Box(s, new(1.25f, y0, -1.2f),  new(1.5f, y1, 1.2f));   // right
}
float wallTop = baseY + courses * logH;

// Door and two windows on the front, one window on each side.
Box(door,  new(-0.32f, baseY, 1.1f),   new(0.32f, baseY + 1.05f, 1.15f));
Box(glass, new(-1.1f, 0.85f, 1.1f),    new(-0.65f, 1.3f, 1.14f));
Box(glass, new(0.65f, 0.85f, 1.1f),    new(1.1f, 1.3f, 1.14f));
Box(glass, new(-1.54f, 0.85f, -0.25f), new(-1.5f, 1.3f, 0.25f));
Box(glass, new(1.5f, 0.85f, -0.25f),   new(1.54f, 1.3f, 0.25f));

// Hip roof: eaves overhang the walls, ridge along X.
float eaveY = wallTop, ridgeY = wallTop + 1.0f;
var e0 = new Vector3(-1.85f, eaveY, 1.45f);  // front-left
var e1 = new Vector3(1.85f, eaveY, 1.45f);   // front-right
var e2 = new Vector3(1.85f, eaveY, -1.45f);  // back-right
var e3 = new Vector3(-1.85f, eaveY, -1.45f); // back-left
var r0 = new Vector3(-0.7f, ridgeY, 0f);
var r1 = new Vector3(0.7f, ridgeY, 0f);
var roofCenter = new Vector3(0f, eaveY + 0.3f, 0f);
Face(roof, roofCenter, e0, e1, r1, r0);  // front slope
Face(roof, roofCenter, e2, e3, r0, r1);  // back slope
Face(roof, roofCenter, e1, e2, r1);      // right hip
Face(roof, roofCenter, e3, e0, r0);      // left hip
Face(roof, roofCenter, e0, e3, e2, e1);  // soffit (underside)

// Chimney through the back slope.
Box(stone, new(0.75f, wallTop - 0.2f, -0.75f), new(1.1f, ridgeY + 0.35f, -0.4f));

var scene = new SceneBuilder();
scene.AddRigidMesh(mesh, Matrix4x4.Identity);
var model = scene.ToGltf2();
model.Asset.Generator = "Glance3D SampleGen";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
model.SaveGLB(outPath);
Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length:N0} bytes)");

static MaterialBuilder Mat(string name, float r, float g, float b) =>
    new MaterialBuilder(name).WithBaseColor(new Vector4(r, g, b, 1f));

// One flat polygon (3 or 4 corners, in order around its edge), wound so its normal points away
// from `inside`.
void Face(MaterialBuilder m, Vector3 inside, params Vector3[] p)
{
    var n = Vector3.Normalize(Vector3.Cross(p[1] - p[0], p[2] - p[0]));
    var centroid = p.Aggregate(Vector3.Zero, (a, v) => a + v) / p.Length;
    if (Vector3.Dot(n, centroid - inside) < 0f)
    {
        Array.Reverse(p);
        n = -n;
    }

    var prim = mesh.UsePrimitive(m);
    VertexPositionNormal V(Vector3 v) => new(v, n);
    prim.AddTriangle(V(p[0]), V(p[1]), V(p[2]));
    if (p.Length == 4) prim.AddTriangle(V(p[0]), V(p[2]), V(p[3]));
}

void Box(MaterialBuilder m, Vector3 min, Vector3 max)
{
    var c = (min + max) * 0.5f;
    Vector3 P(float x, float y, float z) => new(x, y, z);
    float x0 = min.X, y0 = min.Y, z0 = min.Z, x1 = max.X, y1 = max.Y, z1 = max.Z;
    Face(m, c, P(x0, y0, z1), P(x1, y0, z1), P(x1, y1, z1), P(x0, y1, z1)); // +Z
    Face(m, c, P(x0, y0, z0), P(x0, y1, z0), P(x1, y1, z0), P(x1, y0, z0)); // -Z
    Face(m, c, P(x1, y0, z0), P(x1, y1, z0), P(x1, y1, z1), P(x1, y0, z1)); // +X
    Face(m, c, P(x0, y0, z0), P(x0, y0, z1), P(x0, y1, z1), P(x0, y1, z0)); // -X
    Face(m, c, P(x0, y1, z0), P(x0, y1, z1), P(x1, y1, z1), P(x1, y1, z0)); // +Y
    Face(m, c, P(x0, y0, z0), P(x1, y0, z0), P(x1, y0, z1), P(x0, y0, z1)); // -Y
}
