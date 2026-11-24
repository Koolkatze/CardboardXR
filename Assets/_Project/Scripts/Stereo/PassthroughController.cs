using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using CardboardXR.UI;
using UnityEngine.XR.ARFoundation;

namespace CardboardXR.Stereo
{
    // Camera passthrough in the viewer. Each new ARCore camera image is drawn with ARCore's own
    // background material into a texture, with its display transform widened so the texture holds
    // the whole camera image instead of the screen-shaped crop ARCore shows. A screen quad in front
    // of the AR camera shows it to both eyes at the angle the camera saw it, so the room appears at
    // true scale. The AR camera's pose driver moves it only with each new ARCore pose, which comes
    // with each new image, so the picture stays fixed to the room as the head turns and only its
    // edges trail. The camera sees less than a Cardboard eye, so at true scale the image is a window
    // with a dark border; by default it's enlarged about straight ahead until it fills the view
    // (FillZoom), at the price of the room looking that much bigger and virtual things lining up with
    // it only straight ahead, under the reticle. The "passthroughFill" launch option (0) keeps it at
    // true scale. The panel's Zoom button (shown while passthrough is on) steps a digital zoom on top
    // of that, 1x, 2x, 3x and 6x (kept between runs): only the part of the camera image around
    // straight ahead is drawn into the texture (ZoomWindow), at its full resolution, so zooming in
    // sharpens what it can. A new camera image comes only 30 times a second (60 on some phones), and
    // at true scale the room in it stays put between images by itself, as the screen holds the
    // image's pose while the eyes turn. Enlarged, it must turn faster than the head, so between images
    // the screen is turned on by the head's turn since the image was taken, from the gyroscope
    // (MagnifiedTurn), just before each frame is drawn; otherwise the room would move with the head
    // and then jump at every new image. Passthrough needs 6DoF, since ARCore must run, and hides the
    // virtual environment (every VirtualEnvironment, in whichever scene).
    // Switching to 3DoF turns it off until 6DoF is back. The "passthrough" launch option (1) starts
    // with it on. While it's on, the eyes move onto the camera (HeadPoseFusion.EyesAtCamera), so the
    // image is seen from where it was taken. The image is flat, so things in it only line up in both
    // eyes at its own distance; FocusDistance moves it to what the user is looking at, such as a
    // tracked hand. Anything else drawn keeps its own depth and fights that flat image, which is seen
    // as doubling, so while passthrough is on the rig's eyes move together (StereoRig.MonoEyes) and
    // the whole view becomes one flat, self-consistent window. In the single phone view the view also
    // takes the AR camera's pose itself, as an ordinary AR app does: the image then sits still on the
    // screen, at the price of the image's delay, which only matters in a viewer.
    public class PassthroughController : MonoBehaviour
    {
        static readonly int k_DisplayTransformId = Shader.PropertyToID("_UnityDisplayTransform");
        // The nearest the image comes, in meters.
        const float k_MinDistance = 0.2f;
        // How quickly the image follows a change of focus: most of the way in this many seconds.
        const float k_FocusSeconds = 0.25f;
        // The most the image is enlarged to fill the view, however narrow the camera.
        public const float k_MaxFill = 3f;
        // The Zoom button's steps, on top of filling the view.
        public static readonly int[] k_ZoomSteps = { 1, 2, 3, 6 };
        const string k_ZoomKey = "PassthroughZoom";

