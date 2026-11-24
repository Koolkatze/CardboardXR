using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace CardboardXR.Stereo
{
    // The phone's rear cameras in 3DoF, opened directly with Camera2 (CardboardXRCamera.java), with
    // no AR session: for the passthrough view (PassthroughController) and the pinch sensor
    // (PinchSensor), which share the one stream. Any real rear lens can be chosen (Lenses, NextLens):
    // on a phone whose main camera is a logical multi-camera, the ultra-wide and telephoto lenses are
    // reached through its zoom ratio, as the phone's own camera app does; rear cameras listed on their
    // own are opened directly. The choice is kept between runs. Each image arrives as luma and chroma
    // planes, uploaded straight from the Java side's buffers, and is drawn (CameraYuv.shader) into
    // Image, an RGB texture standing upright on the landscape screen, with its field of view
    // (ImageTangents) and when it was taken (ImageTimestampNs, for turning it with the head between
    // images). It runs while anything has Acquired it, only in 3DoF: just before 6DoF starts it lets
    // go of the camera, which the AR session then takes, and it waits k_HandoffSeconds after 6DoF ends
    // before opening it again. The "cameraWidth" and "cameraFps" launch options set the stream's size
    // and rate (1280 wide, 30 fps). Logs [Camera].
    public class PhoneCamera : MonoBehaviour
    {
        public struct Lens
        {
            public string CameraId;
            // The camera's CONTROL_ZOOM_RATIO for this lens; 1 for a camera opened on its own.
            public float ZoomRatio;
            // Against the main lens: 0.5 for an ultra-wide that sees twice as wide, 5 for a 5x telephoto.
            public float Magnification;
            // Half the field of view as tangents, along the sensor's long and short sides.
            public Vector2 Tangents;

            public string Label => LensLabel(Magnification);
        }

        const string k_JavaClass = "com.brenpoly.cardboardxr.CardboardXRCamera";
        const string k_LensKey = "PhoneCameraLens";
        const float k_HandoffSeconds = 1f;
        const float k_NoImageSeconds = 3f;
        const float k_RetrySeconds = 3f;
        const float k_StatusSeconds = 5f;
        const int k_Streaming = 2;
        const int k_Failed = 3;

        static readonly int k_LumaId = Shader.PropertyToID("_LumaTex");
        static readonly int k_ChromaId = Shader.PropertyToID("_ChromaTex");
        static readonly int k_RowXId = Shader.PropertyToID("_ImageFromScreenX");
        static readonly int k_RowYId = Shader.PropertyToID("_ImageFromScreenY");
        static readonly int k_LinearizeId = Shader.PropertyToID("_Linearize");

        [SerializeField] HeadPoseFusion m_Fusion;
        [Tooltip("CardboardXR/CameraYuv: the camera's planes to RGB, turned upright.")]
        [SerializeField] Shader m_Shader;
        [SerializeField] int m_Width = 1280;
        [SerializeField] int m_Height = 720;
        [SerializeField] int m_Fps = 30;

        public static PhoneCamera Instance { get; private set; }

        readonly HashSet<object> m_Users = new();
        readonly List<Lens> m_Lenses = new();
        readonly CameraClock m_Clock = new("[Camera]", "the head tracker's clock");
        int m_LensIndex;
        bool m_ListedLenses;
        bool m_Open;
        string m_OpenCameraId;
        float m_OpenZoomRatio;
        float m_ThreeDofSince;
        float m_RetryAt;
        bool m_AskedPermission;
        bool m_LoggedFailure;

        int m_Generation = -1;
        int m_FrameWidth, m_FrameHeight, m_Rotation;
        Texture2D m_Luma;
        Texture2D m_Chroma;
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject[] m_Buffers;
        NativeArray<byte>[] m_Frames;
#endif
        Material m_Material;
        MaterialPropertyBlock m_Properties;
        CommandBuffer m_Commands;
        Mesh m_Quad;
        RenderTexture m_Image;
        float m_LastImageTime;

        // Statistics for the status line.
        int m_Uploads;
        double m_UploadMs;
        float m_StatusStart;

        // Upright on the screen, in RGB; null until the first image.
        public RenderTexture Image => m_Image;
        public bool HasImage => ImageVersion > 0;
        // Half the field of view of Image as tangents, across and up.
        public Vector2 ImageTangents { get; private set; }
        // When Image's picture was taken, on the camera's clock; and a count that goes up with each.
        public long ImageTimestampNs { get; private set; }
        public int ImageVersion { get; private set; }
        public bool IsStreaming => m_Open && HasImage;

        public IReadOnlyList<Lens> Lenses => m_Lenses;
        public Lens CurrentLens => m_Lenses.Count > 0 ? m_Lenses[m_LensIndex] : default;
        public event Action LensChanged;

        void Awake()
        {
            Instance = this;
            m_Width = Mathf.Clamp(LaunchOptions.GetInt("cameraWidth", m_Width), 320, 3840);
            m_Height = Mathf.RoundToInt(m_Width * 9f / 16f);
            m_Fps = Mathf.Clamp(LaunchOptions.GetInt("cameraFps", m_Fps), 10, 60);
            m_StatusStart = Time.unscaledTime;
        }

        void OnEnable()
        {
            if (m_Fusion != null)
                m_Fusion.ModeChanging += OnModeChanging;
            m_ThreeDofSince = Time.unscaledTime - k_HandoffSeconds;
        }

        void OnDisable()
        {
            if (m_Fusion != null)
                m_Fusion.ModeChanging -= OnModeChanging;
            Close("disabled");
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (m_Image != null)
            {
                m_Image.Release();
                Destroy(m_Image);
            }
            if (m_Luma != null)
                Destroy(m_Luma);
            if (m_Chroma != null)
                Destroy(m_Chroma);
            if (m_Material != null)
                Destroy(m_Material);
            if (m_Quad != null)
                Destroy(m_Quad);
            m_Commands?.Release();
        }

        // Something needs the camera (the passthrough view, the pinch sensor): it runs while anything does.
        public void Acquire(object user) => m_Users.Add(user);

        public void Release(object user)
        {
            m_Users.Remove(user);
            if (m_Users.Count == 0)
                Close("nothing needs it");
        }

        // The panel's Camera button: the next lens, from the widest, round again after the narrowest.
        public void NextLens()
        {
            ListLenses();
            if (m_Lenses.Count < 2)
                return;
            m_LensIndex = (m_LensIndex + 1) % m_Lenses.Count;
            PlayerPrefs.SetString(k_LensKey, CurrentLens.Label);
            PlayerPrefs.Save();
            Debug.Log($"[Camera] Lens {CurrentLens.Label} (camera {CurrentLens.CameraId}, zoom ratio {CurrentLens.ZoomRatio:F2})");
            LensChanged?.Invoke();
        }

        void OnModeChanging(HeadPoseFusion.Mode next)
        {
            if (next == HeadPoseFusion.Mode.SixDof)
                Close("6DoF wants the camera");
            else
                m_ThreeDofSince = Time.unscaledTime;
        }

        void Update()
        {
            var threeDof = m_Fusion == null || m_Fusion.CurrentMode == HeadPoseFusion.Mode.ThreeDof;
            if (m_Users.Count == 0 || !threeDof)
            {
                Close(threeDof ? "nothing needs it" : "6DoF has the camera");
                return;
            }
            if (Time.unscaledTime - m_ThreeDofSince < k_HandoffSeconds || Time.unscaledTime < m_RetryAt)
                return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!HasPermission())
                return;
            ListLenses();
            if (m_Lenses.Count == 0)
            {
                m_RetryAt = Time.unscaledTime + k_RetrySeconds;
                return;
            }
            var lens = CurrentLens;
            if (!m_Open || lens.CameraId != m_OpenCameraId || !Mathf.Approximately(lens.ZoomRatio, m_OpenZoomRatio))
                Open(lens);
            var status = Java.CallStatic<int>("status");
            if (status == k_Failed)
            {
                if (!m_LoggedFailure)
                {
                    m_LoggedFailure = true;
                    Debug.LogWarning($"[Camera] Couldn't open the camera: {Java.CallStatic<string>("error")}; trying again");
                }
                Close("it failed");
                m_RetryAt = Time.unscaledTime + k_RetrySeconds;
                return;
            }
            if (status == k_Streaming && Time.unscaledTime - m_LastImageTime > k_NoImageSeconds)
            {
                Debug.Log($"[Camera] No images for {k_NoImageSeconds:F0} s: opening the camera again");
                Close("it went quiet");
                return;
            }
            TakeImage();
            LogStatus();
#endif
        }

        void Open(Lens lens)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                Java.CallStatic("open", activity, lens.CameraId, lens.ZoomRatio, m_Width, m_Height, m_Fps);
                if (!m_Open)
                    m_LastImageTime = Time.unscaledTime;
                m_Open = true;
                m_OpenCameraId = lens.CameraId;
                m_OpenZoomRatio = lens.ZoomRatio;
                m_LoggedFailure = false;
                Debug.Log($"[Camera] Opening lens {lens.Label} (camera {lens.CameraId}, zoom ratio {lens.ZoomRatio:F2}) " +
                          $"at about {m_Width}x{m_Height}, {m_Fps} fps");
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"[Camera] Couldn't open the camera: {e.Message}");
                m_RetryAt = Time.unscaledTime + k_RetrySeconds;
            }
