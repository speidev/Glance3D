namespace Glance3D
{
    // Plain data handed from MeshLoader to the renderer. Everything is already in world space
    // (node transforms and the glTF Y-up -> Z-up conversion are baked in at load), so the
    // renderer only ever needs the camera transform. GL handles are filled in on upload.

    public sealed class Mesh
    {
        public float[] Positions = [];   // x,y,z interleaved, world space
        public float[] Normals   = [];   // x,y,z interleaved, unit length
        public float[] UVs       = [];   // u,v interleaved
        public float[] Colors    = [];   // r,g,b,a interleaved (COLOR_0, white when absent)
        public uint[]  Indices   = [];   // triangle list
        public int     MaterialIndex;
        public Point   BoundCenter;
        public float   BoundRadius;
        public int     Vao, Vbo, Ebo;   // assigned by the renderer

        public int TriangleCount => Indices.Length / 3;
    }

    public enum AlphaMode { Opaque, Mask, Blend }

    public sealed class Texture
    {
        public byte[] RgbaData = [];
        public int    Width, Height;
        public bool   Nearest;            // glTF sampler asked for NEAREST magnification (pixel art)
        public int    WrapS, WrapT;       // GL wrap enums, straight from the glTF sampler
        public int    GlHandle;           // assigned by the renderer
    }

    public sealed class Material
    {
        public string    Name        = "";
        public float[]   BaseColor   = [1, 1, 1, 1];
        public Texture?  BaseColorTexture;
        public AlphaMode Alpha       = AlphaMode.Opaque;
        public float     AlphaCutoff = 0.5f;
        public bool      DoubleSided;
        public bool      Unlit;           // KHR_materials_unlit
    }

    public sealed class Model
    {
        public string     Name      = "";
        public Mesh[]     Meshes    = [];
        public Material[] Materials = [];
        public Point      BoundsMin, BoundsMax;
        public Point      Center;
        public float      Radius = 1f;

        public int TriangleCount => Meshes.Sum(m => m.TriangleCount);
    }
}