        [SerializeField] HeadPoseFusion m_Fusion;
        [Tooltip("The stereo rig: its eyes move together while passthrough is on, so nothing drawn has " +
                 "a depth the one flat image can't match.")]
        [SerializeField] StereoRig m_Rig;
        [Tooltip("The AR camera. Its ARCameraManager supplies the images and its TrackedPoseDriver places the screen.")]
        [SerializeField] Camera m_ArCamera;
        [Tooltip("A unit quad, a child of the AR camera, that shows the camera image.")]
        [SerializeField] Renderer m_Screen;
        [Tooltip("The panel's toggle, shown selected while passthrough is on.")]
        [SerializeField] M3Button m_Toggle;
        [Tooltip("The panel's Zoom button, shown while passthrough is on: steps through k_ZoomSteps.")]
        [SerializeField] M3Button m_ZoomButton;
        [Tooltip("How far in front of the camera the image is shown without a focus. It's one flat image, so everything in it appears at this depth.")]
        [SerializeField] float m_Distance = 2f;
        [Tooltip("Height of the passthrough texture in pixels; its width follows the camera image's shape.")]
        [SerializeField] int m_TextureHeight = 720;

        ARCameraManager m_CameraManager;
        TrackedPoseDriver m_PoseDriver;
        Material m_CameraMaterial;
        Mesh m_Quad;
        CommandBuffer m_Commands;
        RenderTexture m_Texture;
        bool m_On;
        // 3DoF turned passthrough off, so it comes back with 6DoF.
        bool m_ResumeWithSixDof;
        // ArDebugView drives the AR camera itself.
        bool m_ArViewActive;
        // The image's frustum at unit distance, in the AR camera's x and y, once an image arrived.
        Vector2 m_FrustumCenter;
        Vector2 m_FrustumSize;
        bool m_HasImage;
        bool m_Fill = true;
        int m_ZoomIndex;
        float m_LoggedFill;
        // Where the head tracker had the head when the shown image was taken, if known.
        readonly CameraClock m_Clock = new("[Passthrough]", "the head tracker's clock");
        Quaternion m_TrackerAtImage = Quaternion.identity;
        bool m_HasTrackerAtImage;
        // The screen's place for the window shown, set each frame: at unit distance, as tangents, and
        // how much it's enlarged.
        Vector2 m_ShownCenter;
        Vector2 m_ShownSize;
        float m_Magnification = 1f;
        // The part of the image the texture holds, in its screen UV, and the zoom it was drawn for.
        Vector2 m_WindowLo;
        Vector2 m_WindowHi = Vector2.one;
        float m_WindowZoom = 1f;
        float? m_FocusDistance;
        float m_ShownDistance;

        public bool IsOn => m_On;

        // Where the image should be, in meters from the camera, or null for m_Distance. It eases there.
        public float? FocusDistance
        {
            get => m_FocusDistance;
            set
            {
                if (value.HasValue != m_FocusDistance.HasValue && m_On)
                    Debug.Log(value.HasValue ? $"[Passthrough] Focus at {value.Value:F2} m" : $"[Passthrough] Focus back at {m_Distance:F1} m");
                m_FocusDistance = value;
            }
        }

        void Awake()
        {
            m_CameraManager = m_ArCamera.GetComponent<ARCameraManager>();
            m_PoseDriver = m_ArCamera.GetComponent<TrackedPoseDriver>();
            m_ShownDistance = m_Distance;
            m_Fill = LaunchOptions.GetInt("passthroughFill", 1) != 0;
            m_ZoomIndex = Mathf.Clamp(PlayerPrefs.GetInt(k_ZoomKey, 0), 0, k_ZoomSteps.Length - 1);
            ShowZoom();
            m_Fusion.PoseUpdated += PlaceScreen;
        }

        // The digital zoom now, on top of filling the view.
        public int Zoom => k_ZoomSteps[m_ZoomIndex];

        // The Zoom button: the next step, back to 1x after the last.
        public void NextZoom()
        {
            m_ZoomIndex = (m_ZoomIndex + 1) % k_ZoomSteps.Length;
            PlayerPrefs.SetInt(k_ZoomKey, m_ZoomIndex);
            PlayerPrefs.Save();
            ShowZoom();
            Debug.Log($"[Passthrough] Zoom {Zoom}x");
        }

        void ShowZoom()
        {
            if (m_ZoomButton == null)
                return;
            m_ZoomButton.Text = $"{Zoom}x";
            m_ZoomButton.gameObject.SetActive(m_On);
        }

