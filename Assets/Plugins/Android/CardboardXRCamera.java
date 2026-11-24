package com.brenpoly.cardboardxr;

import android.app.Activity;
import android.content.Context;
import android.graphics.ImageFormat;
import android.graphics.Rect;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CameraMetadata;
import android.hardware.camera2.CaptureRequest;
import android.hardware.camera2.params.StreamConfigurationMap;
import android.media.Image;
import android.media.ImageReader;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Log;
import android.util.Range;
import android.util.Size;
import android.util.SizeF;
import android.view.Surface;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;

// The phone's rear cameras for 3DoF, opened directly with Camera2 (no ARCore), for the passthrough
// view and the pinch sensor (PhoneCamera.cs). Two parts:
//   - lenses(): the real rear lenses to choose from, one line each,
//     "cameraId;zoomRatio;magnification;tanAcross;tanUp": the camera to open, the CONTROL_ZOOM_RATIO
//     to ask it for, how much it magnifies against the main lens (1), and the tangents of half its
//     field of view along the sensor's long and short sides. A phone whose main rear camera is a
//     logical multi-camera (most with an ultra-wide or a telephoto) reaches its other lenses through
//     the zoom ratio: 0.5 makes the camera switch to the ultra-wide, 5 to a 5x telephoto, just as the
//     phone's own camera app does. Each physical lens gives the ratio that matches its field of view.
//     Rear cameras listed on their own (some phones list the ultra-wide that way) are opened by id.
//   - open()/close(): one stream of YUV_420_888 images, each copied, as soon as it arrives, into one
//     of three direct buffers as tightly packed luma (width x height bytes) then interleaved chroma
//     (U, V pairs at half size); Unity takes the newest with lockFrame(), uploads it from its memory
//     (frameBuffer) and gives it back with unlockFrame(). Each buffer keeps its image's timestamp.
// Everything runs on one thread of its own; status() and error() say where it's at. Logs to logcat
// (tag CardboardXR); PhoneCamera.cs logs the outcomes [Camera].
public final class CardboardXRCamera {
    private static final String TAG = "CardboardXR";
    private static final int BUFFERS = 3;

    public static final int CLOSED = 0;
    public static final int OPENING = 1;
    public static final int STREAMING = 2;
    public static final int FAILED = 3;

    private static final Object s_Lock = new Object();
    private static HandlerThread s_Thread;
    private static Handler s_Handler;

    private static CameraManager s_Manager;
    private static CameraDevice s_Device;
    private static CameraCaptureSession s_Session;
    private static ImageReader s_Reader;
    private static String s_CameraId;
    private static float s_ZoomRatio = 1f;
    private static int s_Fps = 30;
    private static int s_Request;
    private static volatile int s_Status = CLOSED;
    private static volatile String s_Error = "";

    // The stream's images: size, how they turn to stand upright on the screen, field of view.
    private static int s_Width;
    private static int s_Height;
    private static int s_Rotation;
    private static float s_TanAcross;
    private static float s_TanUp;
    private static int s_TimestampSource;

    private static ByteBuffer[] s_Buffers;
    private static final long[] s_Timestamps = new long[BUFFERS];
    private static int s_Generation;
    private static int s_Newest = -1;
    private static boolean s_NewestTaken = true;
    private static int s_Reading = -1;
    private static int s_Copied;
    private static int s_Skipped;
    private static byte[] s_YRow;
    private static byte[] s_URow;
    private static byte[] s_VRow;
    private static byte[] s_UvRow;

    private CardboardXRCamera() {}

    // ---- Lenses ----

