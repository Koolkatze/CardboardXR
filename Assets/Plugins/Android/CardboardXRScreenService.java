package com.brenpoly.cardboardxr;

import android.app.ActivityOptions;
import android.content.AttributionSource;
import android.content.ComponentName;
import android.content.Context;
import android.content.ContextWrapper;
import android.content.Intent;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.os.Binder;
import android.os.Build;
import android.os.Bundle;
import android.os.IBinder;
import android.os.Parcel;
import android.os.Process;
import android.os.SystemClock;
import android.util.Log;
import android.view.InputDevice;
import android.view.InputEvent;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.Surface;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.lang.reflect.Constructor;
import java.lang.reflect.Method;

// The phone app's screen in XR, on the system's side. Shizuku runs this class in a process of its own
// as the shell user (the identity adb has), which a normal app can't be: CardboardXRScreen binds it.
// As the shell it can do what scrcpy's --new-display does:
//   - make a virtual display that draws into a Surface CardboardXR gives it (an ImageReader's), which
//     CardboardXR shows on a screen in its stereo scene. Trusted, as only the system and the shell may
//     make one, so any app can open on it, not only those that allow being embedded;
//   - open an app on that display, stopping it first, so an app already running on the phone's own
//     screen doesn't come back up there;
//   - send it touches and keys, as if the display had a touchscreen, where CardboardXR's gaze or pinch
//     lands on its screen.
// One display at a time. Its own Binder protocol (codes below) rather than AIDL, which Unity's build
// doesn't compile. Shizuku ends the process with DESTROY; it also ends when CardboardXR's does (the
// service is bound in non-daemon mode), and with it the display and the app on it.
// Logs to logcat (tag CardboardXR); results go back to CardboardXR as text, which logs them [Apps].
public class CardboardXRScreenService extends Binder {
    public static final String DESCRIPTOR = "com.brenpoly.cardboardxr.IScreenService";
    // Two-way calls reply with an int (the display id, or 0 for success; negative on failure) and a
    // line of text saying what happened.
    // A Surface, width, height and dpi: makes the display, replacing any other.
    public static final int CREATE = FIRST_CALL_TRANSACTION;
    // A launcher activity (package/class): opens it on the display.
    public static final int LAUNCH = FIRST_CALL_TRANSACTION + 1;
    // One way: a MotionEvent action, x and y in the display's pixels.
    public static final int TOUCH = FIRST_CALL_TRANSACTION + 2;
    // One way: a key code, pressed and released.
    public static final int KEY = FIRST_CALL_TRANSACTION + 3;
    // Takes the display down, and the app on it.
    public static final int RELEASE = FIRST_CALL_TRANSACTION + 4;
    // Shizuku's code for ending a user service (16777114 in AIDL terms).
    public static final int DESTROY = 16777115;

    private static final String TAG = "CardboardXR";
    private static final String SHELL_PACKAGE = "com.android.shell";

    // DisplayManager's hidden virtual display flags, as scrcpy uses them.
    private static final int FLAG_PUBLIC = 1;
    private static final int FLAG_PRESENTATION = 1 << 1;
    private static final int FLAG_OWN_CONTENT_ONLY = 1 << 3;
    private static final int FLAG_SUPPORTS_TOUCH = 1 << 6;
    private static final int FLAG_DESTROY_CONTENT_ON_REMOVAL = 1 << 8;
    private static final int FLAG_TRUSTED = 1 << 10;
    private static final int FLAG_OWN_DISPLAY_GROUP = 1 << 11;
    private static final int FLAG_ALWAYS_UNLOCKED = 1 << 12;
    private static final int FLAG_TOUCH_FEEDBACK_DISABLED = 1 << 13;
    private static final int FLAG_OWN_FOCUS = 1 << 14;
    private static final int FLAG_DEVICE_DISPLAY_GROUP = 1 << 15;
    // The display never takes the top focus from the phone's own screen when touched (with its own
    // focus; a newer Android's flag, an unused bit before, and dropped on the next attempt if refused).
    private static final int FLAG_STEAL_TOP_FOCUS_DISABLED = 1 << 16;
    // MotionEvent.FLAG_NO_FOCUS_CHANGE (Android 12): a touch that doesn't move focus to the window
    // touched. Without it, touching the app's display moved the phone's focus there, away from
    // CardboardXR, whose rendering then stopped until the phone's own screen was touched again.
    private static final int MOTION_FLAG_NO_FOCUS_CHANGE = 0x40;
    // WindowManager.DISPLAY_IME_POLICY_LOCAL: the keyboard shows on the display it types into.
    private static final int IME_POLICY_LOCAL = 0;
    // UserHandle.USER_CURRENT.
    private static final int USER_CURRENT = -2;