        // How much the image is enlarged about straight ahead to cover the view: the camera's frustum
        // at unit distance (its size and middle, as tangents) against the view's reach (tangents left,
        // right, bottom, top, StereoRig.FieldOfViewTangents). Enlarging about straight ahead keeps
        // what's there, under the reticle, where it really is. At least 1, at most k_MaxFill.
        public static float FillZoom(Vector2 frustumSize, Vector2 frustumCenter, Vector4 view)
        {
            var half = frustumSize / 2f;
            if (half.x <= 0f || half.y <= 0f)
                return 1f;
            var across = Mathf.Max(Needed(view.x, half.x - frustumCenter.x), Needed(view.y, half.x + frustumCenter.x));
            var up = Mathf.Max(Needed(view.z, half.y - frustumCenter.y), Needed(view.w, half.y + frustumCenter.y));
            return Mathf.Clamp(Mathf.Max(across, up), 1f, k_MaxFill);

            // An edge this far from straight ahead reaching that far out.
            static float Needed(float reach, float edge) => edge > 0f ? reach / edge : float.PositiveInfinity;
        }

        // The part of the image a digital zoom shows, from lo to hi in its screen UV: 1/zoom of it each
        // way, around straight ahead (kept inside the image).
        public static void ZoomWindow(Vector2 frustumSize, Vector2 frustumCenter, float zoom, out Vector2 lo, out Vector2 hi)
        {
            var span = 1f / Mathf.Max(1f, zoom);
            var aheadX = frustumSize.x > 0f ? 0.5f - frustumCenter.x / frustumSize.x : 0.5f;
            var aheadY = frustumSize.y > 0f ? 0.5f - frustumCenter.y / frustumSize.y : 0.5f;
            lo = new Vector2(Mathf.Clamp(aheadX - span / 2f, 0f, 1f - span), Mathf.Clamp(aheadY - span / 2f, 0f, 1f - span));
            hi = lo + new Vector2(span, span);
        }

        // How to turn the screen, in the axes the image was taken in, for an image enlarged by
        // magnification about straight ahead when the head has turned by headTurn (in those axes)
        // since: the room in it must turn magnification times as far as the head, against the head,
        // and turning with the head's eyes already does it once. Nothing at true scale.
        public static Quaternion MagnifiedTurn(Quaternion headTurn, float magnification)
        {
            headTurn.ToAngleAxis(out var angle, out var axis);
            if (angle > 180f)
                angle -= 360f;
            if (Mathf.Abs(angle) < 1e-4f || !(axis.sqrMagnitude > 0.5f && axis.sqrMagnitude < 2f))
                return Quaternion.identity;
            return Quaternion.AngleAxis(angle * (1f - magnification), axis);
        }

        // Where the screen goes, at unit distance, to show the window lo to hi of the image enlarged
        // by magnification about straight ahead: its middle and size, as tangents.
        public static void ShownRect(Vector2 frustumSize, Vector2 frustumCenter, Vector2 lo, Vector2 hi, float magnification,
            out Vector2 center, out Vector2 size)
        {
            center = new Vector2(
                magnification * (frustumCenter.x + ((lo.x + hi.x) / 2f - 0.5f) * frustumSize.x),
                magnification * (frustumCenter.y + ((lo.y + hi.y) / 2f - 0.5f) * frustumSize.y));
            size = new Vector2(magnification * (hi.x - lo.x) * frustumSize.x, magnification * (hi.y - lo.y) * frustumSize.y);
        }

        // The display transform showing only the part of the widened image from lo to hi (in its screen
        // UV), stretched over the whole of screen UV 0 to 1: the middle of it, for a digital zoom.
        public static Matrix4x4 CropDisplayTransform(Matrix4x4 widened, Vector2 lo, Vector2 hi)
        {
            var c0 = new Vector2(widened.m00, widened.m01);
            var c1 = new Vector2(widened.m10, widened.m11);
            var c2 = new Vector2(widened.m20, widened.m21);
            var size = hi - lo;
            var cropped = widened;
            cropped.m00 = c0.x * size.x;
            cropped.m01 = c0.y * size.x;
            cropped.m10 = c1.x * size.y;
            cropped.m11 = c1.y * size.y;
            var offset = lo.x * c0 + lo.y * c1 + c2;
            cropped.m20 = offset.x;
            cropped.m21 = offset.y;
            return cropped;
        }