#endif
        }

        void Close(string why)
        {
            if (!m_Open)
                return;
            m_Open = false;
            m_OpenCameraId = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                Java.CallStatic("close");
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"[Camera] Couldn't close the camera: {e.Message}");
            }
            DisposeBuffers();
#endif
            m_Generation = -1;
            Debug.Log($"[Camera] Closed: {why}");
        }

        void ListLenses()
        {
            if (m_ListedLenses)
                return;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                m_Lenses.Clear();
                m_Lenses.AddRange(ParseLenses(Java.CallStatic<string>("lenses", activity)));
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"[Camera] Couldn't list the cameras: {e.Message}");
                return;
            }
#endif
            m_ListedLenses = true;
            var saved = PlayerPrefs.GetString(k_LensKey, LensLabel(1f));
            m_LensIndex = Mathf.Max(0, m_Lenses.FindIndex(l => l.Label == saved));
            if (m_Lenses.Count > 0 && m_Lenses[m_LensIndex].Label != saved)
                m_LensIndex = Mathf.Max(0, m_Lenses.FindIndex(l => Mathf.Approximately(l.Magnification, 1f)));
            var list = string.Join(", ", m_Lenses.ConvertAll(l => $"{l.Label} (camera {l.CameraId}, zoom ratio {l.ZoomRatio:F2}, " +
                $"{2f * Mathf.Atan(l.Tangents.x) * Mathf.Rad2Deg:F0} deg across)"));
            Debug.Log(m_Lenses.Count > 0 ? $"[Camera] Rear lenses: {list}; using {CurrentLens.Label}" : "[Camera] No rear camera found");
            if (m_Lenses.Count > 0)
                LensChanged?.Invoke();
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        bool HasPermission()
        {
            if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
                return true;
            if (!m_AskedPermission)
            {
                m_AskedPermission = true;
                Debug.Log("[Camera] Asking for the camera permission");
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera);
            }
            return false;
        }

        // The newest image, if there's one Unity hasn't had: its planes into the textures, then drawn
        // upright into Image.
        void TakeImage()
        {
            try
            {
                var generation = Java.CallStatic<int>("frameGeneration");
                if (generation != m_Generation && !Rebind(generation))
                    return;
                var index = Java.CallStatic<int>("lockFrame", generation);
                if (index < 0)
                    return;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                long timestamp;
                try
                {
                    var frame = m_Frames[index];
                    var lumaSize = m_FrameWidth * m_FrameHeight;
                    m_Luma.LoadRawTextureData(frame.GetSubArray(0, lumaSize));
                    m_Luma.Apply(false);
                    m_Chroma.LoadRawTextureData(frame.GetSubArray(lumaSize, lumaSize / 2));
                    m_Chroma.Apply(false);
                    timestamp = Java.CallStatic<long>("frameTimestamp", index);
                }
                finally
                {
                    Java.CallStatic("unlockFrame");
                }
                var tangents = new Vector2(Java.CallStatic<float>("frameTanAcross"), Java.CallStatic<float>("frameTanUp"));
                ImageTangents = UprightTangents(tangents, m_Rotation);
                EnsureImage();
                DrawTo(m_Image, forModels: false);
                ImageTimestampNs = timestamp;
                ImageVersion++;
                m_LastImageTime = Time.unscaledTime;
                m_Uploads++;
                m_UploadMs += stopwatch.Elapsed.TotalMilliseconds;
                if (ImageVersion == 1)
                    Debug.Log($"[Camera] First image: {m_FrameWidth}x{m_FrameHeight}, turned {m_Rotation} deg to stand upright, " +
                              $"{2f * Mathf.Atan(ImageTangents.x) * Mathf.Rad2Deg:F0} x {2f * Mathf.Atan(ImageTangents.y) * Mathf.Rad2Deg:F0} deg, " +
                              $"timestamps {(Java.CallStatic<int>("timestampSource") == 1 ? "on the boot clock" : "of unknown base")}");
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"[Camera] Couldn't take an image: {e.Message}");
            }
        }

        // The Java side's buffers, made anew for a new size: their memory, read in place, and the
        // planes' textures of that size.
        bool Rebind(int generation)
        {
            var width = Java.CallStatic<int>("frameWidth");
            var height = Java.CallStatic<int>("frameHeight");
            if (width <= 0 || height <= 0)
                return false;
            DisposeBuffers();
            m_Buffers = new AndroidJavaObject[3];
            m_Frames = new NativeArray<byte>[3];
            for (var i = 0; i < m_Buffers.Length; i++)
            {
                m_Buffers[i] = Java.CallStatic<AndroidJavaObject>("frameBuffer", i);
                if (m_Buffers[i] == null)
                    return false;
                m_Frames[i] = AndroidJNI.GetDirectByteBuffer(m_Buffers[i].GetRawObject());
            }
            m_FrameWidth = width;
            m_FrameHeight = height;
            m_Rotation = Java.CallStatic<int>("frameRotation");
            if (m_Luma == null || m_Luma.width != width || m_Luma.height != height)
            {
                if (m_Luma != null)
                    Destroy(m_Luma);
                if (m_Chroma != null)
                    Destroy(m_Chroma);
                m_Luma = new Texture2D(width, height, TextureFormat.R8, false, true)
                {
                    name = "Camera Luma", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                };
                m_Chroma = new Texture2D(width / 2, height / 2, TextureFormat.RG16, false, true)
                {
                    name = "Camera Chroma", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                };
            }
            m_Generation = generation;
            return true;
        }

        // The buffers' memory stays the Java side's; only the references to them are let go.
        void DisposeBuffers()
        {
            if (m_Buffers == null)
                return;
            foreach (var buffer in m_Buffers)
                buffer?.Dispose();
            m_Buffers = null;
            m_Frames = null;
        }

        void LogStatus()
        {
            var seconds = Time.unscaledTime - m_StatusStart;
            if (seconds < k_StatusSeconds)
                return;
            var counts = Java.CallStatic<string>("takeCounts").Split(';');
            Debug.Log($"[Camera] Lens {CurrentLens.Label}, {m_FrameWidth}x{m_FrameHeight}: {m_Uploads / seconds:F1} images/s shown " +
                      $"({m_UploadMs / Mathf.Max(1, m_Uploads):F1} ms each on the main thread), " +
                      $"{(counts.Length > 0 ? counts[0] : "?")} copied, {(counts.Length > 1 ? counts[1] : "?")} dropped");
            m_Uploads = 0;
            m_UploadMs = 0;
            m_StatusStart = Time.unscaledTime;
        }

        static AndroidJavaClass s_Java;
        static AndroidJavaClass Java => s_Java ??= new AndroidJavaClass(k_JavaClass);
