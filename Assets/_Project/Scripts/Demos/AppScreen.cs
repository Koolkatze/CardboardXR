using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Demos
{
    // One of the phone's apps on a screen in the stereo scene, seen in both eyes like everything else in
    // CardboardXR: the app runs on a virtual display of its own, landscape if it can turn (AndroidApps),
    // and its frames come into a texture on a panel 2 m ahead. Look at it and tap the screen, or pinch,
    // to touch it there; hold the tap or the pinch and look along to drag (and scroll). Under it, Back
    // (the app's back key), Move here (in front of you again) and Close, and below that a handle
    // (AppScreenHandle): pinch it, or hold a tap on it, and the screen follows the hand (or the gaze)
    // around you. Pinching with both hands, the first on the screen or its handle, resizes it by how
    // far apart the hands go, and moves it with them (PinchSensor.BothPinched); the touch the first
    // pinch began is cancelled. Its size is kept between runs (k_MinScale to k_MaxScale).
    //   Making the display and opening an app on it takes the shell user's rights, which Shizuku lends
    //   (CardboardXRScreen.java and CardboardXRScreenService.java do the Android side), so the screen
    //   needs Shizuku running and CardboardXR allowed in it. Without, the screen shows why instead
    //   (ShowNotice), with Try again, Open flat (over CardboardXR, on the phone's own screen) and
    //   Shizuku (its app, to start it or allow CardboardXR, or its download page).
    //   Frames: the Java side copies each new frame into one of three direct buffers, and this uploads
    //   the newest it hasn't had straight from that memory into the texture (no copy through C#), up to
    //   AppScreenFps a second. The display is k_LongSidePixels by k_ShortSidePixels at k_Dpi: 640x360 dp,
    //   the layout of a phone held sideways, whose text stays readable at the size the screen is seen.
    // One screen at a time: opening another app replaces the one showing. Created on first use and kept
    // across scenes. Logs [Apps], without the app's name.
    public class AppScreen : MonoBehaviour
    {
        public enum ShizukuState
        {
            Missing = 0,
            NotAllowed = 1,
            Ready = 2,
            TooOld = 3,
            Error = 4,
        }

        // MotionEvent's actions and KeyEvent.KEYCODE_BACK.
        public const int k_Down = 0;
        public const int k_Up = 1;
        public const int k_Move = 2;
        public const int k_Cancel = 3;
        // How far, in the display's pixels, the gaze must move a held touch before it drags: about a
        // degree, more than the head's tremor, so holding still long-presses.
        const float k_SlopPixels = 28f;
        const int k_KeyBack = 4;
        public const float k_MinScale = 0.4f;
        public const float k_MaxScale = 3f;
        const string k_ScaleKey = "AppScreenScale";
        // How quickly the screen catches up with a hand moving or resizing it: most of the way in this
        // many seconds, as hands are seen only a few times a second.
        const float k_FollowSeconds = 0.12f;

        public const int k_LongSidePixels = 960;
        public const int k_ShortSidePixels = 540;
        public const int k_Dpi = 240;
        // The screen's long side in dp at k_Distance: 1.78 m at 2 m, about 48 degrees, like a large TV
        // seen from a sofa.
        public const float k_LongSideDp = 640f;
        public const float k_Distance = 2f;
        const float k_Padding = 8f;
        const float k_BarHeight = 56f;
        const float k_PollSeconds = 0.25f;
        const string k_JavaClass = "com.brenpoly.cardboardxr.CardboardXRScreen";

        static AppScreen s_Instance;

        RectTransform m_Panel;
        RectTransform m_ScreenRect;
        RawImage m_Screen;
        TMP_Text m_Status;
        TMP_Text m_Title;
        Texture2D m_Texture;
        Vector2Int m_Display;
        bool m_Landscape;
        bool m_Open;
        bool m_Showing;
        // Showing why the app can't open here, rather than the app.
        bool m_Notice;
        AndroidApps.App m_App;
        RectTransform m_NoticeButtons;
        string m_Label;
        string m_LastStatus;
        Vector2 m_LastTouch;
        // Where the touch went down, and whether it has moved past the slop since: until it has, it
        // stays still, so a held pinch or tap is a long press rather than the gaze's tremor dragging.
        Vector2 m_DownTouch;
        bool m_TouchMoved;
        float m_NextPoll;
        // How big it is against its usual size, and where it's headed while a hand moves or resizes it.
        float m_Scale = 1f;
        float m_TargetScale = 1f;
        Vector3 m_TargetDirection;
        bool m_Following;
        // A pinch on the screen or its handle is held (AppScreenSurface, AppScreenHandle).
        bool m_PinchHeld;
        AppScreenSurface m_Surface;
        // Moving by the handle: what points (the pinching hand, or the gaze) and where, when it began.
        bool m_Moving;
        bool m_MoveByHand;
        Vector3 m_MoveStartPointer;
        Vector3 m_MoveStartDirection;
        // Resizing with both hands: their angle apart, middle and the screen, when it began.
        bool m_Resizing;
        float m_ResizeStartAngle;
        float m_ResizeStartScale;
        Vector3 m_ResizeStartMiddle;
        Vector3 m_ResizeStartDirection;
        int m_Generation = -1;
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject[] m_Buffers;
        NativeArray<byte>[] m_Frames;
#endif

        public static bool IsOpen => s_Instance != null && s_Instance.m_Open;

        // The display's size in pixels, landscape or upright.
        public static Vector2Int DisplayPixels(bool landscape) =>
            landscape ? new Vector2Int(k_LongSidePixels, k_ShortSidePixels) : new Vector2Int(k_ShortSidePixels, k_LongSidePixels);

        // The screen's size in dp, the display's shape.
        public static Vector2 ScreenDp(bool landscape)
        {
            var shortSide = k_LongSideDp * k_ShortSidePixels / k_LongSidePixels;
            return landscape ? new Vector2(k_LongSideDp, shortSide) : new Vector2(shortSide, k_LongSideDp);
        }

        // The panel: the screen with padding round it, and the bar under it.
        public static Vector2 PanelDp(bool landscape)
        {
            var screen = ScreenDp(landscape);
            return new Vector2(screen.x + 2f * k_Padding, screen.y + 3f * k_Padding + k_BarHeight);
        }

        // A point on the screen, from its bottom-left corner as a fraction of its size, on the display in
        // pixels from its top-left corner (where touches are given), held to its edges.
        public static Vector2 ToDisplay(Vector2 fraction, Vector2Int display) =>
            new(Mathf.Clamp01(fraction.x) * display.x, (1f - Mathf.Clamp01(fraction.y)) * display.y);

        // Whether Shizuku is running and lets CardboardXR use it; Missing off a phone.
        public static ShizukuState Shizuku
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                try
                {
                    return (ShizukuState)Java.CallStatic<int>("shizuku");
                }
                catch (AndroidJavaException e)
                {
                    s_LastError = e.Message;
                    return ShizukuState.Error;
                }
#else
                return ShizukuState.Missing;
#endif
            }
        }

        static string s_LastError = "";

        // Why Shizuku last answered Error.
        public static string ShizukuError
        {
            get
            {
                var java = Call<string>("shizukuError");
                return string.IsNullOrEmpty(java) ? s_LastError : java;
            }
        }

        public static bool ShizukuInstalled => CallWithActivity<bool>("shizukuInstalled");

        // What to tell the user, in the screen, when apps can't open here.
        public static string NoticeFor(ShizukuState state, bool installed, string label, string error) => state switch
        {
            ShizukuState.NotAllowed => $"CardboardXR isn't allowed to use Shizuku, so {label} can't open here. " +
                                       "Allow CardboardXR in the Shizuku app, then try again.",
            ShizukuState.TooOld => $"This Shizuku is too old to open {label} here. Update Shizuku, then try again.",
            ShizukuState.Error => $"Couldn't reach Shizuku to open {label} here: {error}",
            _ => installed
                ? $"Shizuku isn't running, so {label} can't open here. Start it in the Shizuku app " +
                  "(with Wireless debugging), then try again."
                : $"{label} opens here with Shizuku, which isn't installed. Install it, start it " +
                  "(with Wireless debugging), then try again.",
        };

        // The screen in front of the user, saying why the app can't open on it, with what to do.
        public static void ShowNotice(AndroidApps.App app, bool landscape, string message)
        {
            if (s_Instance == null)
            {
                var holder = new GameObject("App Screen");
                DontDestroyOnLoad(holder);
                s_Instance = holder.AddComponent<AppScreen>();
            }
            s_Instance.Notice(app, landscape, message);
        }

        // Asks Shizuku (its own dialog) to let CardboardXR use it. False if it won't ask again.
        public static bool AskShizuku() => Call<bool>("askShizuku");

        // The answer to AskShizuku, once: 1 allowed, 0 denied, -1 not yet.
        public static int TakeShizukuAnswer() => Supported ? Call<int>("takeShizukuAnswer") : -1;

        // Opens the app on the screen, in landscape or upright, in front of the user.
        public static void Show(AndroidApps.App app, bool landscape)
        {
            if (s_Instance == null)
            {
                var holder = new GameObject("App Screen");
                DontDestroyOnLoad(holder);
                s_Instance = holder.AddComponent<AppScreen>();
            }
            s_Instance.Open(app, landscape);
        }

        static bool Supported => AndroidApps.Supported;

        void Notice(AndroidApps.App app, bool landscape, string message)
        {
            if (m_Open && !m_Notice)
                Run("close");
            Build(landscape);
            m_App = app;
            m_Label = app.Label;
            m_Title.text = app.Label;
            m_Open = true;
            m_Notice = true;
            m_Showing = false;
            m_Screen.texture = null;
            m_Screen.color = Color.black;
            m_NoticeButtons.gameObject.SetActive(true);
            SetStatus(message);
            Place();
            M3Fade.SetShown(m_Panel.gameObject, true);
            Debug.Log("[Apps] Showed why the app can't open on a screen in XR");
        }

        void TryAgain()
        {
            Debug.Log("[Apps] Trying again to open the app on a screen in XR");
            AndroidApps.Open(m_App);
        }

        void OpenFlat()
        {
            var app = m_App;
            Close();
            AndroidApps.OpenFlat(app);
        }

        void OpenShizuku()
        {
            var outcome = CallWithActivity<string>("openShizuku");
            Debug.Log($"[Apps] Shizuku: {outcome}");
        }

        void Open(AndroidApps.App app, bool landscape)
        {
            Build(landscape);
            m_App = app;
            m_Notice = false;
            m_NoticeButtons.gameObject.SetActive(false);
            m_Label = app.Label;
            m_Title.text = app.Label;
            m_Display = DisplayPixels(landscape);
            m_Open = true;
            m_Showing = false;
            m_LastStatus = null;
            m_Screen.texture = null;
            m_Screen.color = Color.black;
            SetStatus($"Opening {app.Label}…");
            Place();
            M3Fade.SetShown(m_Panel.gameObject, true);
            var fps = Mathf.Clamp(LaunchOptions.GetInt("appScreenFps", 30), 1, 60);
            Debug.Log($"[Apps] Opening an app on a screen in XR: {(landscape ? "landscape" : "upright")}, " +
                      $"{m_Display.x}x{m_Display.y} at {k_Dpi} dpi, up to {fps} fps");
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                Java.CallStatic("setMaxFps", fps);
                Java.CallStatic("open", activity, app.Key, m_Display.x, m_Display.y, k_Dpi);
            }
            catch (AndroidJavaException e)
            {
                Fail($"couldn't start: {e.Message}");
            }