        // After the other components' OnEnable, so the mode set here sticks.
        void Start()
        {
            m_ArViewActive = LaunchOptions.GetInt("arView", 0) != 0;
            if (LaunchOptions.GetInt("passthrough", 0) != 0)
                SetPassthrough(true);
        }

        void OnDestroy()
        {
            if (m_Fusion != null)
                m_Fusion.PoseUpdated -= PlaceScreen;
            if (m_On)
                m_CameraManager.frameReceived -= OnCameraFrame;
            if (m_Texture != null)
            {
                m_Texture.Release();
                Destroy(m_Texture);
            }
            if (m_CameraMaterial != null)
                Destroy(m_CameraMaterial);
            if (m_Quad != null)
                Destroy(m_Quad);
            m_Commands?.Release();
        }

        public void Toggle() => Choose(!m_On);

        // Turns it on or off as the user's own choice, so 3DoF turning it off doesn't bring it back
        // with 6DoF later.
        public void Choose(bool on)
        {
            m_ResumeWithSixDof = false;
            SetPassthrough(on);
        }

        public void SetPassthrough(bool on)
        {
            if (m_ArViewActive || on == m_On)
                return;

            m_On = on;
            if (on)
            {
                m_Fusion.SetMode(HeadPoseFusion.Mode.SixDof);
                m_CameraManager.frameReceived += OnCameraFrame;
            }
            else
            {
                m_CameraManager.frameReceived -= OnCameraFrame;
            }

            m_PoseDriver.enabled = on;
            m_Fusion.EyesAtCamera = on;
            if (m_Rig != null)
                m_Rig.MonoEyes = on;
            // Shown once the first image has arrived.
            m_Screen.enabled = on && m_HasImage;
            VirtualEnvironment.SetAllHidden(on);
            if (m_Toggle != null)
                m_Toggle.Selected = on;
            ShowZoom();
            Debug.Log(on ? $"[Passthrough] On, {(m_Fill ? "filling the view" : "at true scale")}, zoom {Zoom}x" : "[Passthrough] Off");
        }