#endif

        // Image's size: the stream's, turned to stand upright.
        void EnsureImage()
        {
            var turned = m_Rotation % 180 != 0;
            var width = turned ? m_FrameHeight : m_FrameWidth;
            var height = turned ? m_FrameWidth : m_FrameHeight;
            if (m_Image != null && m_Image.width == width && m_Image.height == height)
                return;
            if (m_Image != null)
            {
                m_Image.Release();
                Destroy(m_Image);
            }
            m_Image = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "Phone Camera", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
            };
            m_Image.Create();
        }

        // Draws the latest image upright into target, scaled to fit it. For the screen (forModels
        // false) the target is sRGB and the colors are linearized for it; for the hand models
        // (forModels true) the target is plain RGBA and the colors stay as the camera gave them.
        public bool DrawTo(RenderTexture target, bool forModels)
        {
            if (m_Luma == null || m_Chroma == null || target == null || m_Shader == null)
                return false;
            if (m_Material == null)
                m_Material = new Material(m_Shader) { name = "Phone Camera" };
            m_Properties ??= new MaterialPropertyBlock();
            if (m_Quad == null)
                m_Quad = CreateUnitQuad();
            ImageFromScreen(m_Rotation, out var rowX, out var rowY);
            m_Properties.SetTexture(k_LumaId, m_Luma);
            m_Properties.SetTexture(k_ChromaId, m_Chroma);
            m_Properties.SetVector(k_RowXId, rowX);
            m_Properties.SetVector(k_RowYId, rowY);
            m_Properties.SetFloat(k_LinearizeId, forModels ? 0f : 1f);
            m_Commands ??= new CommandBuffer { name = "Phone Camera Image" };
            m_Commands.Clear();
            m_Commands.SetRenderTarget(target);
            m_Commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, 1f, 0f, 1f, -0.1f, 9.9f));
            m_Commands.DrawMesh(m_Quad, Matrix4x4.identity, m_Material, 0, 0, m_Properties);
            Graphics.ExecuteCommandBuffer(m_Commands);
            return true;
        }

        // How far the head has turned since an image was taken, in the head's axes then, from the
        // head tracker's rotation history: for turning the image with the head between images, and
        // placing what's seen in it.
        public bool TryGetHeadTurnSince(long timestampNs, out Quaternion turn)
        {
            turn = Quaternion.identity;
            var tracker = m_Fusion != null ? m_Fusion.Tracker : null;
            if (tracker == null || timestampNs == 0 ||
                !m_Clock.TryToTrackerClock(timestampNs, tracker.NowNs, CameraClock.SystemNowNs(), out var trackerNs) ||
                !tracker.TryGetRotationAt(trackerNs, out var then))
                return false;
            turn = Quaternion.Inverse(then) * tracker.LastRotation;
            return true;
        }

        // Which way a point of an image looked in the world when it was taken: point in the image's
        // pixels over its size (0 to 1, x right, y down).
        public bool TryGetWorldDirection(Vector2 point, long timestampNs, out Vector3 direction)
        {
            direction = Vector3.forward;
            if (m_Fusion == null || !TryGetHeadTurnSince(timestampNs, out var turn))
                return false;
            var then = m_Fusion.transform.rotation * Quaternion.Inverse(turn);
            direction = then * ImageRay(point, ImageTangents);
            return true;
        }

        // A point of the upright image (0 to 1, x right, y down) as a direction from the camera, in its
        // axes (x right, y up, z ahead), unit length.
        public static Vector3 ImageRay(Vector2 point, Vector2 tangents) =>
            new Vector3((2f * point.x - 1f) * tangents.x, (1f - 2f * point.y) * tangents.y, 1f).normalized;

        // The stream's tangents (along its width and height) for the image turned upright.
        public static Vector2 UprightTangents(Vector2 stream, int rotation) =>
            rotation % 180 != 0 ? new Vector2(stream.y, stream.x) : stream;

        // Where the shader samples the planes for a point of the upright image: the planes' texture
        // coordinates (s, t) = (dot(rowX.xy, uv) + rowX.z, dot(rowY.xy, uv) + rowY.z) for the image's
        // uv (0 to 1, x right, y up). The planes are uploaded top row first, so t = 0 is the
        // picture's top. rotation is how far the picture must turn clockwise to stand upright.
        public static void ImageFromScreen(int rotation, out Vector4 rowX, out Vector4 rowY)
        {
            switch (((rotation % 360) + 360) % 360)
            {
                case 90:
                    rowX = new Vector4(0f, -1f, 1f, 0f);
                    rowY = new Vector4(-1f, 0f, 1f, 0f);
                    break;
                case 180:
                    rowX = new Vector4(-1f, 0f, 1f, 0f);
                    rowY = new Vector4(0f, 1f, 0f, 0f);
                    break;
                case 270:
                    rowX = new Vector4(0f, 1f, 0f, 0f);
                    rowY = new Vector4(1f, 0f, 0f, 0f);
                    break;
                default:
                    rowX = new Vector4(1f, 0f, 0f, 0f);
                    rowY = new Vector4(0f, -1f, 1f, 0f);
                    break;
            }
        }

        // The Java side's lines, "cameraId;zoomRatio;magnification;tanAcross;tanUp", as lenses from the
        // widest to the narrowest.
        public static List<Lens> ParseLenses(string text)
        {
            var lenses = new List<Lens>();
            if (string.IsNullOrEmpty(text))
                return lenses;
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Trim().Split(';');
                if (parts.Length < 5)
                    continue;
                if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom) ||
                    !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var magnification) ||
                    !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var across) ||
                    !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var up) ||
                    magnification <= 0f)
                    continue;
                lenses.Add(new Lens
                {
                    CameraId = parts[0], ZoomRatio = zoom, Magnification = magnification, Tangents = new Vector2(across, up),
                });
            }
            lenses.Sort((a, b) => a.Magnification.CompareTo(b.Magnification));
            return lenses;
        }

        // As the phone's camera app labels its lenses: "0.5x", "1x", "5x", "2.5x".
        public static string LensLabel(float magnification)
        {
            var rounded = Mathf.Round(magnification);
            if (magnification >= 1.5f && Mathf.Abs(magnification - rounded) < 0.15f)
                return $"{rounded.ToString("0", CultureInfo.InvariantCulture)}x";
            var tenths = Mathf.Round(magnification * 10f) / 10f;
            return $"{tenths.ToString("0.#", CultureInfo.InvariantCulture)}x";
        }

        static Mesh CreateUnitQuad()
        {
            var mesh = new Mesh { name = "Phone Camera Quad" };
            mesh.SetVertices(new[]
            {
                new Vector3(0f, 0f, 0.1f), new Vector3(0f, 1f, 0.1f), new Vector3(1f, 1f, 0.1f), new Vector3(1f, 0f, 0.1f),
            });
            mesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            return mesh;
        }
    }
}