#endif
        }

        public void Close()
        {
            if (!m_Open)
                return;
            m_Open = false;
            if (!m_Notice)
                Run("close");
            m_Notice = false;
            M3Fade.SetShown(m_Panel.gameObject, false);
            m_Screen.texture = null;
            m_Screen.color = Color.black;
            Debug.Log("[Apps] Closed the app's screen in XR");
        }

        void Back()
        {
            Run("key", k_KeyBack);
            Debug.Log("[Apps] Back, on the app's screen");
        }

        // In front of the head again, at eye level.
        void Place()
        {
            var core = CoreServices.Instance;
            m_Following = false;
            if (core != null)
                WorldUi.PlaceInFront(m_Panel, core.Head, k_Distance, 0f);
        }

        // Its usual size on its canvas (M3Canvas), times m_Scale.
        void ApplyScale()
        {
            if (m_Panel != null)
                m_Panel.localScale = Vector3.one * (M3Theme.DpToMeters(1f, k_Distance) * m_Scale);
        }

        // Toward the head from the screen, at k_Distance along direction, facing the head.
        void PlaceAlong(Vector3 direction)
        {
            var core = CoreServices.Instance;
            if (core == null || m_Panel == null || direction.sqrMagnitude < 1e-6f)
                return;
            var head = core.Head.position;
            var position = head + direction.normalized * k_Distance;
            m_Panel.SetPositionAndRotation(position, Quaternion.LookRotation(position - head, Vector3.up));
        }

        Vector3 PanelDirection()
        {
            var core = CoreServices.Instance;
            return core == null || m_Panel == null ? Vector3.forward : (m_Panel.position - core.Head.position).normalized;
        }

        // AppScreenSurface and AppScreenHandle: a pinch on them is held, or let go. While it's held the
        // pinch sensor looks for a second hand, for a two-handed pinch.
        public void SetPinchHeld(bool held)
        {
            m_PinchHeld = held;
            Hands.PinchSensor.WantsSecondHand = held;
        }

        // The handle was pinched (byHand, so the pinching hand moves it if it's seen) or tapped and held
        // (the gaze moves it).
        public void BeginMove(bool byHand)
        {
            var core = CoreServices.Instance;
            if (core == null || m_Panel == null)
                return;
            var sensor = Hands.PinchSensor.Instance;
            m_MoveByHand = byHand && sensor != null && sensor.TryGetPinchDirection(0, out m_MoveStartPointer);
            if (!m_MoveByHand)
                m_MoveStartPointer = core.Head.forward;
            m_MoveStartDirection = PanelDirection();
            m_TargetDirection = m_MoveStartDirection;
            m_Moving = true;
            m_Following = true;
            Debug.Log($"[Apps] Moving the screen with {(m_MoveByHand ? "the hand" : "the gaze")}");
        }

        public void EndMove()
        {
            if (!m_Moving)
                return;
            m_Moving = false;
            Debug.Log("[Apps] Moved the screen");
        }

        // Moves and resizes it as the hands or the gaze say, easing toward where they point.
        void LateUpdate()
        {
            if (m_Panel == null || !m_Open)
                return;
            var core = CoreServices.Instance;
            var sensor = Hands.PinchSensor.Instance;
            var both = sensor != null && sensor.BothPinched;
            if (!m_Resizing && both && m_PinchHeld)
                BeginResize(sensor);
            else if (m_Resizing && !both)
                EndResize();

            if (m_Resizing)
            {
                if (sensor.TryGetPinchDirection(0, out var a) && sensor.TryGetPinchDirection(1, out var b))
                {
                    var angle = Mathf.Max(1f, Vector3.Angle(a, b));
                    m_TargetScale = Mathf.Clamp(m_ResizeStartScale * angle / m_ResizeStartAngle, k_MinScale, k_MaxScale);
                    m_TargetDirection = Quaternion.FromToRotation(m_ResizeStartMiddle, (a + b).normalized) * m_ResizeStartDirection;
                }
            }
            else if (m_Moving && core != null)
            {
                var pointer = core.Head.forward;
                if (!m_MoveByHand || sensor.TryGetPinchDirection(0, out pointer))
                    m_TargetDirection = Quaternion.FromToRotation(m_MoveStartPointer, pointer) * m_MoveStartDirection;
            }
            if (!m_Following)
                return;
            var step = 1f - Mathf.Exp(-Time.unscaledDeltaTime / k_FollowSeconds);
            m_Scale = Mathf.Lerp(m_Scale, m_TargetScale, step);
            ApplyScale();
            PlaceAlong(Vector3.Slerp(PanelDirection(), m_TargetDirection, step));
            if (!m_Moving && !m_Resizing && Mathf.Abs(m_Scale - m_TargetScale) < 0.002f &&
                Vector3.Angle(PanelDirection(), m_TargetDirection) < 0.05f)
                m_Following = false;
        }

        void BeginResize(Hands.PinchSensor sensor)
        {
            if (!sensor.TryGetPinchDirection(0, out var a) || !sensor.TryGetPinchDirection(1, out var b))
                return;
            // The pinch that began a touch, or a move, is now this.
            m_Surface?.CancelPinch();
            EndMove();
            m_Resizing = true;
            m_Following = true;
            m_ResizeStartAngle = Mathf.Max(2f, Vector3.Angle(a, b));
            m_ResizeStartScale = m_TargetScale;
            m_ResizeStartMiddle = (a + b).normalized;
            m_ResizeStartDirection = PanelDirection();
            m_TargetDirection = m_ResizeStartDirection;
            Debug.Log($"[Apps] Resizing the screen with both hands, {m_ResizeStartAngle:F0} deg apart");
        }

        void EndResize()
        {
            m_Resizing = false;
            PlayerPrefs.SetFloat(k_ScaleKey, m_TargetScale);
            PlayerPrefs.Save();
            Debug.Log($"[Apps] Resized the screen to {m_TargetScale:F2}x its usual size");
        }

        // A touch where the ray meets the screen's plane (held to its edges, so a drag that runs off it
        // keeps going along the edge). Called by AppScreenSurface.
        public void TouchAt(int action, Ray ray)
        {
            if (!m_Open || m_Notice || m_ScreenRect == null)
                return;
            var plane = new Plane(m_ScreenRect.forward, m_ScreenRect.position);
            var point = m_LastTouch;
            if (plane.Raycast(ray, out var distance))
            {
                var local = m_ScreenRect.InverseTransformPoint(ray.GetPoint(distance));
                var rect = m_ScreenRect.rect;
                point = ToDisplay(new Vector2((local.x - rect.xMin) / rect.width, (local.y - rect.yMin) / rect.height), m_Display);
            }
            else if (action == k_Down)
            {
                return;
            }
            if (action == k_Down)
            {
                m_DownTouch = point;
                m_TouchMoved = false;
            }
            else if (!m_TouchMoved)
            {
                // The slop grows with the screen, so it's about the same angle of gaze.
                if ((point - m_DownTouch).magnitude < k_SlopPixels * Mathf.Max(1f, m_Scale) && action == k_Move)
                    return;
                if (action == k_Move)
                    m_TouchMoved = true;
                else
                    point = m_DownTouch;
            }
            if (action == k_Move && (point - m_LastTouch).sqrMagnitude < 1f)
                return;
            m_LastTouch = point;
            Run("touch", action, point.x, point.y);
        }

        void Update()
        {
            if (!m_Open || m_Notice)
                return;
            UploadFrame();
            if (Time.unscaledTime < m_NextPoll)
                return;
            m_NextPoll = Time.unscaledTime + k_PollSeconds;
            PollStatus();
        }

        // What the Java side says, logged as it changes; a failure shows on the screen.
        void PollStatus()
        {
            var status = Call<string>("status");
            if (string.IsNullOrEmpty(status) || status == m_LastStatus)
                return;
            m_LastStatus = status;
            if (status.StartsWith("failed", System.StringComparison.Ordinal))
            {
                Fail(status.Substring("failed".Length).TrimStart(':', ' '));
                return;
            }
            if (status == "binding")
                SetStatus($"Starting Shizuku's screen service for {m_Label}…");
            Debug.Log($"[Apps] Screen: {status}");
        }

        void Fail(string reason)
        {
            SetStatus($"Couldn't open {m_Label}: {reason}");
            Debug.LogWarning($"[Apps] Couldn't open the app on its screen: {reason}");
        }

        void SetStatus(string text)
        {
            m_Status.text = text;
            m_Status.gameObject.SetActive(!string.IsNullOrEmpty(text));
        }

        // The newest frame the Java side has, if it's one this hasn't had, into the texture.
        void UploadFrame()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                var generation = Java.CallStatic<int>("frameGeneration");
                if (generation != m_Generation && !Rebind(generation))
                    return;
                var index = Java.CallStatic<int>("lockFrame", generation);
                if (index < 0)
                    return;
                try
                {
                    m_Texture.LoadRawTextureData(m_Frames[index]);
                    m_Texture.Apply(false);
                }
                finally
                {
                    Java.CallStatic("unlockFrame");
                }
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"[Apps] Couldn't take a frame from the app's screen: {e.Message}");
                return;
            }
            if (m_Showing)
                return;
            m_Showing = true;
            m_Screen.texture = m_Texture;
            m_Screen.color = Color.white;
            SetStatus("");
            Debug.Log("[Apps] The app is on its screen in XR");
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // The Java side's buffers, made anew for a new size: their memory, read in place, and a texture
        // of that size.
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
            if (m_Texture == null || m_Texture.width != width || m_Texture.height != height)
            {
                if (m_Texture != null)
                    Destroy(m_Texture);
                m_Texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    name = "App Screen",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }
            if (m_Showing)
                m_Screen.texture = m_Texture;
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
#endif

        void OnDestroy()
        {
            if (m_Open)
                Run("close");
#if UNITY_ANDROID && !UNITY_EDITOR
            DisposeBuffers();
#endif
            if (m_Texture != null)
                Destroy(m_Texture);
            if (s_Instance == this)
                s_Instance = null;
        }

        // The panel, for a landscape or upright screen: the screen (black until the first frame), the
        // status over it, and the bar under it with the app's name, Back, Move here and Close. Made again
        // when the shape changes.
        void Build(bool landscape)
        {
            if (m_Panel != null && m_Landscape == landscape)
                return;
            if (m_Panel != null)
                Destroy(m_Panel.gameObject);
            m_Landscape = landscape;
            var size = PanelDp(landscape);
            var screenSize = ScreenDp(landscape);
            m_Panel = WorldUi.Panel("App Screen", transform, size, k_Distance);
            m_Scale = m_TargetScale = Mathf.Clamp(PlayerPrefs.GetFloat(k_ScaleKey, 1f), k_MinScale, k_MaxScale);
            ApplyScale();
            var screenY = size.y / 2f - k_Padding - screenSize.y / 2f;

            var screenObject = new GameObject("Screen", typeof(RectTransform));
            m_ScreenRect = (RectTransform)screenObject.transform;
            m_ScreenRect.SetParent(m_Panel, false);
            m_ScreenRect.anchoredPosition = new Vector2(0f, screenY);
            m_ScreenRect.sizeDelta = screenSize;
            m_Screen = screenObject.AddComponent<RawImage>();
            m_Screen.color = Color.black;
            m_Screen.raycastTarget = true;
            // The frames' rows come top first, a texture's bottom first.
            m_Screen.uvRect = new Rect(0f, 1f, 1f, -1f);
            m_Surface = screenObject.AddComponent<AppScreenSurface>();
            m_Surface.Initialize(this);

            // The handle under the panel: a pill, in a larger area that takes the gaze.
            var handleObject = new GameObject("Handle", typeof(RectTransform));
            var handle = (RectTransform)handleObject.transform;
            handle.SetParent(m_Panel, false);
            handle.anchoredPosition = new Vector2(0f, -size.y / 2f - 28f);
            handle.sizeDelta = new Vector2(240f, 44f);
            var handleArea = handleObject.AddComponent<Image>();
            handleArea.color = Color.clear;
            handleArea.raycastTarget = true;
            M3.Surface("Pill", handle, Vector2.zero, new Vector2(160f, 10f), M3Theme.OnSurfaceVariant, M3Theme.k_ShapeFull);
            handleObject.AddComponent<AppScreenHandle>().Initialize(this);

            m_Status = M3.Text("Status", m_Panel, "", M3Theme.Type.BodyLarge, new Vector2(0f, screenY),
                new Vector2(screenSize.x - 48f, 120f), M3Theme.OnSurfaceVariant);

            var barY = -size.y / 2f + k_Padding + k_BarHeight / 2f;
            var closeX = size.x / 2f - k_Padding - M3Theme.k_Target / 2f;
            var moveX = closeX - M3Theme.k_Target - k_Padding;
            var backX = moveX - M3Theme.k_Target - k_Padding;
            var back = M3.IconButton("Back", m_Panel, "undo", M3Button.Variant.Tonal, new Vector2(backX, barY), null);
            var move = M3.IconButton("Move Here", m_Panel, "open_with", M3Button.Variant.Tonal, new Vector2(moveX, barY), null);
            var close = M3.IconButton("Close", m_Panel, "close", M3Button.Variant.Tonal, new Vector2(closeX, barY), null);
            back.Button.onClick.AddListener(Back);
            move.Button.onClick.AddListener(Place);
            close.Button.onClick.AddListener(Close);
            var titleLeft = -size.x / 2f + 2f * k_Padding;
            var titleRight = backX - M3Theme.k_Target / 2f - k_Padding;
            m_Title = M3.Text("Title", m_Panel, "", M3Theme.Type.TitleMedium, new Vector2((titleLeft + titleRight) / 2f, barY),
                new Vector2(titleRight - titleLeft, k_BarHeight), M3Theme.OnSurface, TextAlignmentOptions.Left);
            m_Title.textWrappingMode = TextWrappingModes.NoWrap;
            m_Title.overflowMode = TextOverflowModes.Ellipsis;

            // The notice's choices, side by side under its text in the middle of the screen; narrower, and
            // without icons, on an upright one.
            m_NoticeButtons = M3.Group("Notice", m_Panel, new Vector2(0f, screenY - 64f), new Vector2(screenSize.x, M3Theme.k_Target));
            var width = landscape ? 136f : 112f;
            var step = width + k_Padding;
            var again = M3.Button("Try Again", m_NoticeButtons, "Try again", M3Button.Variant.Filled, new Vector2(-step, 0f), null,
                landscape ? "refresh" : null, width);
            var flat = M3.Button("Open Flat", m_NoticeButtons, "Open flat", M3Button.Variant.Tonal, Vector2.zero, null,
                landscape ? "smartphone" : null, width);
            var shizuku = M3.Button("Shizuku", m_NoticeButtons, "Shizuku", M3Button.Variant.Tonal, new Vector2(step, 0f), null,
                landscape ? "settings" : null, width);
            again.Button.onClick.AddListener(TryAgain);
            flat.Button.onClick.AddListener(OpenFlat);
            shizuku.Button.onClick.AddListener(OpenShizuku);
            m_NoticeButtons.gameObject.SetActive(m_Notice);
            m_Status.rectTransform.anchoredPosition = new Vector2(0f, screenY + 36f);
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static AndroidJavaClass s_Java;
        static AndroidJavaClass Java => s_Java ??= new AndroidJavaClass(k_JavaClass);
#endif

        // A static method of the Java side, or the default if it fails or there's none.
        static T Call<T>(string method, params object[] args)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                return Java.CallStatic<T>(method, args);
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"[Apps] Screen {method} failed: {e.Message}");
            }
#endif
            return default;
        }

        // A static method of the Java side that takes the activity first.
        static T CallWithActivity<T>(string method)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                return Java.CallStatic<T>(method, activity);
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"[Apps] Screen {method} failed: {e.Message}");
            }
#endif
            return default;
        }

        // A static void method of the Java side.
        static void Run(string method, params object[] args)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                Java.CallStatic(method, args);
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"[Apps] Screen {method} failed: {e.Message}");
            }
#endif
        }
    }
}
