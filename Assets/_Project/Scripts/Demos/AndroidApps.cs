using System;
using System.Collections.Generic;
using CardboardXR.Stereo;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace CardboardXR.Demos
{
    // The phone's own apps, for the app library's Android section (AppLibrary): the apps a launcher
    // shows, with their icons, and opening one. By default an app that can turn opens in landscape,
    // since the phone sits landscape in a viewer; an app that only runs upright opens upright (forced on
    // its side, Android would letterbox it in a narrow strip), and a landscape app needs no help. Which
    // is which comes from the screenOrientation of the app's launcher activity (Fit). The "Landscape"
    // switch on the library's rail turns this off and on (LandscapeEnabled, saved), as does the
    // "landscapeApps" launch option for one run.
    //   In XR: with Shizuku running and CardboardXR allowed in it, the app opens on a screen in the
    // stereo scene (AppScreen), on a virtual display of that shape. Allowing it is asked for (Shizuku's
    // own dialog) the first time an app is picked with Shizuku running.
    //   Without Shizuku, the screen says why the app can't open on it, and what to do (AppScreen
    // ShowNotice); its Open flat opens the app over CardboardXR, on the phone's own screen, where one
    // that can turn is kept in landscape while it's in front.
    //   The Java side (Assets/Plugins/Android/CardboardXRApps.java) lists the apps on a thread of its
    //   own, draws their icons and holds landscape with an overlay window, which needs the "Display over
    //   other apps" permission. The first time an app would be kept in landscape without it, Settings
    //   opens at CardboardXR's switch (after asking, on Android 13 and later, for notifications, so a
    //   notification can end the hold); coming back, the app opens, in landscape if it was allowed.
    //   After that it isn't asked again: without the permission, apps open upright as they choose.
    //   The hold ends when CardboardXR comes back to the front, when the screen goes off, or from that
    //   notification.
    // Only on an Android phone: elsewhere Supported is false and the library has no Android section.
    // Creates itself (like ScreenBrightness), so Core needs nothing for it. Logs [Apps], without the
    // apps' names: which apps someone opens is theirs, and the session log holds nothing personal.
    public class AndroidApps : MonoBehaviour
    {
        // How an app's launcher activity sits: free to turn (so it can be kept in landscape), fixed on its
        // side, or fixed upright.
        public enum Fit
        {
            Turns,
            Landscape,
            Portrait,
        }

        public readonly struct App
        {
            // Its launcher activity, as package/class, which opens it.
            public readonly string Key;
            public readonly string Label;
            // That activity's ActivityInfo.screenOrientation.
            public readonly int Orientation;

            public App(string key, string label, int orientation)
            {
                Key = key;
                Label = label;
                Orientation = orientation;
            }

            public Fit Fit => FitOf(Orientation);
        }

        public const string k_LogTag = "[Apps]";
        const string k_JavaClass = "com.brenpoly.cardboardxr.CardboardXRApps";
        const string k_LandscapeKey = "LandscapeApps";
        const string k_AskedKey = "AskedToKeepAppsLandscape";
        const string k_NotificationPermission = "android.permission.POST_NOTIFICATIONS";
        // The icons' size in pixels: an icon tile is 72 dp, which a viewer's eye sees at about this.
        const int k_IconPixels = 96;
        // How often the Java side is asked whether a new list or a launch's outcome is in.
        const float k_PollSeconds = 0.25f;

        // ActivityInfo.screenOrientation values that fix an activity upright: portrait, sensorPortrait,
        // reversePortrait, userPortrait. And on its side: landscape, sensorLandscape, reverseLandscape,
        // userLandscape. Every other value (unspecified, user, behind, sensor, nosensor, fullSensor,
        // fullUser, locked) lets the display decide, so the activity turns with it.
        static readonly int[] k_PortraitOrientations = { 1, 7, 9, 12 };
        static readonly int[] k_LandscapeOrientations = { 0, 6, 8, 11 };

        enum Asking
        {
            None,
            Notifications,
            OverlaySetting,
        }

        static readonly List<App> s_Apps = new();
        static readonly Dictionary<string, Texture2D> s_Icons = new();
        static int s_JavaVersion;
        static int s_Version;
        static bool s_Loaded;
        static bool s_Watching;
        static bool? s_Landscape;

        static Asking s_Asking;
        static volatile bool s_Answered;
        static bool s_LeftForSetting;
        static App s_Pending;
        // The app waiting for Shizuku's answer, and whether it opens in landscape.
        static App? s_ShizukuPending;

        // An app is being opened, until the Java side says how it went.
        static bool s_Opening;
        // An app was opened and CardboardXR hasn't come back since; and whether it has gone to the
        // background since, so a focus change during the launch itself doesn't end the hold.
        static bool s_Opened;
        static bool s_PausedSinceOpen;

        float m_NextPoll;

#if UNITY_ANDROID && !UNITY_EDITOR
        public static bool Supported => true;
#else
        public static bool Supported => false;
#endif

        // The apps, sorted by name as the launcher sorts them; empty until the first list is in.
        public static IReadOnlyList<App> Apps => s_Apps;
        // Goes up each time a new list replaces Apps.
        public static int Version => s_Version;
        public static bool Loaded => s_Loaded;

        // Whether apps that can turn open in landscape: on unless switched off, or the "landscapeApps"
        // launch option says otherwise for this run.
        public static bool LandscapeEnabled
        {
            get
            {
                s_Landscape ??= LaunchOptions.GetInt("landscapeApps", -1) switch
                {
                    0 => false,
                    1 => true,
                    _ => PlayerPrefs.GetInt(k_LandscapeKey, 1) != 0,
                };
                return s_Landscape.Value;
            }
            set
            {
                s_Landscape = value;
                PlayerPrefs.SetInt(k_LandscapeKey, value ? 1 : 0);
                PlayerPrefs.Save();
                Debug.Log($"{k_LogTag} Phone apps that can turn open {(value ? "in landscape" : "as they choose")}");
            }
        }

        public static Fit FitOf(int screenOrientation) =>
            Array.IndexOf(k_PortraitOrientations, screenOrientation) >= 0 ? Fit.Portrait
            : Array.IndexOf(k_LandscapeOrientations, screenOrientation) >= 0 ? Fit.Landscape
            : Fit.Turns;

        // Whether an app is kept in landscape: only one that can turn, and only with the switch on.
        public static bool HoldsLandscape(Fit fit, bool enabled) => enabled && fit == Fit.Turns;

        // The line the library shows while the app is looked at.
        public static string Describe(App app, bool landscape) => app.Fit switch
        {
            Fit.Portrait => $"{app.Label} · runs upright only",
            Fit.Landscape => $"{app.Label} · a landscape app",
            _ => landscape ? $"{app.Label} · opens in landscape" : $"{app.Label} · turns as it likes",
        };

        static string FitName(Fit fit) => fit switch
        {
            Fit.Portrait => "upright only",
            Fit.Landscape => "landscape",
            _ => "can turn",
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            if (!Supported)
                return;
            SessionLog.KeepTag(k_LogTag);
            var holder = new GameObject("Android Apps");
            DontDestroyOnLoad(holder);
            holder.AddComponent<AndroidApps>();
        }

        // A run that was killed while holding landscape may have left its notification up.
        void Awake() => Release();

        void Update()
        {
            if (s_Asking == Asking.Notifications && s_Answered)
            {
                s_Answered = false;
                OpenOverlaySetting();
            }
            if (s_ShizukuPending.HasValue)
                TakeShizukuAnswer();
            if (Time.unscaledTime < m_NextPoll)
                return;
            m_NextPoll = Time.unscaledTime + k_PollSeconds;
            if (s_Watching)
                ReadList();
            if (s_Opening)
                ReadStatus();
        }

        void OnApplicationPause(bool paused)
        {
            if (!paused)
                return;
            if (s_Asking == Asking.OverlaySetting)
                s_LeftForSetting = true;
            if (s_Opened)
                s_PausedSinceOpen = true;
        }

        // Back in front: from Settings, open the app that was waiting for it; from an app, let the
        // display turn again.
        void OnApplicationFocus(bool focused)
        {
            if (!focused)
            {
                // CardboardXR stops drawing without focus: a touch on the app's screen used to take it.
                if (AppScreen.IsOpen)
                    Debug.LogWarning($"{k_LogTag} CardboardXR lost focus while an app's screen is open");
                return;
            }
            if (s_Asking == Asking.OverlaySetting && s_LeftForSetting)
            {
                s_Asking = Asking.None;
                Debug.Log($"{k_LogTag} Back from Settings: {(CanHoldLandscape() ? "allowed" : "not allowed")} to display over other apps");
                Launch(s_Pending, true);
                return;
            }
            if (s_Opened && !s_PausedSinceOpen)
                return;
            var wasOpened = s_Opened;
            s_Opened = false;
            var released = Call<string>("takeReleased");
            if (!string.IsNullOrEmpty(released))
                Debug.Log($"{k_LogTag} Landscape was let go while away: {released}");
            if (Call<bool>("isHolding"))
                Debug.Log($"{k_LogTag} Back from the app: letting the display turn again");
            else if (wasOpened)
                Debug.Log($"{k_LogTag} Back from the app");
            Release();
        }

        // Lists the phone's apps again; Apps and Version change once the new list is in.
        public static void Refresh()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var activity = Activity();
                Java.CallStatic("refresh", activity, k_IconPixels);
                s_Watching = true;
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"{k_LogTag} Couldn't list the phone's apps: {e.Message}");
            }
