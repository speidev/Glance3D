using System.Globalization;

namespace Glance3D
{
    public static class Program
    {
        private const string Usage = """
            glance3d - a tiny glTF/GLB model viewer

            usage: glance3d [model.glb|model.gltf] [options]

              --size WxH         window (or capture) size, default 1280x720
              --view YAW,PITCH   initial heading in degrees, default -120,-20
              --no-grid          start with the floor grid hidden
              --capture out.png  render one frame offscreen, write it, and exit

            mouse   left drag: orbit / look   right or middle drag (Shift+left): pan
                    wheel: zoom   double-click: orbit around the point under the cursor
            keys    W A S D move   Q E down / up   Shift faster   arrows turn
                    Tab orbit/fly   F frame   R reset view   Z wireframe   G grid
                    F11 fullscreen   Esc quit
            You can also drop a .glb/.gltf onto the window.
            """;

        public static int Main(string[] args)
        {
            if (!TryParse(args, out var opts, out var error))
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine();
                Console.Error.WriteLine(Usage);
                return 2;
            }
            if (opts == null)
            {
                Console.WriteLine(Usage);
                return 0;
            }

            using var window = new ViewerWindow(opts);
            window.Run();
            return 0;
        }

        // Returns false with an error for bad input, true with opts == null for --help.
        internal static bool TryParse(string[] args, out ViewerOptions? opts, out string error)
        {
            opts  = new ViewerOptions();
            error = "";

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string? Next() => i + 1 < args.Length ? args[++i] : null;

                switch (a)
                {
                    case "-h" or "--help":
                        opts = null;
                        return true;

                    case "--no-grid":
                        opts.ShowGrid = false;
                        break;

                    case "--size":
                        var size = Next()?.Split('x', 'X');
                        if (size is not [var sw, var sh] || !int.TryParse(sw, out opts.Width) || !int.TryParse(sh, out opts.Height)
                            || opts.Width < 16 || opts.Height < 16)
                        {
                            error = "--size expects WIDTHxHEIGHT, e.g. 1920x1080";
                            return false;
                        }
                        break;

                    case "--view":
                        var view = Next()?.Split(',');
                        if (view is not [var vy, var vp]
                            || !float.TryParse(vy, NumberStyles.Float, CultureInfo.InvariantCulture, out float yaw)
                            || !float.TryParse(vp, NumberStyles.Float, CultureInfo.InvariantCulture, out float pitch))
                        {
                            error = "--view expects YAW,PITCH in degrees, e.g. -120,-20";
                            return false;
                        }
                        opts.YawDeg   = yaw;
                        opts.PitchDeg = pitch;
                        break;

                    case "--capture":
                        opts.CapturePath = Next();
                        if (opts.CapturePath == null) { error = "--capture expects an output .png path"; return false; }
                        break;

                    default:
                        if (a.StartsWith('-')) { error = $"unknown option {a}"; return false; }
                        if (opts.ModelPath != null) { error = "only one model can be opened at a time"; return false; }
                        if (!File.Exists(a)) { error = $"file not found: {a}"; return false; }
                        opts.ModelPath = a;
                        break;
                }
            }

            if (opts.CapturePath != null && opts.ModelPath == null)
            {
                error = "--capture needs a model to render";
                return false;
            }
            return true;
        }
    }
}
