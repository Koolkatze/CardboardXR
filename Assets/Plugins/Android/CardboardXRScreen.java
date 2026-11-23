package com.brenpoly.cardboardxr;

import android.app.Activity;
import android.content.ComponentName;
import android.content.ServiceConnection;
import android.content.pm.PackageManager;
import android.graphics.PixelFormat;
import android.media.Image;
import android.media.ImageReader;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.Looper;
import android.os.Parcel;
import android.os.RemoteException;
import android.os.SystemClock;
import android.util.Log;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;

import rikka.shizuku.Shizuku;

// A phone app on a screen in CardboardXR's stereo scene (AppScreen.cs), on CardboardXR's side:
//   - Shizuku: whether it's running and CardboardXR may use it (shizuku(), askShizuku());
//   - the screen service (CardboardXRScreenService), which Shizuku runs as the shell user, bound once
//     and kept while CardboardXR runs;
//   - frames: the virtual display draws into an ImageReader's surface; each new image is copied into
//     one of three direct buffers, the newest of which Unity takes with lockFrame(), uploads straight
//     from its memory (frameBuffer) into a texture, and gives back with unlockFrame(). Three, so the
//     copy never writes into the one Unity is reading nor the newest it hasn't taken yet. Copies are
//     kept to maxFps, as each is a few MB, and XR's own frames come first;
//   - touches and keys, passed on to the service one way, without waiting.
// open() and close() run on a thread of their own, since making a display and starting an app take
// a few hundred ms; status() says where they're at. Logs to logcat (tag CardboardXR); AppScreen.cs
// logs the outcomes [Apps].
public final class CardboardXRScreen {
    public static final int SHIZUKU_MISSING = 0;
    public static final int SHIZUKU_NOT_ALLOWED = 1;
    public static final int SHIZUKU_READY = 2;
    public static final int SHIZUKU_TOO_OLD = 3;

    private static final String TAG = "CardboardXR";
    private static final int PERMISSION_REQUEST = 0x5843;
    // Raised whenever the service's code changes, so Shizuku replaces a service left from an older build.
    private static final int SERVICE_VERSION = 1;
    private static final int BUFFERS = 3;
    private static final Handler MAIN = new Handler(Looper.getMainLooper());

    private static final Object s_Lock = new Object();
    private static Handler s_Work;
    private static Handler s_FrameThread;

    private static Shizuku.UserServiceArgs s_Args;
    private static IBinder s_Service;
    private static boolean s_Binding;
    private static Runnable s_WhenBound;
    private static boolean s_Listening;
    private static volatile int s_PermissionAnswer = -1;

    private static ImageReader s_Reader;
    private static ByteBuffer[] s_Buffers;
    private static int s_Width;
    private static int s_Height;
    private static int s_Generation;
    private static int s_Newest = -1;
    private static boolean s_NewestTaken = true;
    private static int s_Reading = -1;
    private static long s_LastCopy;
    private static volatile int s_MaxFps = 30;
    private static volatile int s_Copied;

    private static volatile String s_Status = "idle";
    private static volatile int s_DisplayId = -1;

    private CardboardXRScreen() {}

    // ---- Shizuku ----

    // Whether Shizuku is running, and whether CardboardXR may use it.
    public static int shizuku() {
        try {
            listen();
            if (!Shizuku.pingBinder())
                return SHIZUKU_MISSING;
            if (Shizuku.isPreV11())
                return SHIZUKU_TOO_OLD;
            return Shizuku.checkSelfPermission() == PackageManager.PERMISSION_GRANTED ? SHIZUKU_READY : SHIZUKU_NOT_ALLOWED;
        } catch (Throwable t) {
            // The library missing from the build, or the binder dying between the calls.
            Log.w(TAG, "Couldn't ask Shizuku", t);
            return SHIZUKU_MISSING;
        }
    }