    public static String lenses(Activity activity) {
        StringBuilder result = new StringBuilder();
        try {
            CameraManager manager = manager(activity);
            List<String> mainPhysical = new ArrayList<>();
            float mainTan = 0f;
            String mainId = null;
            for (String id : manager.getCameraIdList()) {
                CameraCharacteristics c = manager.getCameraCharacteristics(id);
                Integer facing = c.get(CameraCharacteristics.LENS_FACING);
                if (facing == null || facing != CameraMetadata.LENS_FACING_BACK)
                    continue;
                float[] tan = tangents(c);
                if (tan == null)
                    continue;
                if (mainId == null) {
                    // The first rear camera is the main one, at 1x.
                    mainId = id;
                    mainTan = tan[0];
                    List<float[]> options = new ArrayList<>();
                    options.add(new float[] { 1f, 1f, tan[0], tan[1] });
                    Range<Float> zoom = zoomRange(c);
                    if (zoom != null && Build.VERSION.SDK_INT >= 28) {
                        for (String physical : c.getPhysicalCameraIds()) {
                            mainPhysical.add(physical);
                            float[] p = tangents(manager.getCameraCharacteristics(physical));
                            if (p == null)
                                continue;
                            float ratio = tan[0] / p[0];
                            ratio = Math.max(zoom.getLower(), Math.min(zoom.getUpper(), ratio));
                            if (!near(options, ratio))
                                options.add(new float[] { ratio, ratio, tan[0] / ratio, tan[1] / ratio });
                        }
                    }
                    for (float[] o : options)
                        line(result, id, o[0], o[1], o[2], o[3]);
                    Log.i(TAG, "Camera: main rear camera " + id + ", zoom " + (zoom != null ? zoom.toString() : "fixed") +
                            ", lenses " + mainPhysical);
                    continue;
                }
                // Another rear camera listed on its own, unless it's one of the main camera's lenses.
                if (mainPhysical.contains(id))
                    continue;
                line(result, id, 1f, mainTan / tan[0], tan[0], tan[1]);
            }
        } catch (CameraAccessException | RuntimeException e) {
            Log.w(TAG, "Camera: couldn't list the cameras", e);
            s_Error = String.valueOf(e.getMessage());
        }
        return result.toString();
    }

    private static boolean near(List<float[]> options, float ratio) {
        for (float[] o : options) {
            if (Math.abs(o[0] - ratio) / Math.max(o[0], ratio) < 0.15f)
                return true;
        }
        return false;
    }

    private static void line(StringBuilder result, String id, float zoomRatio, float magnification, float tanAcross, float tanUp) {
        result.append(id).append(';')
                .append(String.format(Locale.ROOT, "%.4f;%.4f;%.5f;%.5f", zoomRatio, magnification, tanAcross, tanUp))
                .append('\n');
    }

