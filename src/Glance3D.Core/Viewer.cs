namespace Glance3D
{
    // One frame of host-agnostic input. Each host translates its own events into this: OpenTK's
    // mouse and keyboard state on the desktop, DOM pointer and key events in the browser. That
    // way what each button and key *does* is decided in exactly one place (Viewer.Update).
    public struct InputFrame
    {
        public float DragX, DragY;           // pointer movement since last frame, in pixels
        public bool  Left, Right, Middle;    // buttons held
        public bool  Shift;
        public float Wheel;                  // notches, positive = away from the user (zoom in)
        public float ViewportHeight;         // in the same pixel units as DragX/DragY

        public float MoveForward, MoveRight, MoveUp; // -1..1 (W/S, D/A, E/Q)
        public float TurnYaw, TurnPitch;              // -1..1 (arrow keys)

        public bool  Frame, Reset, ToggleMode, ToggleWireframe, ToggleGrid; // pressed this frame

        public bool  DoubleClick;            // a double-click happened this frame at...
        public float ClickNdcX, ClickNdcY;   // ...this point, in NDC ([-1, 1], y up)
    }

    // The whole viewer minus the GPU: the loaded model, the camera, and the display toggles.
    // A host owns one, feeds it an InputFrame every frame, and draws what RenderPlan says.
    public sealed class Viewer
    {
        public const float OrbitSensitivity = 0.006f; // rad per pixel of drag
        public const float TurnSpeed        = 1.8f;   // rad/s for the arrow keys

        public Camera Camera    { get; } = new();
        public Model? Model     { get; private set; }
        public Mesh?  Grid      { get; private set; }
        public bool   Wireframe { get; set; }
        public bool   ShowGrid  { get; set; } = true;

        public void SetModel(Model model, float aspect)
        {
            Model = model;
            Grid  = RenderPlan.BuildGrid(model);
            FrameModel(aspect);
        }

        public void FrameModel(float aspect)
        {
            if (Model != null) Camera.Frame(Model.Center, Model.Radius, aspect);
        }

        public void ResetView(float aspect)
        {
            Camera.ResetHeading();
            FrameModel(aspect);
        }

        public string ModeName => Camera.Mode == CameraMode.Orbit ? "orbit" : "fly";

        // Mouse: left drag turns (orbit / look), right or middle drag pans, Shift+left pans too
        // for trackpads. Double-click re-centers the orbit on the surface under the cursor.
        // Keyboard: WASD + Q/E, Shift for 4x, arrows turn. Movement speed scales with the model,
        // so a building and a ring both cross the screen in about the same time.
        public void Update(in InputFrame input, float dt, float aspect)
        {
            if (input.Frame)           FrameModel(aspect);
            if (input.Reset)           ResetView(aspect);
            if (input.ToggleWireframe) Wireframe = !Wireframe;
            if (input.ToggleGrid)      ShowGrid  = !ShowGrid;
            if (input.ToggleMode)      Camera.ToggleMode();

            bool panning = input.Right || input.Middle || (input.Shift && input.Left);
            bool moved   = input.DragX != 0f || input.DragY != 0f;
            if (panning && moved)
                Camera.Pan(input.DragX, input.DragY, input.ViewportHeight);
            else if (input.Left && moved)
                Camera.Turn(input.DragX * OrbitSensitivity, -input.DragY * OrbitSensitivity);

            if (input.Wheel != 0f) Camera.Dolly(input.Wheel);

            float speed = (Model?.Radius ?? 1f) * (input.Shift ? 4f : 1f) * dt;
            if (input.MoveForward != 0f || input.MoveRight != 0f || input.MoveUp != 0f)
                Camera.Move(input.MoveForward * speed, input.MoveRight * speed, input.MoveUp * speed);

            if (input.TurnYaw != 0f || input.TurnPitch != 0f)
                Camera.Turn(input.TurnYaw * TurnSpeed * dt, input.TurnPitch * TurnSpeed * dt);

            if (input.DoubleClick && Model != null)
            {
                var frustum     = RenderPlan.FrustumFor(Camera, Model, aspect);
                var (o, d)      = Picker.RayThrough(Camera, frustum, input.ClickNdcX, input.ClickNdcY);
                if (Picker.Raycast(Model, o, d) is Point hit) Camera.OrbitAround(hit); // background: keep the pivot
            }
        }
    }
}