    // Shows Shizuku's own dialog asking to let CardboardXR use it. False if Shizuku won't ask again
    // (the user chose "deny and don't ask again"), so it has to be allowed in the Shizuku app.
    public static boolean askShizuku() {
        try {
            listen();
            if (Shizuku.shouldShowRequestPermissionRationale())
                return false;
            s_PermissionAnswer = -1;
            Shizuku.requestPermission(PERMISSION_REQUEST);
            return true;
        } catch (Throwable t) {
            Log.w(TAG, "Couldn't ask for Shizuku", t);
            return false;
        }
    }

    // The answer to askShizuku, once: 1 allowed, 0 denied, -1 not yet.
    public static int takeShizukuAnswer() {
        int answer = s_PermissionAnswer;
        s_PermissionAnswer = -1;
        return answer;
    }

    private static void listen() {
        synchronized (s_Lock) {
            if (s_Listening)
                return;
            s_Listening = true;
        }
        Shizuku.addRequestPermissionResultListener((requestCode, grantResult) -> {
            if (requestCode == PERMISSION_REQUEST)
                s_PermissionAnswer = grantResult == PackageManager.PERMISSION_GRANTED ? 1 : 0;
        });
        Shizuku.addBinderDeadListener(() -> {
            synchronized (s_Lock) {
                s_Service = null;
                s_Binding = false;
            }
            if (s_DisplayId >= 0)
                s_Status = "failed: Shizuku stopped";
            s_DisplayId = -1;
        });
    }

    // ---- The screen ----

    // Opens the app (its launcher activity, package/class) on a new display of this size in pixels at
    // this density, replacing any app open before.
    public static void open(final Activity activity, final String key, final int width, final int height, final int dpi) {
        s_Status = "starting";
        work().post(() -> {
            try {
                frames(width, height);
                withService(activity, () -> work().post(() -> start(key, width, height, dpi)));
            } catch (Throwable t) {
                Log.w(TAG, "Couldn't open the screen", t);
                s_Status = "failed: " + t;
            }
        });
    }

    // What the last open did: "idle", "starting", "binding", "showing: ...", "closed" or "failed: ...".
    public static String status() { return s_Status; }

    // The display the app is on, or -1.
    public static int displayId() { return s_DisplayId; }

    // Takes the display down, and the app on it.
    public static void close() {
        s_DisplayId = -1;
        s_Status = "closed";
        work().post(() -> {
            IBinder service = s_Service;
            if (service != null) {
                try {
                    call(service, CardboardXRScreenService.RELEASE, data -> { });
                } catch (Throwable t) {
                    Log.w(TAG, "Couldn't release the display", t);
                }
            }
            closeFrames();
        });
    }

    public static void setMaxFps(int fps) { s_MaxFps = Math.max(1, fps); }

    private static void start(String key, int width, int height, int dpi) {
        IBinder service = s_Service;
        if (service == null) {
            s_Status = "failed: the screen service didn't start";
            return;
        }
        try {
            final ImageReader reader = s_Reader;
            if (reader == null) {
                s_Status = "failed: no frames";
                return;
            }
            Object[] created = call(service, CardboardXRScreenService.CREATE, data -> {
                data.writeInt(1);
                reader.getSurface().writeToParcel(data, 0);
                data.writeInt(width);
                data.writeInt(height);
                data.writeInt(dpi);
            });
            int display = (int) created[0];
            if (display < 0) {
                s_Status = (String) created[1];
                return;
            }
            s_DisplayId = display;
            Object[] launched = call(service, CardboardXRScreenService.LAUNCH, data -> data.writeString(key));
            String launch = (String) launched[1];
            s_Status = (int) launched[0] < 0 ? launch : "showing: " + created[1] + "; " + launch;
        } catch (Throwable t) {
            Log.w(TAG, "The screen service failed", t);
            s_Status = "failed: " + t;
        }
    }