    private final Context m_Context;
    private VirtualDisplay m_Display;
    private int m_DisplayId = -1;
    private int m_Width;
    private int m_Height;
    private long m_DownTime;
    private Object m_Input;
    private Method m_Inject;
    private Method m_SetDisplayId;
    private boolean m_InputFailed;

    public CardboardXRScreenService() {
        this(null);
    }

    // Shizuku 13 and later pass a context for CardboardXR's package, created as the shell user.
    public CardboardXRScreenService(Context context) {
        m_Context = context;
        Log.i(TAG, "Screen service started as uid " + Process.myUid());
    }

    @Override
    protected boolean onTransact(int code, Parcel data, Parcel reply, int flags) throws android.os.RemoteException {
        if (code == INTERFACE_TRANSACTION) {
            reply.writeString(DESCRIPTOR);
            return true;
        }
        if (code == DESTROY) {
            release();
            System.exit(0);
            return true;
        }
        if (code < CREATE || code > RELEASE)
            return super.onTransact(code, data, reply, flags);
        data.enforceInterface(DESCRIPTOR);
        int result;
        String message;
        try {
            switch (code) {
                case CREATE: {
                    Surface surface = data.readInt() != 0 ? Surface.CREATOR.createFromParcel(data) : null;
                    int width = data.readInt();
                    int height = data.readInt();
                    int dpi = data.readInt();
                    message = create(surface, width, height, dpi);
                    result = m_DisplayId;
                    break;
                }
                case LAUNCH:
                    message = launch(data.readString());
                    result = message.startsWith("failed") ? -1 : 0;
                    break;
                case TOUCH:
                    touch(data.readInt(), data.readFloat(), data.readFloat());
                    return true;
                case KEY:
                    key(data.readInt());
                    return true;
                default:
                    release();
                    result = 0;
                    message = "released";
                    break;
            }
        } catch (Throwable t) {
            Log.w(TAG, "Screen service call " + code + " failed", t);
            result = -1;
            message = "failed: " + t;
        }
        if (reply != null) {
            reply.writeNoException();
            reply.writeInt(result);
            reply.writeString(message);
        }
        return true;
    }

    // ---- The display ----

    private synchronized String create(Surface surface, int width, int height, int dpi) throws Exception {
        release();
        if (surface == null || !surface.isValid())
            return "failed: no surface";
        DisplayManager displays = shellDisplayManager();
        // Every flag scrcpy's new displays use, then fewer, in case this Android or this shell doesn't
        // allow some. Untrusted, only apps that allow embedding would open on it.
        int base = FLAG_PUBLIC | FLAG_PRESENTATION | FLAG_OWN_CONTENT_ONLY | FLAG_SUPPORTS_TOUCH
                | FLAG_DESTROY_CONTENT_ON_REMOVAL;
        int trusted = Build.VERSION.SDK_INT >= 33 ? FLAG_TRUSTED | FLAG_OWN_DISPLAY_GROUP | FLAG_TOUCH_FEEDBACK_DISABLED : 0;
        int unlocked = Build.VERSION.SDK_INT >= 33 ? FLAG_ALWAYS_UNLOCKED : 0;
        int focus = Build.VERSION.SDK_INT >= 34 ? FLAG_OWN_FOCUS | FLAG_DEVICE_DISPLAY_GROUP : 0;
        int noSteal = Build.VERSION.SDK_INT >= 34 ? FLAG_STEAL_TOP_FOCUS_DISABLED : 0;
        int[] attempts = {
            base | trusted | unlocked | focus | noSteal,
            base | trusted | unlocked | focus,
            base | trusted | focus,
            base | trusted,
            base,
        };
        RuntimeException last = null;
        for (int flags : attempts) {
            try {
                m_Display = displays.createVirtualDisplay("CardboardXR", width, height, dpi, surface, flags);
                if (m_Display != null && m_Display.getDisplay() != null) {
                    m_DisplayId = m_Display.getDisplay().getDisplayId();
                    m_Width = width;
                    m_Height = height;
                    String ime = keyboardHere(m_DisplayId);
                    String message = "display " + m_DisplayId + ", " + width + "x" + height + " at " + dpi + " dpi, flags 0x"
                            + Integer.toHexString(flags) + (flags == attempts[0] ? "" : " (fewer than asked)") + ", " + ime;
                    Log.i(TAG, message);
                    return message;
                }
            } catch (RuntimeException e) {
                Log.w(TAG, "Virtual display flags 0x" + Integer.toHexString(flags) + " not allowed", e);
                last = e;
            }
        }
        return "failed: no virtual display" + (last != null ? ": " + last.getMessage() : "");
    }