#endif
        }

        // The app's icon, or null until it has loaded. Kept once made.
        public static Texture2D Icon(string key)
        {
            if (key == null)
                return null;
            if (s_Icons.TryGetValue(key, out var texture))
                return texture;
            var png = Call<sbyte[]>("iconPng", key);
            if (png == null || png.Length == 0)
                return null;
            var bytes = new byte[png.Length];
            Buffer.BlockCopy(png, 0, bytes, 0, png.Length);
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, true)
            {
                name = $"Icon {key}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
            };
            if (!texture.LoadImage(bytes, true))
            {
                Destroy(texture);
                return null;
            }
            s_Icons[key] = texture;
            return texture;
        }

        // Opens the app, in landscape if it can turn and the switch is on, asking for what that needs the
        // first time.
        public static void Open(App app)
        {
            if (!Supported)
                return;
            if (s_Asking != Asking.None || s_ShizukuPending.HasValue)
            {
                Debug.Log($"{k_LogTag} Ignored a pick: still asking for permission to open the app picked before");
                return;
            }
            var landscape = OpensInLandscape(app.Fit, LandscapeEnabled);
            var state = AppScreen.Shizuku;
            switch (state)
            {
                case AppScreen.ShizukuState.Ready:
                    AppScreen.Show(app, landscape);
                    return;
                case AppScreen.ShizukuState.NotAllowed when AppScreen.AskShizuku():
                    Debug.Log($"{k_LogTag} Asking Shizuku to let CardboardXR open apps on a screen in XR");
                    s_ShizukuPending = app;
                    return;
            }
            // Said in XR, on the screen, rather than opening the app flat behind the user's back.
            var installed = AppScreen.ShizukuInstalled;
            var error = state == AppScreen.ShizukuState.Error ? AppScreen.ShizukuError : "";
            Debug.Log($"{k_LogTag} Can't open the app on a screen in XR: Shizuku {state}" +
                      $"{(installed ? ", installed" : ", not installed")}{(error.Length > 0 ? $" ({error})" : "")}");
            AppScreen.ShowNotice(app, landscape, AppScreen.NoticeFor(state, installed, app.Label, error));
        }

        // On a screen in XR, whether the app's display is landscape: landscape apps always, apps that can
        // turn with the switch on.
        public static bool OpensInLandscape(Fit fit, bool enabled) => fit == Fit.Landscape || HoldsLandscape(fit, enabled);

        // Whether apps open on a screen in XR: Shizuku running and CardboardXR allowed in it, or allowed
        // when asked.
        public static bool OpensInXr => Supported && AppScreen.Shizuku is AppScreen.ShizukuState.Ready or AppScreen.ShizukuState.NotAllowed;

        static void TakeShizukuAnswer()
        {
            var answer = AppScreen.TakeShizukuAnswer();
            if (answer < 0)
                return;
            var app = s_ShizukuPending.Value;
            s_ShizukuPending = null;
            if (answer == 1)
            {
                Debug.Log($"{k_LogTag} Shizuku allowed");
                AppScreen.Show(app, OpensInLandscape(app.Fit, LandscapeEnabled));
                return;
            }
            Debug.Log($"{k_LogTag} Shizuku not allowed");
            AppScreen.ShowNotice(app, OpensInLandscape(app.Fit, LandscapeEnabled),
                AppScreen.NoticeFor(AppScreen.ShizukuState.NotAllowed, true, app.Label, ""));
        }

        // Over CardboardXR, on the phone's own screen, kept in landscape if it can turn: the notice's
        // Open flat.
        public static void OpenFlat(App app)
        {
            var hold = HoldsLandscape(app.Fit, LandscapeEnabled);
            if (hold && !CanHoldLandscape() && PlayerPrefs.GetInt(k_AskedKey, 0) == 0)
            {
                PlayerPrefs.SetInt(k_AskedKey, 1);
                PlayerPrefs.Save();
                s_Pending = app;
                AskForNotifications();
                return;
            }
            Launch(app, hold);
        }

        public static bool CanHoldLandscape() => Call<bool>("canHoldLandscape", ActivityArgument);

        static void Launch(App app, bool hold)
        {
            if (hold && !CanHoldLandscape())
            {
                Debug.Log($"{k_LogTag} The app can't be kept in landscape: CardboardXR isn't allowed to display over other " +
                          "apps (Settings > Apps > CardboardXR > Display over other apps)");
                hold = false;
            }
            Debug.Log($"{k_LogTag} Opening an app ({FitName(app.Fit)}){(hold ? ", kept in landscape" : "")}");
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var activity = Activity();
                Java.CallStatic("launch", activity, app.Key, app.Label, hold);
                s_Opening = true;
                s_Opened = true;
                s_PausedSinceOpen = false;
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"{k_LogTag} Couldn't open the app: {e.Message}");
            }