    // Binds the service if it isn't, then runs then on the main thread.
    private static void withService(final Activity activity, final Runnable then) {
        MAIN.post(() -> {
            if (s_Service != null && s_Service.pingBinder()) {
                then.run();
                return;
            }
            s_WhenBound = then;
            if (s_Binding)
                return;
            s_Binding = true;
            s_Status = "binding";
            if (s_Args == null) {
                s_Args = new Shizuku.UserServiceArgs(new ComponentName(activity.getPackageName(),
                        CardboardXRScreenService.class.getName()))
                        .daemon(false)
                        .processNameSuffix("screen")
                        .tag("screen")
                        .debuggable(false)
                        .version(SERVICE_VERSION);
            }
            try {
                Shizuku.bindUserService(s_Args, CONNECTION);
            } catch (Throwable t) {
                s_Binding = false;
                s_Status = "failed: couldn't start the screen service: " + t;
            }
        });
    }

    private static final ServiceConnection CONNECTION = new ServiceConnection() {
        @Override
        public void onServiceConnected(ComponentName name, IBinder service) {
            s_Binding = false;
            if (service == null || !service.pingBinder()) {
                s_Status = "failed: the screen service isn't answering";
                return;
            }
            s_Service = service;
            Runnable then = s_WhenBound;
            s_WhenBound = null;
            if (then != null)
                then.run();
        }

        @Override
        public void onServiceDisconnected(ComponentName name) {
            s_Binding = false;
            s_Service = null;
            if (s_DisplayId >= 0)
                s_Status = "failed: the screen service stopped";
            s_DisplayId = -1;
        }
    };

    private interface Writer {
        void write(Parcel data);
    }

    // A two-way call to the service: its int and its line of text.
    private static Object[] call(IBinder service, int code, Writer writer) throws RemoteException {
        Parcel data = Parcel.obtain();
        Parcel reply = Parcel.obtain();
        try {
            data.writeInterfaceToken(CardboardXRScreenService.DESCRIPTOR);
            writer.write(data);
            service.transact(code, data, reply, 0);
            reply.readException();
            return new Object[] { reply.readInt(), reply.readString() };
        } finally {
            data.recycle();
            reply.recycle();
        }
    }

    // ---- Input ----

    // A touch on the display: MotionEvent.ACTION_DOWN, ACTION_MOVE or ACTION_UP, at x, y in its pixels.
    public static void touch(int action, float x, float y) {
        oneWay(CardboardXRScreenService.TOUCH, data -> {
            data.writeInt(action);
            data.writeFloat(x);
            data.writeFloat(y);
        });
    }

    // A key pressed and released on the display, such as KeyEvent.KEYCODE_BACK.
    public static void key(int keyCode) {
        oneWay(CardboardXRScreenService.KEY, data -> data.writeInt(keyCode));
    }

    private static void oneWay(int code, Writer writer) {
        IBinder service = s_Service;
        if (service == null || s_DisplayId < 0)
            return;
        Parcel data = Parcel.obtain();
        try {
            data.writeInterfaceToken(CardboardXRScreenService.DESCRIPTOR);
            writer.write(data);
            service.transact(code, data, null, IBinder.FLAG_ONEWAY);
        } catch (Throwable t) {
            Log.w(TAG, "Couldn't send input to the screen", t);
        } finally {
            data.recycle();
        }
    }

    // ---- Frames ----

    // Goes up when the buffers are made anew (a new size), so Unity takes their addresses again.
    public static int frameGeneration() {
        synchronized (s_Lock) {
            return s_Generation;
        }
    }

    public static int frameWidth() { return s_Width; }

    public static int frameHeight() { return s_Height; }

    // How many frames have been copied, for the logs.
    public static int framesCopied() { return s_Copied; }

    // One of the buffers, width * height RGBA pixels, top row first; direct, so Unity reads its memory.
    public static ByteBuffer frameBuffer(int index) {
        synchronized (s_Lock) {
            return s_Buffers != null && index >= 0 && index < s_Buffers.length ? s_Buffers[index] : null;
        }
    }