    private synchronized void release() {
        if (m_Display != null) {
            try {
                m_Display.release();
            } catch (RuntimeException e) {
                Log.w(TAG, "Couldn't release the display", e);
            }
        }
        m_Display = null;
        m_DisplayId = -1;
    }

    // DisplayManager checks that the package asking matches the uid asking, so it's made with a context
    // that says it's the shell's (scrcpy's FakeContext).
    private DisplayManager shellDisplayManager() throws Exception {
        Constructor<DisplayManager> constructor = DisplayManager.class.getDeclaredConstructor(Context.class);
        constructor.setAccessible(true);
        return constructor.newInstance(new ShellContext(baseContext()));
    }

    private Context baseContext() throws Exception {
        if (m_Context != null)
            return m_Context;
        Class<?> activityThread = Class.forName("android.app.ActivityThread");
        Object thread = activityThread.getMethod("currentActivityThread").invoke(null);
        if (thread == null)
            thread = activityThread.getMethod("systemMain").invoke(null);
        return (Context) activityThread.getMethod("getSystemContext").invoke(thread);
    }

    // The keyboard on the display, so typing into an app is done on its own screen. Best effort.
    private static String keyboardHere(int displayId) {
        try {
            Object windows = windowManager();
            if (Build.VERSION.SDK_INT >= 31) {
                windows.getClass().getMethod("setDisplayImePolicy", int.class, int.class).invoke(windows, displayId, IME_POLICY_LOCAL);
            } else {
                windows.getClass().getMethod("setShouldShowIme", int.class, boolean.class).invoke(windows, displayId, true);
            }
            return "keyboard on it";
        } catch (Throwable t) {
            Log.w(TAG, "Couldn't put the keyboard on the display", t);
            return "keyboard on the phone's screen";
        }
    }

    private static Object windowManager() throws Exception {
        Object binder = Class.forName("android.os.ServiceManager").getMethod("getService", String.class).invoke(null, "window");
        return Class.forName("android.view.IWindowManager$Stub").getMethod("asInterface", IBinder.class).invoke(null, binder);
    }

    // ---- Apps ----

    private synchronized String launch(String key) throws Exception {
        if (m_DisplayId < 0)
            return "failed: no display";
        ComponentName component = key != null ? ComponentName.unflattenFromString(key) : null;
        if (component == null)
            return "failed: not an app's launcher activity";
        Intent intent = new Intent(Intent.ACTION_MAIN)
                .addCategory(Intent.CATEGORY_LAUNCHER)
                .setComponent(component)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        ActivityOptions options = ActivityOptions.makeBasic();
        options.setLaunchDisplayId(m_DisplayId);
        try {
            Object activities = activityManager();
            // Stopped first: an app already running would otherwise come back up where it was.
            activities.getClass().getMethod("forceStopPackage", String.class, int.class)
                    .invoke(activities, component.getPackageName(), USER_CURRENT);
            int status = startActivity(activities, intent, options.toBundle());
            // ActivityManager.START_SUCCESS and the other non-negative codes are started.
            if (status >= 0)
                return "opened on display " + m_DisplayId + " (status " + status + ")";
            Log.w(TAG, "startActivityAsUser returned " + status + "; trying am");
        } catch (Throwable t) {
            Log.w(TAG, "Couldn't start the app directly; trying am", t);
        }
        return am("start", "-S", "--display", Integer.toString(m_DisplayId), "-n", component.flattenToShortString());
    }

    private static Object activityManager() throws Exception {
        try {
            return Class.forName("android.app.ActivityManager").getMethod("getService").invoke(null);
        } catch (ReflectiveOperationException e) {
            return Class.forName("android.app.ActivityManagerNative").getMethod("getDefault").invoke(null);
        }
    }

    // IActivityManager.startActivityAsUser, as the shell, which may start activities on any display.
    private static int startActivity(Object activities, Intent intent, Bundle options) throws Exception {
        Class<?> applicationThread = Class.forName("android.app.IApplicationThread");
        Class<?> profilerInfo = Class.forName("android.app.ProfilerInfo");
        Method start = activities.getClass().getMethod("startActivityAsUser", applicationThread, String.class, Intent.class,
                String.class, IBinder.class, String.class, int.class, int.class, profilerInfo, Bundle.class, int.class);
        return (int) start.invoke(activities, null, SHELL_PACKAGE, intent, null, null, null, 0, 0, null, options, USER_CURRENT);
    }