        void OnCameraFrame(ARCameraFrameEventArgs args)
        {
            var source = m_CameraManager.cameraMaterial;
            if (source == null || args.textures == null || args.textures.Count == 0 ||
                !args.displayMatrix.HasValue || !args.projectionMatrix.HasValue)
                return;
            // Always the whole camera image, in both views. Mono once cropped it to ARCore's
            // screen-shaped rectangle so it filled the screen, as an ordinary AR app does, but seeing
            // everything the camera saw was better even at the price of a border around it.
            if (!TryWidenDisplayTransform(args.displayMatrix.Value, out var widened, out var min, out var max))
                return;

            if (m_CameraMaterial == null)
            {
                m_CameraMaterial = new Material(source) { name = "Passthrough Camera" };
                EnableRenderPipelineKeyword();
            }
            for (var i = 0; i < args.textures.Count; i++)
                m_CameraMaterial.SetTexture(args.propertyNameIds[i], args.textures[i]);

            // The head's rotation when the image was taken, for turning it on between images.
            m_HasTrackerAtImage = false;
            var tracker = m_Fusion.Tracker;
            if (args.timestampNs is long cameraNs && tracker != null &&
                m_Clock.TryToTrackerClock(cameraNs, tracker.NowNs, CameraClock.SystemNowNs(), out var trackerNs))
                m_HasTrackerAtImage = tracker.TryGetRotationAt(trackerNs, out m_TrackerAtImage);

            // Screen UV maps to NDC as 2uv - 1, which ARCore's projection maps to camera space at unit
            // distance as (ndc + p02) / p00 across and (ndc + p12) / p11 up.
            var size = max - min;
            var projection = args.projectionMatrix.Value;
            m_FrustumSize = new Vector2(2f * size.x / projection.m00, 2f * size.y / projection.m11);
            m_FrustumCenter = new Vector2(
                (min.x + max.x - 1f + projection.m02) / projection.m00,
                (min.y + max.y - 1f + projection.m12) / projection.m11);

            // Zoomed, only the window around straight ahead is drawn, filling the texture: the zoom
            // costs no texture resolution. LateUpdate places the screen for the window drawn here.
            m_WindowZoom = Zoom;
            ZoomWindow(m_FrustumSize, m_FrustumCenter, m_WindowZoom, out m_WindowLo, out m_WindowHi);
            m_CameraMaterial.SetMatrix(k_DisplayTransformId,
                m_WindowZoom > 1f ? CropDisplayTransform(widened, m_WindowLo, m_WindowHi) : widened);

            EnsureTexture(Screen.width * size.x / (Screen.height * size.y));
            DrawCameraImage();
            if (m_HasImage)
                return;

            m_HasImage = true;
            m_Screen.enabled = m_On;
            Debug.Log($"[Passthrough] Camera image {m_Texture.width}x{m_Texture.height}, " +
                      $"{2f * Mathf.Atan(m_FrustumSize.x / 2f) * Mathf.Rad2Deg:F0} x " +
                      $"{2f * Mathf.Atan(m_FrustumSize.y / 2f) * Mathf.Rad2Deg:F0} deg");
            LogCameraMaterial(args);
        }