    private static Range<Float> zoomRange(CameraCharacteristics c) {
        if (Build.VERSION.SDK_INT < 30)
            return null;
        Range<Float> range = c.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE);
        if (range == null || range.getUpper() - range.getLower() < 0.01f)
            return null;
        return range;
    }

    // Half the field of view as tangents, along the sensor's long side then its short side, of the
    // active pixels through the shortest focal length; null if the camera doesn't say.
    private static float[] tangents(CameraCharacteristics c) {
        float[] focal = c.get(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS);
        SizeF physical = c.get(CameraCharacteristics.SENSOR_INFO_PHYSICAL_SIZE);
        Size pixels = c.get(CameraCharacteristics.SENSOR_INFO_PIXEL_ARRAY_SIZE);
        Rect active = c.get(CameraCharacteristics.SENSOR_INFO_ACTIVE_ARRAY_SIZE);
        if (focal == null || focal.length == 0 || physical == null || pixels == null || active == null)
            return null;
        float f = focal[0];
        for (float candidate : focal)
            f = Math.min(f, candidate);
        if (f <= 0f || pixels.getWidth() <= 0 || pixels.getHeight() <= 0)
            return null;
        float width = physical.getWidth() * active.width() / (float) pixels.getWidth();
        float height = physical.getHeight() * active.height() / (float) pixels.getHeight();
        return new float[] { Math.max(width, height) / 2f / f, Math.min(width, height) / 2f / f };
    }

    private static CameraManager manager(Context context) {
        if (s_Manager == null)
            s_Manager = (CameraManager) context.getApplicationContext().getSystemService(Context.CAMERA_SERVICE);
        return s_Manager;
    }

    // ---- Stream ----

    // Opens the camera at about width x height and fps, or, if it's the one already streaming, only
    // changes its zoom ratio. Camera permission must already be granted.
    public static void open(Activity activity, String cameraId, float zoomRatio, int width, int height, int fps) {
        CameraManager manager = manager(activity);
        int displayRotation = displayDegrees(activity);
        Handler handler = handler();
        handler.post(() -> {
            if (s_Device != null && cameraId.equals(s_CameraId) && s_Status == STREAMING) {
                s_ZoomRatio = zoomRatio;
                applyZoomTangents(manager);
                repeat();
                Log.i(TAG, "Camera: zoom ratio " + zoomRatio + " on camera " + cameraId);
                return;
            }
            closeNow();
            s_CameraId = cameraId;
            s_ZoomRatio = zoomRatio;
            s_Fps = fps;
            s_Status = OPENING;
            s_Error = "";
            final int request = ++s_Request;
            try {
                CameraCharacteristics c = manager.getCameraCharacteristics(cameraId);
                StreamConfigurationMap map = c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
                Size size = pickSize(map.getOutputSizes(ImageFormat.YUV_420_888), width, height);
                Integer sensor = c.get(CameraCharacteristics.SENSOR_ORIENTATION);
                Integer source = c.get(CameraCharacteristics.SENSOR_INFO_TIMESTAMP_SOURCE);
                s_TimestampSource = source != null ? source : 0;
                // Back camera: turned clockwise by the sensor's angle less the display's.
                s_Rotation = ((sensor != null ? sensor : 90) - displayRotation + 360) % 360;
                frames(size.getWidth(), size.getHeight());
                applyZoomTangents(manager);
                Log.i(TAG, "Camera: opening " + cameraId + " at " + size + ", zoom ratio " + zoomRatio + ", " + fps +
                        " fps, turned " + s_Rotation + " deg, timestamps " + (s_TimestampSource == 1 ? "realtime" : "unknown base"));
                manager.openCamera(cameraId, new CameraDevice.StateCallback() {
                    @Override
                    public void onOpened(CameraDevice device) {
                        if (request != s_Request) {
                            device.close();
                            return;
                        }
                        s_Device = device;
                        startSession(c);
                    }

                    @Override
                    public void onDisconnected(CameraDevice device) {
                        device.close();
                        if (device == s_Device)
                            fail("disconnected (another app may have taken the camera)");
                    }

                    @Override
                    public void onError(CameraDevice device, int error) {
                        device.close();
                        if (request == s_Request)
                            fail("camera error " + error);
                    }
                }, handler);
            } catch (CameraAccessException | RuntimeException e) {
                fail(e.toString());
            }
        });
    }

    private static void startSession(CameraCharacteristics c) {
        try {
            final CameraDevice device = s_Device;
            Surface target = s_Reader.getSurface();
            device.createCaptureSession(Arrays.asList(target), new CameraCaptureSession.StateCallback() {
                @Override
                public void onConfigured(CameraCaptureSession session) {
                    if (device != s_Device) {
                        session.close();
                        return;
                    }
                    s_Session = session;
                    if (repeat()) {
                        s_Status = STREAMING;
                        Log.i(TAG, "Camera: streaming");
                    }
                }

                @Override
                public void onConfigureFailed(CameraCaptureSession session) {
                    fail("the camera couldn't be set up for this stream");
                }
            }, s_Handler);
        } catch (CameraAccessException | RuntimeException e) {
            fail(e.toString());
        }
    }

    // The repeating request: steady exposure at the frame rate where the camera allows it, so the view
    // never slows down in dim light, continuous focus, no video stabilization (it would move the image
    // against the head), and the zoom ratio.
    private static boolean repeat() {
        if (s_Device == null || s_Session == null)
            return false;
        try {
            CameraCharacteristics c = s_Manager.getCameraCharacteristics(s_CameraId);
            CaptureRequest.Builder builder = s_Device.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
            builder.addTarget(s_Reader.getSurface());
            builder.set(CaptureRequest.CONTROL_MODE, CameraMetadata.CONTROL_MODE_AUTO);
            builder.set(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE, CameraMetadata.CONTROL_VIDEO_STABILIZATION_MODE_OFF);
            int[] afModes = c.get(CameraCharacteristics.CONTROL_AF_AVAILABLE_MODES);
            if (contains(afModes, CameraMetadata.CONTROL_AF_MODE_CONTINUOUS_VIDEO))
                builder.set(CaptureRequest.CONTROL_AF_MODE, CameraMetadata.CONTROL_AF_MODE_CONTINUOUS_VIDEO);
            Range<Integer> fps = pickFps(c.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES), s_Fps);
            if (fps != null)
                builder.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, fps);
            if (Build.VERSION.SDK_INT >= 30 && zoomRange(c) != null)
                builder.set(CaptureRequest.CONTROL_ZOOM_RATIO, s_ZoomRatio);
            s_Session.setRepeatingRequest(builder.build(), null, s_Handler);
            return true;
        } catch (CameraAccessException | RuntimeException e) {
            fail(e.toString());
            return false;
        }
    }

    private static void applyZoomTangents(CameraManager manager) {
        try {
            CameraCharacteristics c = manager.getCameraCharacteristics(s_CameraId);
            float[] tan = tangents(c);
            if (tan == null)
                tan = new float[] { 0.6f, 0.45f };
            float ratio = Build.VERSION.SDK_INT >= 30 && zoomRange(c) != null ? s_ZoomRatio : 1f;
            float across = tan[0] / ratio;
            float up = tan[1] / ratio;
            // The stream is the middle of the sensor in its own shape: wider than the sensor keeps
            // its width and loses height, narrower the other way round.
            float aspect = s_Width / (float) Math.max(1, s_Height);
            if (aspect >= across / up)
                up = across / aspect;
            else
                across = up * aspect;
            synchronized (s_Lock) {
                s_TanAcross = across;
                s_TanUp = up;
            }
        } catch (CameraAccessException | RuntimeException e) {
            Log.w(TAG, "Camera: no field of view", e);
        }
    }

    private static boolean contains(int[] values, int value) {
        if (values == null)
            return false;
        for (int v : values) {
            if (v == value)
                return true;
        }
        return false;
    }

    // The range ending at fps with the highest start (steady), else the one ending nearest it.
    private static Range<Integer> pickFps(Range<Integer>[] ranges, int fps) {
        if (ranges == null || ranges.length == 0)
            return null;
        Range<Integer> best = null;
        for (Range<Integer> r : ranges) {
            if (best == null) {
                best = r;
                continue;
            }
            int distance = Math.abs(r.getUpper() - fps);
            int bestDistance = Math.abs(best.getUpper() - fps);
            if (distance < bestDistance || distance == bestDistance && r.getLower() > best.getLower())
                best = r;
        }
        return best;
    }

    // The size of the requested shape nearest the requested width, preferring one at least as wide.
    private static Size pickSize(Size[] sizes, int width, int height) {
        float aspect = width / (float) height;
        Size best = null;
        float bestScore = Float.MAX_VALUE;
        for (Size s : sizes) {
            float shape = Math.abs(s.getWidth() / (float) s.getHeight() - aspect);
            float scale = s.getWidth() >= width ? (s.getWidth() - width) / (float) width : 2f * (width - s.getWidth()) / (float) width;
            float score = shape * 4f + scale;
            if (score < bestScore) {
                bestScore = score;
                best = s;
            }
        }
        return best != null ? best : new Size(width, height);
    }

    private static int displayDegrees(Activity activity) {
        int rotation = activity.getWindowManager().getDefaultDisplay().getRotation();
        switch (rotation) {
            case Surface.ROTATION_90:
                return 90;
            case Surface.ROTATION_180:
                return 180;
            case Surface.ROTATION_270:
                return 270;
            default:
                return 0;
        }
    }

    public static void close() {
        handler().post(CardboardXRCamera::closeNow);
    }

    private static void closeNow() {
        s_Request++;
        if (s_Session != null) {
            try {
                s_Session.close();
            } catch (RuntimeException ignored) {
            }
            s_Session = null;
        }
        if (s_Device != null) {
            s_Device.close();
            s_Device = null;
        }
        closeFrames();
        if (s_Status != FAILED)
            s_Status = CLOSED;
    }

    private static void fail(String why) {
        Log.w(TAG, "Camera: " + why);
        s_Error = why;
        closeNow();
        s_Status = FAILED;
    }

    private static Handler handler() {
        synchronized (s_Lock) {
            if (s_Thread == null) {
                s_Thread = new HandlerThread("CardboardXRCamera");
                s_Thread.start();
                s_Handler = new Handler(s_Thread.getLooper());
            }
            return s_Handler;
        }
    }

    public static int status() {
        return s_Status;
    }

    public static String error() {
        return s_Error;
    }

    // ---- Frames ----

    private static void frames(int width, int height) {
        closeFrames();
        // Even sizes, so the chroma's half size is exact.
        width &= ~1;
        height &= ~1;
        ImageReader reader = ImageReader.newInstance(width, height, ImageFormat.YUV_420_888, 3);
        ByteBuffer[] buffers = new ByteBuffer[BUFFERS];
        for (int i = 0; i < BUFFERS; i++)
            buffers[i] = ByteBuffer.allocateDirect(width * height * 3 / 2).order(ByteOrder.nativeOrder());
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
        s_YRow = new byte[width];
        s_URow = new byte[width];
        s_VRow = new byte[width];
        s_UvRow = new byte[width];
        reader.setOnImageAvailableListener(CardboardXRCamera::copy, s_Handler);
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
            int target = -1;
            ByteBuffer destination;
            int width;
            int height;
            synchronized (s_Lock) {
                if (reader != s_Reader || s_Buffers == null)
                    return;
                for (int i = 0; i < BUFFERS && target < 0; i++) {
                    if (i != s_Reading && !(i == s_Newest && !s_NewestTaken))
                        target = i;
                }
                if (target < 0) {
                    s_Skipped++;
                    return;
                }
                destination = s_Buffers[target];
                width = s_Width;
                height = s_Height;
            }
            Image.Plane[] planes = image.getPlanes();
            destination.clear();
            copyLuma(planes[0], destination, width, height);
            copyChroma(planes[1], planes[2], destination, width / 2, height / 2);
            synchronized (s_Lock) {
                s_Timestamps[target] = image.getTimestamp();
                s_Newest = target;
                s_NewestTaken = false;
                s_Copied++;
            }
        } catch (RuntimeException e) {
            Log.w(TAG, "Camera: couldn't copy an image", e);
        } finally {
            image.close();
        }
    }

    private static void copyLuma(Image.Plane plane, ByteBuffer destination, int width, int height) {
        ByteBuffer source = plane.getBuffer();
        int stride = plane.getRowStride();
        if (stride == width && plane.getPixelStride() == 1) {
            source.position(0);
            source.limit(Math.min(source.capacity(), width * height));
            destination.put(source);
            return;
        }
        if (s_YRow.length < width)
            s_YRow = new byte[width];
        byte[] row = s_YRow;
        for (int y = 0; y < height; y++) {
            source.position(y * stride);
            source.get(row, 0, Math.min(width, source.remaining()));
            destination.put(row, 0, width);
        }
    }

    // As U, V pairs, whatever the planes' layout.
    private static void copyChroma(Image.Plane uPlane, Image.Plane vPlane, ByteBuffer destination, int width, int height) {
        ByteBuffer u = uPlane.getBuffer();
        ByteBuffer v = vPlane.getBuffer();
        int uStride = uPlane.getRowStride();
        int vStride = vPlane.getRowStride();
        int uStep = uPlane.getPixelStride();
        int vStep = vPlane.getPixelStride();
        int uLength = (width - 1) * uStep + 1;
        int vLength = (width - 1) * vStep + 1;
        if (s_URow.length < uLength)
            s_URow = new byte[uLength];
        if (s_VRow.length < vLength)
            s_VRow = new byte[vLength];
        if (s_UvRow.length < width * 2)
            s_UvRow = new byte[width * 2];
        byte[] uRow = s_URow;
        byte[] vRow = s_VRow;
        byte[] uv = s_UvRow;
        for (int y = 0; y < height; y++) {
            u.position(y * uStride);
            u.get(uRow, 0, Math.min(uLength, u.remaining()));
            v.position(y * vStride);
            v.get(vRow, 0, Math.min(vLength, v.remaining()));
            for (int x = 0, i = 0; x < width; x++, i += 2) {
                uv[i] = uRow[x * uStep];
                uv[i + 1] = vRow[x * vStep];
            }
            destination.put(uv, 0, width * 2);
        }
    }

    public static int frameGeneration() {
        synchronized (s_Lock) {
            return s_Generation;
        }
    }

    public static int frameWidth() {
        return s_Width;
    }

    public static int frameHeight() {
        return s_Height;
    }

    // Clockwise degrees that stand the image upright on the screen.
    public static int frameRotation() {
        return s_Rotation;
    }

    // Half the stream's field of view as tangents, along its width and its height.
    public static float frameTanAcross() {
        synchronized (s_Lock) {
            return s_TanAcross;
        }
    }

    public static float frameTanUp() {
        synchronized (s_Lock) {
            return s_TanUp;
        }
    }

    public static int timestampSource() {
        return s_TimestampSource;
    }

    public static ByteBuffer frameBuffer(int index) {
        synchronized (s_Lock) {
            return s_Buffers != null && index >= 0 && index < BUFFERS ? s_Buffers[index] : null;
        }
    }

    public static long frameTimestamp(int index) {
        synchronized (s_Lock) {
            return index >= 0 && index < BUFFERS ? s_Timestamps[index] : 0L;
        }
    }

    // The newest image's buffer if Unity hasn't had it, kept from the copier until unlockFrame; or
    // -1, also if the buffers aren't of that generation any more.
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

    // Images copied and dropped (Unity hadn't given a buffer back) since the last call.
    public static String takeCounts() {
        synchronized (s_Lock) {
            String counts = s_Copied + ";" + s_Skipped;
            s_Copied = 0;
            s_Skipped = 0;
            return counts;
        }
    }
}