    // The newest frame's buffer if Unity hasn't had it, kept from the copier until unlockFrame; or -1.
    // -1 too if the buffers aren't of that generation any more.
    public static int lockFrame(int generation) {
        synchronized (s_Lock) {
            if (generation != s_Generation || s_Newest < 0 || s_NewestTaken)
                return -1;
            s_Reading = s_Newest;
            s_NewestTaken = true;
            return s_Reading;
        }
    }

    public static void unlockFrame() {
        synchronized (s_Lock) {
            s_Reading = -1;
        }
    }

    private static void frames(int width, int height) {
        synchronized (s_Lock) {
            if (s_Reader != null && s_Width == width && s_Height == height)
                return;
        }
        closeFrames();
        ImageReader reader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 3);
        ByteBuffer[] buffers = new ByteBuffer[BUFFERS];
        for (int i = 0; i < BUFFERS; i++)
            buffers[i] = ByteBuffer.allocateDirect(width * height * 4).order(ByteOrder.nativeOrder());
        synchronized (s_Lock) {
            s_Reader = reader;
            s_Buffers = buffers;
            s_Width = width;
            s_Height = height;
            s_Generation++;
            s_Newest = -1;
            s_NewestTaken = true;
            s_Reading = -1;
        }
        reader.setOnImageAvailableListener(CardboardXRScreen::copy, frameHandler());
    }

    private static void closeFrames() {
        ImageReader reader;
        synchronized (s_Lock) {
            reader = s_Reader;
            s_Reader = null;
            s_Newest = -1;
            s_NewestTaken = true;
        }
        if (reader != null) {
            reader.setOnImageAvailableListener(null, null);
            reader.close();
        }
    }

    // The newest image into a buffer Unity isn't reading and hasn't yet to take, row by row if the
    // image's rows are padded.
    private static void copy(ImageReader reader) {
        Image image;
        try {
            image = reader.acquireLatestImage();
        } catch (RuntimeException e) {
            return;
        }
        if (image == null)
            return;
        try {
            long now = SystemClock.uptimeMillis();
            if (now - s_LastCopy < 1000 / s_MaxFps)
                return;
            int target;
            ByteBuffer destination;
            int width;
            int height;
            synchronized (s_Lock) {
                if (reader != s_Reader || s_Buffers == null)
                    return;
                target = -1;
                for (int i = 0; i < BUFFERS && target < 0; i++) {
                    if (i != s_Reading && !(i == s_Newest && !s_NewestTaken))
                        target = i;
                }
                if (target < 0)
                    return;
                destination = s_Buffers[target];
                width = s_Width;
                height = s_Height;
            }
            Image.Plane plane = image.getPlanes()[0];
            ByteBuffer source = plane.getBuffer();
            int rowBytes = width * 4;
            int stride = plane.getRowStride();
            destination.clear();
            if (stride == rowBytes) {
                source.clear().limit(rowBytes * height);
                destination.put(source);
            } else {
                for (int row = 0; row < height; row++) {
                    int start = row * stride;
                    source.clear().limit(start + rowBytes).position(start);
                    destination.put(source);
                }
            }
            synchronized (s_Lock) {
                if (reader != s_Reader)
                    return;
                s_Newest = target;
                s_NewestTaken = false;
            }
            s_LastCopy = now;
            s_Copied++;
        } catch (RuntimeException e) {
            Log.w(TAG, "Couldn't copy a frame", e);
        } finally {
            image.close();
        }
    }

    private static Handler work() {
        synchronized (s_Lock) {
            if (s_Work == null) {
                HandlerThread thread = new HandlerThread("CardboardXR screen");
                thread.start();
                s_Work = new Handler(thread.getLooper());
            }
            return s_Work;
        }
    }

    private static Handler frameHandler() {
        synchronized (s_Lock) {
            if (s_FrameThread == null) {
                HandlerThread thread = new HandlerThread("CardboardXR frames");
                thread.start();
                s_FrameThread = new Handler(thread.getLooper());
            }
            return s_FrameThread;
        }
    }
}
