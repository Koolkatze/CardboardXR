package com.brenpoly.cardboardxr;

import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.ActivityInfo;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.PixelFormat;
import android.graphics.drawable.Drawable;
import android.net.Uri;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.util.Log;
import android.view.Gravity;
import android.view.View;
import android.view.ViewTreeObserver;
import android.view.WindowManager;

import java.io.ByteArrayOutputStream;
import java.text.Collator;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

// The phone's own apps, for the app library's Android section (AndroidApps.cs): the apps a launcher
// shows, with their icons, and opening one, kept in landscape if it can turn.
//   Listing runs on a thread of its own, since loading ~150 labels and icons takes a few hundred ms:
//   refresh() starts it, version() goes up when a new list is ready, and the icons follow one by one
//   in the list's order, so the first page's come first.
//   Landscape is held by an overlay window: a 1-pixel, invisible, untouchable window of type
//   TYPE_APPLICATION_OVERLAY whose screenOrientation asks for landscape. The display takes its
//   orientation from the topmost window asking for one, and overlays sit above every app, so the app
//   in front turns with the display even if its own activity asked for no orientation (or for the
//   sensor). Apps that only run upright aren't held, since Android would letterbox them in a narrow
//   portrait strip. It needs the "Display over other apps" permission, which the user grants in
//   Settings (openOverlaySettings). The hold ends when CardboardXR comes back to the front
//   (AndroidApps.cs calls release), when the screen goes off, or from the notification shown while
//   holding (when notifications are allowed); an app left by the home button stays in landscape until
//   one of those. While the overlay shows, the process counts as perceptible, so it isn't killed.
// Called from Unity's main thread; window and receiver work is posted to Android's main thread.
public final class CardboardXRApps {
    private static final String TAG = "CardboardXR";
    private static final String CHANNEL = "landscape";
    private static final int NOTIFICATION_ID = 0x4c414e44;
    private static final String ACTION_STOP = "com.brenpoly.cardboardxr.STOP_LANDSCAPE";
    // How long to wait for the overlay's first layout (when the display has its orientation) before
    // opening the app anyway.
    private static final long LAYOUT_TIMEOUT_MS = 300;
    private static final Handler MAIN = new Handler(Looper.getMainLooper());

    // The apps, as one immutable list replaced whole by each refresh.
    public static final class AppList {
        private final String[] m_Keys;
        private final String[] m_Labels;
        private final int[] m_Orientations;

        AppList(String[] keys, String[] labels, int[] orientations) {
            m_Keys = keys;
            m_Labels = labels;
            m_Orientations = orientations;
        }

        // Each app's launcher activity, as package/class (ComponentName.flattenToString).
        public String[] keys() { return m_Keys; }
        public String[] labels() { return m_Labels; }
        // Each launcher activity's ActivityInfo.screenOrientation.
        public int[] orientations() { return m_Orientations; }
    }

    private static final class Entry {
        final String key;
        final String label;
        final int orientation;
        final ResolveInfo info;

        Entry(String key, String label, int orientation, ResolveInfo info) {
            this.key = key;
            this.label = label;
            this.orientation = orientation;
            this.info = info;
        }
    }

    private static final Object s_Lock = new Object();
    private static boolean s_Refreshing;
    private static volatile AppList s_List;
    private static volatile int s_Version;
    private static volatile String s_ListError = "";
    private static final Map<String, byte[]> s_Icons = new ConcurrentHashMap<>();

    // Android's main thread only.
    private static View s_Overlay;
    private static BroadcastReceiver s_Receiver;
    // Read from Unity's thread.
    private static volatile boolean s_Holding;
    private static volatile String s_Status = "";
    private static volatile String s_Released = "";

    private CardboardXRApps() {}

    // ---- Listing ----