        // ARCore's background shader maps screen UV to camera-texture UV as
        // tex = u * (m00, m01) + v * (m10, m11) + (m20, m21), and only screen UV 0 to 1 is drawn: a
        // crop of the camera image shaped like the screen. This finds the screen-UV rectangle the
        // whole texture covers, from min to max, and widens the transform so that screen UV 0 to 1
        // covers all of it.
        public static bool TryWidenDisplayTransform(Matrix4x4 display, out Matrix4x4 widened, out Vector2 min, out Vector2 max)
        {
            widened = display;
            min = max = Vector2.zero;
            var c0 = new Vector2(display.m00, display.m01);
            var c1 = new Vector2(display.m10, display.m11);
            var c2 = new Vector2(display.m20, display.m21);
            var determinant = c0.x * c1.y - c1.x * c0.y;
            if (Mathf.Abs(determinant) < 1e-6f)
                return false;

            Vector2 ScreenUv(Vector2 texture)
            {
                var t = texture - c2;
                return new Vector2((c1.y * t.x - c1.x * t.y) / determinant, (c0.x * t.y - c0.y * t.x) / determinant);
            }

            var a = ScreenUv(new Vector2(0f, 0f));
            var b = ScreenUv(new Vector2(1f, 0f));
            var c = ScreenUv(new Vector2(0f, 1f));
            var d = ScreenUv(new Vector2(1f, 1f));
            min = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d));
            max = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d));
            var size = max - min;
            widened.m00 = c0.x * size.x;
            widened.m01 = c0.y * size.x;
            widened.m10 = c1.x * size.y;
            widened.m11 = c1.y * size.y;
            var offset = min.x * c0 + min.y * c1 + c2;
            widened.m20 = offset.x;
            widened.m21 = offset.y;
            return true;
        }

        void EnsureTexture(float aspect)
        {
            var height = Mathf.Max(16, m_TextureHeight);
            var width = Mathf.Max(16, Mathf.RoundToInt(height * aspect));
            if (m_Texture != null && m_Texture.width == width && m_Texture.height == height)
                return;
            if (m_Texture != null)
            {
                m_Texture.Release();
                Destroy(m_Texture);
            }
            // With a depth buffer, because both providers' background shaders declare ZWrite On and
            // write SV_Depth. Metal drops the draw when the target has no depth to write to, which
            // left the texture on the clear colour; GLES on Android tolerated it, so this only ever
            // showed up on the iPhone, as a black passthrough image with nothing logged.
            m_Texture = new RenderTexture(width, height, 24) { name = "Passthrough" };
            m_Screen.material.mainTexture = m_Texture;
        }

        // Reads one pixel back from the middle of the passthrough texture, once. A black quad in the
        // viewer has two quite different causes, and the frame rate and logs look identical for both:
        // either the camera image never reaches this texture, or it does and the screen that shows it
        // is what fails. This says which half of the pipeline to look at.
        Color SampleTextureCentre()
        {
            var previous = RenderTexture.active;
            var probe = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = m_Texture;
                probe.ReadPixels(new Rect(m_Texture.width / 2f, m_Texture.height / 2f, 1f, 1f), 0, 0);
                probe.Apply();
                return probe.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = previous;
                Destroy(probe);
            }
        }

        // ARKit ships one background shader for both render pipelines and chooses between them with a
        // keyword. Without it the shader takes its built-in-pipeline branch, which draws black in a
        // URP project: the image arrives, the draw succeeds, and nothing reports a failure. AR
        // Foundation sets this on its own background renderer feature, which we bypass by drawing the
        // camera image ourselves. ARCore needs nothing here, because it ships a shader per pipeline,
        // so this asks the shader whether it has the keyword rather than assuming a platform.
        void EnableRenderPipelineKeyword()
        {
            if (GraphicsSettings.currentRenderPipeline == null)
                return;

            // Looked up rather than constructed: constructing one the shader lacks, as ARCore's does,
            // logs an error on Android.
            var keyword = m_CameraMaterial.shader.keywordSpace.FindKeyword("ARKIT_BACKGROUND_URP");
            if (keyword.isValid)
                m_CameraMaterial.EnableKeyword(keyword);
        }

        // What the provider actually handed us, logged once. The two platforms differ here more than
        // anywhere else: ARCore gives one RGB texture, ARKit two (luma and chroma) that its own shader
        // converts, and a variant of that shader that didn't survive into the build draws black
        // without failing. Shader, keywords and textures together say which of those is the case.
        void LogCameraMaterial(ARCameraFrameEventArgs args)
        {
            var shader = m_CameraMaterial.shader;
            var keywords = m_CameraMaterial.shaderKeywords;
            var textures = new string[args.textures.Count];
            for (var i = 0; i < args.textures.Count; i++)
            {
                var texture = args.textures[i];
                textures[i] = texture == null
                    ? "null"
                    : $"{texture.width}x{texture.height} {texture.graphicsFormat}";
            }

            Debug.Log($"[Passthrough] Shader {shader.name}, supported {shader.isSupported}, " +
                      $"passes {m_CameraMaterial.passCount}, " +
                      $"keywords {(keywords.Length == 0 ? "none" : string.Join(" ", keywords))}, " +
                      $"{args.textures.Count} textures: {string.Join(", ", textures)}");
            Debug.Log($"[Passthrough] Texture centre reads {SampleTextureCentre()}, " +
                      $"display transform {m_CameraMaterial.GetMatrix(k_DisplayTransformId).GetRow(0)} " +
                      $"{m_CameraMaterial.GetMatrix(k_DisplayTransformId).GetRow(1)}");
        }

        // Draws the camera image into the texture the way ARCameraBackground draws the screen: a unit
        // quad through an orthographic projection.
        void DrawCameraImage()
        {
            if (m_Quad == null)
                m_Quad = CreateUnitQuad();
            m_Commands ??= new CommandBuffer { name = "Passthrough Camera Image" };
            m_Commands.Clear();
            m_Commands.SetRenderTarget(m_Texture);
            m_Commands.ClearRenderTarget(false, true, Color.black);
            m_Commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, 1f, 0f, 1f, -0.1f, 9.9f));
            m_Commands.DrawMesh(m_Quad, Matrix4x4.identity, m_CameraMaterial);
            Graphics.ExecuteCommandBuffer(m_Commands);
        }

        static Mesh CreateUnitQuad()
        {
            var mesh = new Mesh { name = "Passthrough Quad" };
            mesh.SetVertices(new[]
            {
                new Vector3(0f, 0f, 0.1f), new Vector3(0f, 1f, 0.1f), new Vector3(1f, 1f, 0.1f), new Vector3(1f, 0f, 0.1f),
            });
            mesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            return mesh;
        }

        void LateUpdate()
        {
            // In the single phone view the view takes the camera's own pose, the one the image was taken
            // from, so the image sits still on the screen instead of shaking between the fused head
            // pose and each frame's (HeadPoseFusion.FollowPose). In the viewer it stays fused.
            var follow = m_On && m_Rig != null && m_Rig.IsMono ? m_ArCamera.transform : null;
            if (m_Fusion.FollowPose != follow)
            {
                m_Fusion.FollowPose = follow;
                Debug.Log(follow != null ? "[Passthrough] Phone view: the view follows the camera's pose" :
                    "[Passthrough] The view follows the fused head pose");
            }
            var sixDof = m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof;
            if (!m_On)
            {
                if (m_ResumeWithSixDof && sixDof)
                {
                    m_ResumeWithSixDof = false;
                    SetPassthrough(true);
                }
                return;
            }
            // Switching to 3DoF turns ARCore, and so the camera, off.
            if (!sixDof)
            {
                SetPassthrough(false);
                m_ResumeWithSixDof = true;
                return;
            }
            if (!m_HasImage)
                return;

            // Eased in proportion, so moving from 2 m to 0.3 m takes as long as 0.3 m to 0.045 m would.
            var target = Mathf.Clamp(m_FocusDistance ?? m_Distance, k_MinDistance, m_Distance);
            var step = 1f - Mathf.Exp(-Time.unscaledDeltaTime / k_FocusSeconds);
            m_ShownDistance = Mathf.Exp(Mathf.Lerp(Mathf.Log(m_ShownDistance), Mathf.Log(target), step));
            var d = m_ShownDistance;
            // The window drawn (OnCameraFrame) enlarged by fill x zoom about straight ahead: as large as
            // the whole image enlarged to fill, whatever the zoom.
            var fill = m_Fill && m_Rig != null ? FillZoom(m_FrustumSize, m_FrustumCenter, m_Rig.FieldOfViewTangents) : 1f;
            if (Mathf.Abs(fill - m_LoggedFill) > 0.01f)
            {
                m_LoggedFill = fill;
                Debug.Log($"[Passthrough] Enlarged {fill:F2}x to fill the view");
            }
            m_Magnification = fill * m_WindowZoom;
            ShownRect(m_FrustumSize, m_FrustumCenter, m_WindowLo, m_WindowHi, m_Magnification, out m_ShownCenter, out m_ShownSize);
            PlaceScreen();
        }

        // Places the screen for the window shown, turned on by the head's turn since its image when
        // it's enlarged. Each frame, and again whenever the head moves, the last time just before
        // drawing (HeadPoseFusion.PoseUpdated).
        void PlaceScreen()
        {
            if (!m_On || !m_HasImage)
                return;
            var turn = Quaternion.identity;
            // The phone view follows the camera's own pose, so the head never turns from the image's.
            if (m_Magnification > 1.0001f && m_HasTrackerAtImage && m_Fusion.FollowPose == null)
                turn = MagnifiedTurn(Quaternion.Inverse(m_TrackerAtImage) * m_Fusion.TrackerRotation, m_Magnification);
            var d = m_ShownDistance;
            var screen = m_Screen.transform;
            screen.SetLocalPositionAndRotation(turn * new Vector3(m_ShownCenter.x * d, m_ShownCenter.y * d, d), turn);
            screen.localScale = new Vector3(m_ShownSize.x * d, m_ShownSize.y * d, 1f);
        }
    }
}