    // The am command, for when the hidden method isn't there: slower, as it starts a process.
    private static String am(String... args) {
        String[] command = new String[args.length + 1];
        command[0] = "am";
        System.arraycopy(args, 0, command, 1, args.length);
        try {
            java.lang.Process process = new ProcessBuilder(command).redirectErrorStream(true).start();
            StringBuilder output = new StringBuilder();
            try (BufferedReader reader = new BufferedReader(new InputStreamReader(process.getInputStream()))) {
                String line;
                while ((line = reader.readLine()) != null)
                    output.append(line.trim()).append(' ');
            }
            int exit = process.waitFor();
            String text = output.toString().trim();
            boolean failed = exit != 0 || text.contains("Error");
            return (failed ? "failed: am " : "opened with am: ") + text;
        } catch (Exception e) {
            return "failed: am: " + e;
        }
    }

    // ---- Input ----

    private synchronized void touch(int action, float x, float y) {
        if (m_DisplayId < 0)
            return;
        long now = SystemClock.uptimeMillis();
        if (action == MotionEvent.ACTION_DOWN)
            m_DownTime = now;
        x = Math.max(0f, Math.min(m_Width - 1, x));
        y = Math.max(0f, Math.min(m_Height - 1, y));
        MotionEvent.PointerProperties[] properties = { new MotionEvent.PointerProperties() };
        properties[0].id = 0;
        properties[0].toolType = MotionEvent.TOOL_TYPE_FINGER;
        MotionEvent.PointerCoords[] coords = { new MotionEvent.PointerCoords() };
        coords[0].x = x;
        coords[0].y = y;
        coords[0].pressure = 1f;
        coords[0].size = 1f;
        // A finger on the display that leaves the phone's focus on CardboardXR.
        MotionEvent event = MotionEvent.obtain(m_DownTime, now, action, 1, properties, coords, 0, 0, 1f, 1f, 0, 0,
                InputDevice.SOURCE_TOUCHSCREEN, MOTION_FLAG_NO_FOCUS_CHANGE);
        // The input command can't keep focus where it is, so only the hidden method is used for presses
        // and drags; the command is the last resort for a tap.
        if (!inject(event) && action == MotionEvent.ACTION_UP)
            command("input", "-d", Integer.toString(m_DisplayId), "tap", Integer.toString((int) x), Integer.toString((int) y));
        event.recycle();
    }

    private synchronized void key(int keyCode) {
        if (m_DisplayId < 0)
            return;
        long now = SystemClock.uptimeMillis();
        boolean injected = true;
        for (int action : new int[] { KeyEvent.ACTION_DOWN, KeyEvent.ACTION_UP }) {
            KeyEvent event = new KeyEvent(now, now, action, keyCode, 0, 0, KeyCharacterMap.VIRTUAL_KEYBOARD, 0, 0,
                    InputDevice.SOURCE_KEYBOARD);
            injected &= inject(event);
        }
        if (!injected)
            command("input", "-d", Integer.toString(m_DisplayId), "keyevent", Integer.toString(keyCode));
    }

    // InputManager.injectInputEvent, hidden, with the event's display set (scrcpy's way). False if it
    // couldn't, and the slower input command is used instead.
    private boolean inject(InputEvent event) {
        if (m_InputFailed)
            return false;
        try {
            if (m_Inject == null) {
                Object input;
                Class<?> type;
                try {
                    type = Class.forName("android.hardware.input.InputManagerGlobal");
                    input = type.getMethod("getInstance").invoke(null);
                } catch (ReflectiveOperationException e) {
                    type = Class.forName("android.hardware.input.InputManager");
                    input = type.getMethod("getInstance").invoke(null);
                }
                m_Input = input;
                m_Inject = type.getMethod("injectInputEvent", InputEvent.class, int.class);
                m_SetDisplayId = InputEvent.class.getMethod("setDisplayId", int.class);
            }
            m_SetDisplayId.invoke(event, m_DisplayId);
            // InputManager.INJECT_INPUT_EVENT_MODE_ASYNC.
            return (boolean) m_Inject.invoke(m_Input, event, 0);
        } catch (Throwable t) {
            Log.w(TAG, "Couldn't inject input; using the input command", t);
            m_InputFailed = true;
            return false;
        }
    }

    private static void command(String... command) {
        try {
            new ProcessBuilder(command).redirectErrorStream(true).start();
        } catch (Exception e) {
            Log.w(TAG, "Couldn't run " + command[0], e);
        }
    }

    // A context that says it's the shell's, so system services take the shell's calls as its own.
    private static final class ShellContext extends ContextWrapper {
        ShellContext(Context base) {
            super(base);
        }

        @Override
        public String getPackageName() {
            return SHELL_PACKAGE;
        }

        @Override
        public String getOpPackageName() {
            return SHELL_PACKAGE;
        }

        @Override
        public AttributionSource getAttributionSource() {
            return new AttributionSource.Builder(Process.SHELL_UID).setPackageName(SHELL_PACKAGE).build();
        }

        @Override
        public Context getApplicationContext() {
            return this;
        }
    }
}