    // Lists the apps again, unless a listing is still running. iconSize is in pixels.
    public static void refresh(Context context, final int iconSize) {
        synchronized (s_Lock) {
            if (s_Refreshing)
                return;
            s_Refreshing = true;
        }
        final Context app = context.getApplicationContext();
        Thread thread = new Thread(() -> {
            try {
                list(app, iconSize);
            } catch (RuntimeException e) {
                Log.w(TAG, "Couldn't list the apps", e);
                s_ListError = e.toString();
            } finally {
                synchronized (s_Lock) {
                    s_Refreshing = false;
                }
            }
        }, "CardboardXR apps");
        // Behind Unity's threads and the hand models.
        thread.setPriority(Thread.MIN_PRIORITY);
        thread.start();
    }

    // Goes up each time a new list is ready.
    public static int version() { return s_Version; }

    // The latest list, or null before the first is ready.
    public static AppList list() { return s_List; }

    // Why the last listing failed, or "".
    public static String listError() { return s_ListError; }

    // An app's icon as PNG, or null until it has loaded.
    public static byte[] iconPng(String key) { return key == null ? null : s_Icons.get(key); }

    private static void list(Context context, int iconSize) {
        PackageManager packages = context.getPackageManager();
        Intent launcher = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER);
        // Needs the <queries> entry for launcher activities in the manifest from Android 11.
        List<ResolveInfo> found = packages.queryIntentActivities(launcher, 0);
        List<Entry> entries = new ArrayList<>(found.size());
        String own = context.getPackageName();
        for (ResolveInfo info : found) {
            ActivityInfo activity = info.activityInfo;
            if (activity == null || own.equals(activity.packageName))
                continue;
            CharSequence label = info.loadLabel(packages);
            String text = label != null ? label.toString().trim() : "";
            String key = new ComponentName(activity.packageName, activity.name).flattenToString();
            entries.add(new Entry(key, text.isEmpty() ? activity.packageName : text, activity.screenOrientation, info));
        }
        // As the launcher sorts them: by name, ignoring case and accents, in the phone's language.
        final Collator collator = Collator.getInstance();
        collator.setStrength(Collator.PRIMARY);
        Collections.sort(entries, (a, b) -> collator.compare(a.label, b.label));

        int count = entries.size();
        String[] keys = new String[count];
        String[] labels = new String[count];
        int[] orientations = new int[count];
        for (int i = 0; i < count; i++) {
            Entry entry = entries.get(i);
            keys[i] = entry.key;
            labels[i] = entry.label;
            orientations[i] = entry.orientation;
        }
        s_List = new AppList(keys, labels, orientations);
        s_ListError = "";
        s_Version = s_Version + 1;