#endif
        }

        // On Android 13 and later, notifications first (if they're off), so the hold can show the one
        // that ends it; then the overlay setting.
        static void AskForNotifications()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Call<bool>("shouldAskForNotifications", ActivityArgument))
            {
                s_Asking = Asking.Notifications;
                s_Answered = false;
                // Answered on Java's thread; Update goes on from there.
                var callbacks = new PermissionCallbacks();
                callbacks.PermissionGranted += _ => s_Answered = true;
                callbacks.PermissionDenied += _ => s_Answered = true;
                callbacks.PermissionRequestDismissed += _ => s_Answered = true;
                Debug.Log($"{k_LogTag} Asking for notifications, to show one that lets apps turn again");
                Permission.RequestUserPermission(k_NotificationPermission, callbacks);
                return;
            }
#endif
            OpenOverlaySetting();
        }

        static void OpenOverlaySetting()
        {
            s_Asking = Asking.OverlaySetting;
            s_LeftForSetting = false;
            Debug.Log($"{k_LogTag} Opening Settings to let CardboardXR display over other apps, to keep the app in landscape");
            if (Call<bool>("openOverlaySettings", ActivityArgument))
                return;
            s_Asking = Asking.None;
            Launch(s_Pending, true);
        }

        static void Release()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var activity = Activity();
                Java.CallStatic("release", activity);
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"{k_LogTag} Couldn't let the display turn again: {e.Message}");
            }
