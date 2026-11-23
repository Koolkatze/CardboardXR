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
    // (the app's back key), Move here (in front of you again) and Close.
    //   Making the display and opening an app on it takes the shell user's rights, which Shizuku lends
    //   (CardboardXRScreen.java and CardboardXRScreenService.java do the Android side), so the screen
    //   needs Shizuku running and CardboardXR allowed in it; without, AndroidApps opens apps flat.
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
        }

        // MotionEvent's actions and KeyEvent.KEYCODE_BACK.
        public const int k_Down = 0;
        public const int k_Up = 1;
        public const int k_Move = 2;
        const int k_KeyBack = 4;

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
        string m_Label;
        string m_LastStatus;
        Vector2 m_LastTouch;
        float m_NextPoll;
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
        public static ShizukuState Shizuku => (ShizukuState)Call<int>("shizuku");

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

        void Open(AndroidApps.App app, bool landscape)
        {
            Build(landscape);
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
            Run("close");
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
            if (core != null)
                WorldUi.PlaceInFront(m_Panel, core.Head, k_Distance, 0f);
        }

        // A touch where the ray meets the screen's plane (held to its edges, so a drag that runs off it
        // keeps going along the edge). Called by AppScreenSurface.
        public void TouchAt(int action, Ray ray)
        {
            if (!m_Open || m_ScreenRect == null)
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
            if (action == k_Move && (point - m_LastTouch).sqrMagnitude < 1f)
                return;
            m_LastTouch = point;
            Run("touch", action, point.x, point.y);
        }

        void Update()
        {
            if (!m_Open)
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
            screenObject.AddComponent<AppScreenSurface>().Initialize(this);

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