        for (Entry entry : entries) {
            if (s_Icons.containsKey(entry.key))
                continue;
            byte[] png = drawIcon(entry.info, packages, iconSize);
            if (png != null)
                s_Icons.put(entry.key, png);
        }
    }

    // The icon as the launcher shows it (adaptive icons in the phone's icon shape), size pixels across.
    private static byte[] drawIcon(ResolveInfo info, PackageManager packages, int size) {
        try {
            Drawable drawable = info.loadIcon(packages);
            if (drawable == null)
                return null;
            Bitmap bitmap = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888);
            Canvas canvas = new Canvas(bitmap);
            drawable.setBounds(0, 0, size, size);
            drawable.draw(canvas);
            ByteArrayOutputStream png = new ByteArrayOutputStream(size * size);
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, png);
            bitmap.recycle();
            return png.toByteArray();
        } catch (RuntimeException e) {
            Log.w(TAG, "Couldn't draw the icon of " + info.activityInfo.packageName, e);
            return null;
        }
    }

    // ---- Opening apps ----

    // Opens the app by its key, keeping it in landscape if asked and allowed. Returns at once; status()
    // says how it went.
    public static void launch(final Activity activity, final String key, final String label, final boolean landscape) {
        s_Status = "pending";
        MAIN.post(() -> {
            ComponentName component = ComponentName.unflattenFromString(key);
            if (component == null) {
                s_Status = "failed: not an app's launcher activity";
                return;
            }
            final Intent intent = new Intent(Intent.ACTION_MAIN)
                    .addCategory(Intent.CATEGORY_LAUNCHER)
                    .setComponent(component)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_RESET_TASK_IF_NEEDED);
            final Context app = activity.getApplicationContext();
            if (!landscape) {
                releaseNow(app, "an app opened as it is");
                start(activity, intent, "opened as it is");
                return;
            }
            if (!Settings.canDrawOverlays(app)) {
                start(activity, intent, "opened upright: CardboardXR isn't allowed to display over other apps");
                return;
            }
            boolean wasHolding = s_Overlay != null;
            if (!hold(app, label)) {
                start(activity, intent, "opened upright: the landscape overlay couldn't be added");
                return;
            }
            if (wasHolding)
                start(activity, intent, "opened in landscape");
            else
                afterFirstLayout(s_Overlay, () -> start(activity, intent, "opened in landscape"));
        });
    }

    // How the last launch went: "pending", "opened ...", or "failed: ...".
    public static String status() { return s_Status; }

    // Whether landscape is being held now.
    public static boolean isHolding() { return s_Holding; }

    // What ended the last hold, if it ended other than by release() from CardboardXR, once; else "".
    public static String takeReleased() {
        String released = s_Released;
        s_Released = "";
        return released;
    }

    // Ends the hold, if there is one, and takes down its notification.
    public static void release(Context context) {
        final Context app = context.getApplicationContext();
        MAIN.post(() -> releaseNow(app, ""));
    }

    private static void start(Activity activity, Intent intent, String outcome) {
        try {
            activity.startActivity(intent);
            s_Status = outcome;
        } catch (RuntimeException e) {
            // The exception's class only: its message names the app.
            releaseNow(activity.getApplicationContext(), "");
            s_Status = "failed: " + e.getClass().getSimpleName();
        }
    }

    // Once the window has been laid out, and so has its surface and the display its orientation, or
    // after LAYOUT_TIMEOUT_MS, whichever comes first.
    private static void afterFirstLayout(final View view, final Runnable action) {
        final boolean[] done = { false };
        final Runnable once = () -> {
            if (done[0])
                return;
            done[0] = true;
            action.run();
        };
        view.getViewTreeObserver().addOnGlobalLayoutListener(new ViewTreeObserver.OnGlobalLayoutListener() {
            @Override
            public void onGlobalLayout() {
                view.getViewTreeObserver().removeOnGlobalLayoutListener(this);
                // After this layout's frame, when the window manager has the window.
                MAIN.post(once);
            }
        });
        MAIN.postDelayed(once, LAYOUT_TIMEOUT_MS);
    }

    // Adds the landscape overlay (if it isn't there) and shows the notification. Main thread.
    private static boolean hold(Context context, String label) {
        if (s_Overlay == null) {
            WindowManager windows = context.getSystemService(WindowManager.class);
            if (windows == null)
                return false;
            View view = new View(context);
            WindowManager.LayoutParams params = new WindowManager.LayoutParams(1, 1,
                    WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
                    WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE | WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE,
                    PixelFormat.TRANSLUCENT);
            params.gravity = Gravity.TOP | Gravity.START;
            // Either way round, by the accelerometer: landscape-left in a viewer, whichever way it's
            // held in the hand.
            params.screenOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE;
            params.setTitle("CardboardXR landscape");
            try {
                windows.addView(view, params);
            } catch (RuntimeException e) {
                Log.w(TAG, "Couldn't add the landscape overlay", e);
                return false;
            }
            s_Overlay = view;
            s_Holding = true;
            listen(context);
        }
        notifyHolding(context, label);
        return true;
    }

    // The screen going off, and the notification's tap or dismissal, end the hold.
    private static void listen(Context context) {
        s_Receiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context receiver, Intent intent) {
                boolean screenOff = Intent.ACTION_SCREEN_OFF.equals(intent.getAction());
                releaseNow(receiver.getApplicationContext(), screenOff ? "the screen went off" : "the notification");
            }
        };
        IntentFilter filter = new IntentFilter(Intent.ACTION_SCREEN_OFF);
        filter.addAction(ACTION_STOP);
        // Only the system and this app's own notification can send these.
        int flags = Build.VERSION.SDK_INT >= 33 ? Context.RECEIVER_NOT_EXPORTED : 0;
        try {
            context.registerReceiver(s_Receiver, filter, flags);
        } catch (RuntimeException e) {
            Log.w(TAG, "Couldn't listen for the screen going off", e);
            s_Receiver = null;
        }
    }

    // Main thread. why: what ended it, kept for takeReleased(); "" when CardboardXR asked.
    private static void releaseNow(Context context, String why) {
        if (s_Overlay != null) {
            WindowManager windows = context.getSystemService(WindowManager.class);
            try {
                if (windows != null)
                    windows.removeView(s_Overlay);
            } catch (RuntimeException e) {
                Log.w(TAG, "Couldn't remove the landscape overlay", e);
            }
            s_Overlay = null;
            s_Holding = false;
            if (!why.isEmpty())
                s_Released = why;
        }
        if (s_Receiver != null) {
            try {
                context.unregisterReceiver(s_Receiver);
            } catch (RuntimeException e) {
                Log.w(TAG, "Couldn't stop listening for the screen going off", e);
            }
            s_Receiver = null;
        }
        NotificationManager notifications = context.getSystemService(NotificationManager.class);
        if (notifications != null)
            notifications.cancel(NOTIFICATION_ID);
    }

    // Shown while holding, if notifications are allowed: tapping or swiping it away ends the hold, the
    // way out for an app left by the home button.
    private static void notifyHolding(Context context, String label) {
        NotificationManager notifications = context.getSystemService(NotificationManager.class);
        if (notifications == null || !notifications.areNotificationsEnabled())
            return;
        NotificationChannel channel = new NotificationChannel(CHANNEL, "Apps kept in landscape",
                NotificationManager.IMPORTANCE_LOW);
        channel.setDescription("Shows while CardboardXR keeps an app it opened in landscape");
        channel.setShowBadge(false);
        notifications.createNotificationChannel(channel);
        Intent stopIntent = new Intent(ACTION_STOP).setPackage(context.getPackageName());
        PendingIntent stop = PendingIntent.getBroadcast(context, 0, stopIntent,
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        Notification notification = new Notification.Builder(context, CHANNEL)
                .setSmallIcon(android.R.drawable.ic_menu_rotate)
                .setContentTitle(label + " stays in landscape")
                .setContentText("Tap to let apps turn freely again")
                .setContentIntent(stop)
                .setDeleteIntent(stop)
                .setOnlyAlertOnce(true)
                .setCategory(Notification.CATEGORY_STATUS)
                .build();
        try {
            notifications.notify(NOTIFICATION_ID, notification);
        } catch (RuntimeException e) {
            Log.w(TAG, "Couldn't show the landscape notification", e);
        }
    }

    // ---- Permissions ----

    // Whether CardboardXR may display over other apps, which holding landscape needs.
    public static boolean canHoldLandscape(Context context) {
        return Settings.canDrawOverlays(context);
    }

    // Whether to ask for the notification permission before holding: Android 13 and later need it
    // granted for the notification that ends the hold.
    public static boolean shouldAskForNotifications(Context context) {
        if (Build.VERSION.SDK_INT < 33)
            return false;
        NotificationManager notifications = context.getSystemService(NotificationManager.class);
        return notifications != null && !notifications.areNotificationsEnabled();
    }

    // Opens Settings at CardboardXR's "Display over other apps" switch. Returns whether it opened.
    public static boolean openOverlaySettings(Activity activity) {
        Intent intent = new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                Uri.parse("package:" + activity.getPackageName()));
        try {
            activity.startActivity(intent);
            return true;
        } catch (RuntimeException e) {
            Log.w(TAG, "Couldn't open CardboardXR's overlay setting; opening the list", e);
        }
        try {
            activity.startActivity(new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION));
            return true;
        } catch (RuntimeException e) {
            Log.w(TAG, "Couldn't open the overlay settings", e);
            return false;
        }
    }
}