#endif
        }

        static void ReadList()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                var version = Java.CallStatic<int>("version");
                if (version == s_JavaVersion)
                    return;
                using var list = Java.CallStatic<AndroidJavaObject>("list");
                if (list == null)
                    return;
                var keys = list.Call<string[]>("keys");
                var labels = list.Call<string[]>("labels");
                var orientations = list.Call<int[]>("orientations");
                s_Apps.Clear();
                int turns = 0, landscape = 0, portrait = 0;
                for (var i = 0; i < keys.Length; i++)
                {
                    var app = new App(keys[i], labels[i], orientations[i]);
                    s_Apps.Add(app);
                    switch (app.Fit)
                    {
                        case Fit.Turns: turns++; break;
                        case Fit.Landscape: landscape++; break;
                        default: portrait++; break;
                    }
                }
                s_JavaVersion = version;
                s_Version++;
                s_Loaded = true;
                Debug.Log($"{k_LogTag} {s_Apps.Count} apps on the phone: {turns} can turn, {landscape} landscape, {portrait} upright only");
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"{k_LogTag} Couldn't read the phone's apps: {e.Message}");
                s_Watching = false;
            }
#endif
        }

        static void ReadStatus()
        {
            var status = Call<string>("status");
            if (status == "pending")
                return;
            if (status != null && status.StartsWith("failed", StringComparison.Ordinal))
            {
                // Nothing opened, so nothing to come back from (the Java side has let go of landscape).
                Debug.LogWarning($"{k_LogTag} The app {status}");
                s_Opened = false;
            }
            else
            {
                Debug.Log($"{k_LogTag} The app {status}");
            }
            s_Opening = false;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static AndroidJavaClass s_Java;
        static AndroidJavaClass Java => s_Java ??= new AndroidJavaClass(k_JavaClass);

        static AndroidJavaObject Activity()
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            return player.GetStatic<AndroidJavaObject>("currentActivity");
        }
#endif

        // A placeholder for the activity in Call's arguments, swapped for the real one there.
        static readonly object ActivityArgument = new();

        // A static method of the Java side, or the default if it fails or there is none.
        static T Call<T>(string method, params object[] args)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJavaObject activity = null;
            try
            {
                for (var i = 0; i < args.Length; i++)
                {
                    if (args[i] == ActivityArgument)
                        args[i] = activity ??= Activity();
                }
                return Java.CallStatic<T>(method, args);
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"{k_LogTag} {method} failed: {e.Message}");
            }
            finally
            {
                activity?.Dispose();
            }
#endif
            return default;
        }
    }
}
