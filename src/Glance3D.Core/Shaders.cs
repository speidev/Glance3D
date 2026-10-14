namespace Glance3D
{
    // The one copy of the viewer's GLSL, used by both hosts. Desktop compiles it as GLSL 330 core
    // and the browser as GLSL ES 300 (WebGL2). The bodies are identical, and only the header differs.
    // Keep it inside the common subset of the two: no PolygonMode-dependent tricks, no
    // derivatives extensions, explicit precision.
    public static class Shaders
    {
        public static string Vertex(bool es)   => Header(es) + VertexBody;
        public static string Fragment(bool es) => Header(es) + FragmentBody;

        private static string Header(bool es) => es
            ? "#version 300 es\nprecision highp float;\nprecision highp int;\n"
            : "#version 330 core\n";

        // Camera space is X=forward, Y=right, Z=up, so clip w is the camera-space x (depth) and
        // depth is the standard GL perspective mapping applied to x. Keep in sync with
        // Frustum.ProjectNdc and Picker.RayThrough.
        private const string VertexBody = """
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec2 aUV;
            layout(location=2) in vec3 aNormal;
            layout(location=3) in vec4 aColor;
            uniform mat4  uView;
            uniform float uHalfTanY;
            uniform float uHalfTanZ;
            uniform float uNear;
            uniform float uFar;
            out vec2 vUV;
            out vec3 vNormal;
            out vec4 vColor;
            void main() {
                vec3 cam = (uView * vec4(aPos, 1.0)).xyz;
                float ndcZ = (uFar + uNear) / (uFar - uNear) * cam.x
                           - 2.0 * uFar * uNear / (uFar - uNear);
                gl_Position = vec4(cam.y / uHalfTanY, cam.z / uHalfTanZ, ndcZ, cam.x);
                vUV     = aUV;
                vNormal = mat3(uView) * aNormal;
                vColor  = aColor;
            }

            """;

        // A key light from over the viewer's left shoulder plus a sky/ground fill, both fixed to
        // the camera so the side you're looking at is always lit. That's the point of a viewer:
        // inspecting shape, not staging a scene. glTF UVs have their origin at the image's
        // top-left, which is also row 0 of the decoded image, so V is used as-is.
        // uAlphaMode: 0 opaque, 1 mask, 2 blend (AlphaMode's values).
        private const string FragmentBody = """
            in vec2 vUV;
            in vec3 vNormal;
            in vec4 vColor;
            uniform sampler2D uTex;
            uniform vec4  uBaseColor;
            uniform int   uAlphaMode;
            uniform float uAlphaCutoff;
            uniform int   uUnlit;
            out vec4 fragColor;
            void main() {
                vec4 c = texture(uTex, vUV) * uBaseColor * vColor;
                if (uAlphaMode == 1 && c.a < uAlphaCutoff) discard;
                if (uAlphaMode != 2) c.a = 1.0;
                if (uUnlit == 0) {
                    vec3 n = normalize(vNormal);
                    if (!gl_FrontFacing) n = -n;
                    vec3  keyDir = normalize(vec3(-1.0, -0.45, 0.75));
                    float key    = max(dot(n, keyDir), 0.0);
                    float sky    = 0.5 + 0.5 * n.z;
                    c.rgb *= 0.22 + 0.18 * sky + 0.75 * key;
                }
                fragColor = c;
            }

            """;
    }
}
