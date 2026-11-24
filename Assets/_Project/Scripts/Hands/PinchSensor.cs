using System;
using System.Diagnostics;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using Unity.Collections;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace CardboardXR.Hands
{
    // A pinch as a click, for 3DoF, without hand tracking's cost. Where HandTracker places the hand
    // in 3D (which needs the AR session running, depth, and the landmarker on every image), this only
    // asks whether the thumb and index fingertips are touching, which the camera image alone answers:
    // their gap over the palm's width there, about 0.23 pinched and 1.8-2.1 open in the pinch study.
    // So it reads the phone camera's images (PhoneCamera, shared with the passthrough view, from
    // whichever lens is chosen; no ARKit or ARCore) at up to m_ReadsPerSecond, drawn small, runs the
    // same MediaPipe models on them, and a pinch clicks whatever the gaze is on, as a screen tap does.
    // It follows up to two hands: the palm detector runs while fewer are followed (every
    // m_SearchInterval with none, m_SecondHandInterval with one), and the landmarker on each followed
    // hand. The first hand to pinch clicks (PinchRouter); a second pinching while it's held makes a
    // two-handed pinch (BothPinched), which the app's screen uses to move and resize itself. Each
    // pinch also has a direction in the world (TryGetPinchDirection): where its fingertips were seen,
    // through the image's field of view and the head's rotation when the image was taken. A click
    // wants ~100 ms, not the dragging that made a slower landmarker feel laggy. A hand has to be seen
    // in k_SteadyResults results in a row before its pinch counts, so one passing through view doesn't
    // click. It can't run alongside the AR session, which holds the camera, so it stays off in 6DoF.
    // Enable it to start. Logs [Pinch] every five seconds, with what it costs.
    public class PinchSensor : MonoBehaviour
    {
        const int k_LandmarkerSize = 224;
        const int k_MaxHands = 2;
        static readonly (string name, int index) k_BoxesOutput = ("Identity", 0);
        static readonly (string name, int index) k_ScoresOutput = ("Identity_1", 1);
        static readonly (string name, int index) k_LandmarksOutput = ("Identity", 0);
        static readonly (string name, int index) k_PresenceOutput = ("Identity_1", 1);
        static readonly (string name, int index) k_HandednessOutput = ("Identity_2", 2);
        // The fingertip gap over the palm's width in the image: closer than k_CloseRatio is a pinch,
        // and it lasts until they're farther apart than k_OpenRatio.
        public const float k_CloseRatio = 0.4f;
        public const float k_OpenRatio = 0.8f;
        // A hand must have been seen in more than this many results in a row before a pinch counts.
        public const int k_SteadyResults = 2;
        const float k_StatusSeconds = 5f;

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] GazePointer m_Gaze;
        [SerializeField] ModelAsset m_Detector;
        [SerializeField] ModelAsset m_Landmarker;
        [Tooltip("How wide the camera image is drawn for the models, which see at most 640 wide anyway.")]
        [SerializeField] int m_ModelWidth = 640;
        [Tooltip("At most this many camera images a second are read for the models.")]
        [SerializeField] int m_ReadsPerSecond = 20;
        [Tooltip("While no hand is followed, the palm detector runs at most this often, in seconds.")]
        [SerializeField] float m_SearchInterval = 0.2f;
        [Tooltip("While one hand is followed, the palm detector looks for a second at most this often, in seconds.")]
        [SerializeField] float m_SecondHandInterval = 0.5f;
        [SerializeField, Range(0f, 1f)] float m_MinPalmScore = 0.5f;
        [SerializeField, Range(0f, 1f)] float m_MinHandPresence = 0.5f;

        // One followed hand.
        class Hand
        {
            public HandRegion? Region;
            public readonly PinchRule Rule = new();
            public readonly HandTurnGate Turn = new();
            public readonly Vector2[] Landmarks = new Vector2[HandGeometry.JointCount];
            public float RightHandScore = 0.5f;
            // Its pinch went to PinchRouter as a click.
            public bool Clicks;
            // Where its fingertips met, over the image's size (0 to 1, x right, y down), in the image
            // taken at TimestampNs.
            public Vector2 PinchPoint;
            public long TimestampNs;
            public bool Seen => Region != null;
        }

        public static PinchSensor Instance { get; private set; }

        readonly Hand[] m_Hands = { new(), new() };
        readonly System.Collections.Generic.List<Palm> m_Palms = new();
        Worker m_DetectorWorker;
        Worker m_LandmarkerWorker;
        int m_BoxesIndex, m_ScoresIndex, m_LandmarksIndex, m_PresenceIndex, m_HandednessIndex;
        Tensor<float> m_DetectorInput;
        Tensor<float> m_LandmarkerInput;
        float[] m_DetectorPixels;
        float[] m_LandmarkerPixels;
        float[] m_Boxes;
        float[] m_Scores;
        readonly float[] m_LandmarkValues = new float[HandGeometry.JointCount * 3];
        Vector2[] m_Anchors;
        // The frame the models are working on, and the newest one read since, which they swap in when
        // they're done (see Update and Step): reading the camera never waits for the models, nor
        // they for it. On a Pixel 9 Pro, taking turns left 2-5 frames a second analyzed.
        NativeArray<byte> m_Image;
        NativeArray<byte> m_NextImage;
        int m_Width, m_Height;
        int m_NextWidth, m_NextHeight;
        long m_ImageTimestampNs, m_NextTimestampNs, m_PendingTimestampNs;
        bool m_HasNextImage;
        bool m_ReadbackInFlight;
        bool m_Running;
        bool m_LoopActive;
        bool m_LoggedCamera;
        bool m_HasCamera;
        // Frames are read back from the GPU without stalling the main thread where the target's
        // format allows it; otherwise with ReadPixels, which does stall.
        bool m_AsyncReadback = true;
        RenderTexture m_Small;
        Texture2D m_ReadTexture;
        int m_LastVersion;
        float m_NextRead;
        bool m_BothPinched;
        float m_NextSearch;

        // Statistics for the status line.
        int m_Reads, m_Frames, m_Detections, m_Landmarks, m_Losses, m_Pinches, m_TwoHanded;
        double m_ReadMs, m_DetectMs, m_LandmarkMs;
        float m_MinRatio = float.PositiveInfinity, m_MaxRatio;
        float m_StatusStart;

        // A pinch that clicks is held.
        public bool IsPinched
        {
            get
            {
                foreach (var hand in m_Hands)
                {
                    if (hand.Clicks)
                        return true;
                }
                return false;
            }
        }

        public bool SeesHand => m_Hands[0].Seen || m_Hands[1].Seen;

        // Both hands are pinching: the first one's pinch clicked, and the second came while it was held.
        public bool BothPinched => m_BothPinched;
        public event Action<bool> BothPinchedChanged;

        // The first hand seen's landmarks in the camera image, in pixels (x right, y down, upright on
        // the screen), the image's size, and the chance it's a right hand: for which way the palm faces
        // (PalmGesture) and where the hand is.
        public System.Collections.Generic.IReadOnlyList<Vector2> ImageLandmarks => FirstSeen().Landmarks;
        public Vector2 ImageSize => new(m_Width, m_Height);
        public float RightHandScore => FirstSeen().RightHandScore;

        Hand FirstSeen() => m_Hands[0].Seen || !m_Hands[1].Seen ? m_Hands[0] : m_Hands[1];

        // Which way the pinch is in the world, from where the fingertips met in the latest image:
        // which 0 for the pinch that clicked, 1 for the other. False if there's no such pinch.
        public bool TryGetPinchDirection(int which, out Vector3 direction)
        {
            direction = Vector3.forward;
            Hand clicking = null, other = null;
            foreach (var hand in m_Hands)
            {
                if (!hand.Rule.IsPinched)
                    continue;
                if (hand.Clicks)
                    clicking = hand;
                else
                    other = hand;
            }
            var chosen = which == 0 ? clicking : other;
            var camera = PhoneCamera.Instance;
            return chosen != null && camera != null && camera.TryGetWorldDirection(chosen.PinchPoint, chosen.TimestampNs, out direction);
        }

        void Awake() => Instance = this;

        void OnEnable()
        {
            if (m_Fusion != null && m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof)
            {
                Debug.LogWarning("[Pinch] Not starting in 6DoF: the AR session has the camera. Hand tracking covers pinches there");
                enabled = false;
                return;
            }
            m_Running = true;
            m_StatusStart = Time.unscaledTime;
            if (!m_LoopActive)
                RunLoop();
        }

        void OnDisable()
        {
            m_Running = false;
            // Now, not when the loop next wakes: the AR session may be about to open the camera.
            ReleaseCamera();
            foreach (var hand in m_Hands)
                Forget(hand);
            SetBothPinched(false);
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        async void RunLoop()
        {
            m_LoopActive = true;
            try
            {
                if (!StartCamera())
                    return;
                Setup();
                Debug.Log($"[Pinch] On: up to {k_MaxHands} hands in the phone camera's images, read at up to {m_ReadsPerSecond}/s " +
                          $"{m_ModelWidth} wide; a pinch closes below {k_CloseRatio:F2} palm widths and opens above {k_OpenRatio:F2}");
                while (m_Running)
                {
                    // Also leaves if something turned 6DoF on, which wants the camera.
                    if (m_Fusion != null && m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof)
                    {
                        Debug.Log("[Pinch] Off: 6DoF wants the camera");
                        enabled = false;
                        break;
                    }
                    if (!await Step())
                        await Awaitable.NextFrameAsync();
                    LogStatus();
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                Teardown();
                m_LoopActive = false;
                Debug.Log("[Pinch] Off");
            }
        }

        bool StartCamera()
        {
            var camera = PhoneCamera.Instance;
            if (camera == null)
            {
                Debug.LogWarning("[Pinch] No phone camera");
                enabled = false;
                return false;
            }
            camera.Acquire(this);
            m_HasCamera = true;
            m_LastVersion = 0;
            return true;
        }

        void ReleaseCamera()
        {
            if (!m_HasCamera)
                return;
            m_HasCamera = false;
            if (PhoneCamera.Instance != null)
                PhoneCamera.Instance.Release(this);
        }

        void Setup()
        {
            m_Anchors = PalmDecoder.CreateAnchors();
            var detector = ModelLoader.Load(m_Detector);
            var landmarker = ModelLoader.Load(m_Landmarker);
            m_BoxesIndex = OutputIndex(detector, k_BoxesOutput);
            m_ScoresIndex = OutputIndex(detector, k_ScoresOutput);
            m_LandmarksIndex = OutputIndex(landmarker, k_LandmarksOutput);
            m_PresenceIndex = OutputIndex(landmarker, k_PresenceOutput);
            m_HandednessIndex = OutputIndex(landmarker, k_HandednessOutput);
            m_DetectorWorker = new Worker(detector, BackendType.CPU);
            m_LandmarkerWorker = new Worker(landmarker, BackendType.CPU);
            m_DetectorInput = new Tensor<float>(new TensorShape(1, PalmDecoder.InputSize, PalmDecoder.InputSize, 3));
            m_LandmarkerInput = new Tensor<float>(new TensorShape(1, k_LandmarkerSize, k_LandmarkerSize, 3));
            m_DetectorPixels = new float[PalmDecoder.InputSize * PalmDecoder.InputSize * 3];
            m_LandmarkerPixels = new float[k_LandmarkerSize * k_LandmarkerSize * 3];
            m_Boxes = new float[PalmDecoder.AnchorCount * PalmDecoder.ValuesPerAnchor];
            m_Scores = new float[PalmDecoder.AnchorCount];
        }

        void Teardown()
        {
            ReleaseCamera();
            m_DetectorWorker?.Dispose();
            m_LandmarkerWorker?.Dispose();
            m_DetectorInput?.Dispose();
            m_LandmarkerInput?.Dispose();
            m_DetectorWorker = m_LandmarkerWorker = null;
            m_DetectorInput = m_LandmarkerInput = null;
            if (m_Image.IsCreated)
                m_Image.Dispose();
            if (m_NextImage.IsCreated)
                m_NextImage.Dispose();
            if (m_Small != null)
            {
                m_Small.Release();
                Destroy(m_Small);
                m_Small = null;
            }
            if (m_ReadTexture != null)
            {
                Destroy(m_ReadTexture);
                m_ReadTexture = null;
            }
            m_HasNextImage = false;
            m_ReadbackInFlight = false;
        }

        static int OutputIndex(Model model, (string name, int index) output)
        {
            for (var i = 0; i < model.outputs.Count; i++)
            {
                if (model.outputs[i].name == output.name)
                    return i;
            }
            return output.index;
        }

        int FollowedCount() => (m_Hands[0].Seen ? 1 : 0) + (m_Hands[1].Seen ? 1 : 0);

        // Runs the newest camera frame through the models. False if there was no new frame.
        async Awaitable<bool> Step()
        {
            if (!m_HasNextImage)
                return false;
            var followed = FollowedCount();
            if (followed == 0 && Time.unscaledTime < m_NextSearch)
                return false;

            // The newest frame becomes the one worked on; the old buffer takes the next read.
            (m_Image, m_NextImage) = (m_NextImage, m_Image);
            m_Width = m_NextWidth;
            m_Height = m_NextHeight;
            m_ImageTimestampNs = m_NextTimestampNs;
            m_HasNextImage = false;
            m_Frames++;
            var stopwatch = Stopwatch.StartNew();

            if (followed < k_MaxHands && Time.unscaledTime >= m_NextSearch)
            {
                stopwatch.Restart();
                var letterbox = Letterbox.Fit(m_Width, m_Height, PalmDecoder.InputSize);
                ImageSampler.SampleLetterbox(m_Image, m_Width, m_Height, letterbox, m_DetectorPixels);
                m_DetectorInput.Upload(m_DetectorPixels);
                m_DetectorWorker.Schedule(m_DetectorInput);
                using (var scores = await (m_DetectorWorker.PeekOutput(m_ScoresIndex) as Tensor<float>).ReadbackAndCloneAsync())
                    CopyTo(scores, m_Scores);
                using (var boxes = (m_DetectorWorker.PeekOutput(m_BoxesIndex) as Tensor<float>).ReadbackAndClone())
                    CopyTo(boxes, m_Boxes);
                m_DetectMs += stopwatch.Elapsed.TotalMilliseconds;
                m_Detections++;
                if (!m_Running)
                    return true;
                PalmDecoder.DecodeTop(m_Boxes, m_Scores, m_Anchors, m_MinPalmScore, k_MaxHands, m_Palms);
                foreach (var palm in m_Palms)
                {
                    var region = HandRegion.FromPalm(palm, letterbox);
                    var free = FreeSlotFor(region);
                    if (free != null)
                        free.Region = region;
                }
                followed = FollowedCount();
                m_NextSearch = Time.unscaledTime + (followed == 0 ? m_SearchInterval : m_SecondHandInterval);
                if (followed == 0)
                    return true;
            }

            foreach (var hand in m_Hands)
            {
                if (!hand.Seen)
                    continue;
                await Landmark(hand, stopwatch);
                if (!m_Running)
                    return true;
            }
            // Two followed regions that closed in on the same hand: keep one.
            if (m_Hands[0].Seen && m_Hands[1].Seen && SameHand(m_Hands[0].Region.Value, m_Hands[1].Region.Value))
                Lose(m_Hands[1]);
            UpdateBothPinched();
            return true;
        }

        // A slot for a newly found palm, unless it's a hand already followed.
        Hand FreeSlotFor(HandRegion region)
        {
            Hand free = null;
            foreach (var hand in m_Hands)
            {
                if (hand.Seen)
                {
                    if (SameHand(hand.Region.Value, region))
                        return null;
                }
                else
                {
                    free ??= hand;
                }
            }
            return free;
        }

        // Regions whose middles are closer than half their size are one hand.
        public static bool SameHand(HandRegion a, HandRegion b) =>
            Vector2.Distance(a.center, b.center) < 0.25f * (a.size + b.size);

        async Awaitable Landmark(Hand hand, Stopwatch stopwatch)
        {
            stopwatch.Restart();
            var region = hand.Region.Value;
            ImageSampler.SampleRegion(m_Image, m_Width, m_Height, region, k_LandmarkerSize, m_LandmarkerPixels);
            m_LandmarkerInput.Upload(m_LandmarkerPixels);
            m_LandmarkerWorker.Schedule(m_LandmarkerInput);
            using (var landmarks = await (m_LandmarkerWorker.PeekOutput(m_LandmarksIndex) as Tensor<float>).ReadbackAndCloneAsync())
                CopyTo(landmarks, m_LandmarkValues);
            float presence, handedness;
            using (var output = (m_LandmarkerWorker.PeekOutput(m_PresenceIndex) as Tensor<float>).ReadbackAndClone())
                presence = output[0];
            using (var output = (m_LandmarkerWorker.PeekOutput(m_HandednessIndex) as Tensor<float>).ReadbackAndClone())
                handedness = output[0];
            // The model may give a probability or a logit.
            hand.RightHandScore = handedness < 0f || handedness > 1f ? PalmDecoder.Sigmoid(handedness) : handedness;
            m_LandmarkMs += stopwatch.Elapsed.TotalMilliseconds;
            m_Landmarks++;
            if (!m_Running)
                return;

            if (presence < 0f || presence > 1f)
                presence = PalmDecoder.Sigmoid(presence);
            if (presence < m_MinHandPresence)
            {
                Lose(hand);
                return;
            }

            var points = hand.Landmarks;
            for (var i = 0; i < HandGeometry.JointCount; i++)
            {
                var crop = new Vector2(m_LandmarkValues[i * 3], m_LandmarkValues[i * 3 + 1]) / k_LandmarkerSize;
                points[i] = region.CropToImage(crop);
            }
            hand.Region = HandRegion.FromLandmarks(points);
            var thumb = points[HandGeometry.ThumbTip];
            var index = points[HandGeometry.IndexTip];
            hand.PinchPoint = new Vector2((thumb.x + index.x) / 2f / Mathf.Max(1, m_Width), (thumb.y + index.y) / 2f / Mathf.Max(1, m_Height));
            hand.TimestampNs = m_ImageTimestampNs;
            var ratio = Vector2.Distance(thumb, index) / Mathf.Max(1f, HandGeometry.PalmSpan(points));
            m_MinRatio = Mathf.Min(m_MinRatio, ratio);
            m_MaxRatio = Mathf.Max(m_MaxRatio, ratio);
            // No pinch starts while the hand turns over (HandTurnGate): edge-on, the fingertips meet in
            // the image too.
            var turning = hand.Turn.Update(PalmGesture.Facing(points[PalmGesture.k_Wrist],
                points[PalmGesture.k_IndexKnuckle], points[PalmGesture.k_PinkyKnuckle], hand.RightHandScore >= 0.5f),
                Time.unscaledTime);
            if (turning && !hand.Rule.IsPinched && ratio < k_CloseRatio)
            {
                Debug.Log($"[Pinch] Fingertips {ratio:F2} palm widths apart while the hand turned over; not a pinch");
                ratio = k_CloseRatio;
            }
            var change = hand.Rule.Update(true, ratio);
            if (change == PinchRule.Change.Closed)
            {
                m_Pinches++;
                if (!IsPinched)
                {
                    // At the moment of the pinch, as the viewer's button clicks: PinchRouter decides what
                    // it does, from opening settings to clicking what the gaze is on.
                    hand.Clicks = true;
                    Debug.Log($"[Pinch] Pinched ({ratio:F2} palm widths)");
                    PinchRouter.Begin(PinchSource.Camera, m_Gaze, false);
                }
                else
                {
                    Debug.Log($"[Pinch] Second hand pinched ({ratio:F2} palm widths)");
                }
            }
            else if (change == PinchRule.Change.Opened)
            {
                EndClick(hand);
            }
        }

        void EndClick(Hand hand)
        {
            if (!hand.Clicks)
                return;
            hand.Clicks = false;
            PinchRouter.End();
        }

        void UpdateBothPinched()
        {
            var clicking = false;
            var other = false;
            foreach (var hand in m_Hands)
            {
                if (!hand.Rule.IsPinched)
                    continue;
                if (hand.Clicks)
                    clicking = true;
                else
                    other = true;
            }
            SetBothPinched(clicking && other);
        }

        void SetBothPinched(bool both)
        {
            if (both == m_BothPinched)
                return;
            m_BothPinched = both;
            if (both)
                m_TwoHanded++;
            Debug.Log(both ? "[Pinch] Both hands pinching" : "[Pinch] Two-handed pinch ended");
            BothPinchedChanged?.Invoke(both);
        }

        void Lose(Hand hand)
        {
            if (hand.Region != null)
                m_Losses++;
            hand.Region = null;
            hand.Turn.Reset();
            if (hand.Rule.Update(false, 0f) == PinchRule.Change.Opened)
                EndClick(hand);
            UpdateBothPinched();
            if (FollowedCount() == 0)
                m_NextSearch = Time.unscaledTime + m_SearchInterval;
        }

        // Without a word, as the sensor stops.
        void Forget(Hand hand)
        {
            hand.Region = null;
            hand.Turn.Reset();
            if (hand.Clicks)
            {
                hand.Clicks = false;
                PinchRouter.End();
            }
            hand.Rule.Reset();
        }

        // Draws each new camera image small and reads it back into m_NextImage as RGBA with the top
        // row first, as the samplers expect, while the models work on m_Image. At most one read back
        // from the GPU at a time; a newer frame replaces one not yet taken.
        void Update()
        {
            var camera = PhoneCamera.Instance;
            if (!m_Running || !m_HasCamera || camera == null || !camera.HasImage || camera.Image == null)
                return;
            if (camera.ImageVersion == m_LastVersion || m_ReadbackInFlight || Time.unscaledTime < m_NextRead)
                return;
            m_LastVersion = camera.ImageVersion;
            m_NextRead = Time.unscaledTime + 1f / Mathf.Max(1, m_ReadsPerSecond);

            var stopwatch = Stopwatch.StartNew();
            EnsureSmall(camera.Image.width, camera.Image.height);
            if (!camera.DrawTo(m_Small, forModels: true))
                return;
            m_PendingTimestampNs = camera.ImageTimestampNs;
            if (m_AsyncReadback)
            {
                m_ReadbackInFlight = true;
                AsyncGPUReadback.Request(m_Small, 0, TextureFormat.RGBA32, OnReadback);
            }
            else
            {
                var previous = RenderTexture.active;
                RenderTexture.active = m_Small;
                m_ReadTexture.ReadPixels(new Rect(0f, 0f, m_Small.width, m_Small.height), 0, 0, false);
                RenderTexture.active = previous;
                TakeFrame(m_ReadTexture.GetRawTextureData<Color32>(), m_Small.width, m_Small.height, "read with ReadPixels");
            }
            m_ReadMs += stopwatch.Elapsed.TotalMilliseconds;
        }

        void EnsureSmall(int imageWidth, int imageHeight)
        {
            var width = Mathf.Min(m_ModelWidth, imageWidth);
            var height = Mathf.Max(16, Mathf.RoundToInt(width * imageHeight / (float)imageWidth));
            if (m_Small != null && m_Small.width == width && m_Small.height == height)
                return;
            if (m_Small != null)
            {
                m_Small.Release();
                Destroy(m_Small);
            }
            m_Small = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "Pinch Camera" };
            m_Small.Create();
            if (m_ReadTexture != null)
                Destroy(m_ReadTexture);
            m_ReadTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            m_AsyncReadback = SystemInfo.supportsAsyncGPUReadback &&
                              SystemInfo.IsFormatSupported(m_Small.graphicsFormat, GraphicsFormatUsage.ReadPixels);
        }

        void OnReadback(AsyncGPUReadbackRequest request)
        {
            m_ReadbackInFlight = false;
            if (!m_Running)
                return;
            if (request.hasError)
            {
                Debug.LogWarning("[Pinch] The GPU couldn't read the camera frame back; reading it the slower way from now on");
                m_AsyncReadback = false;
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            TakeFrame(request.GetData<Color32>(), request.width, request.height, "read back without waiting");
            m_ReadMs += stopwatch.Elapsed.TotalMilliseconds;
        }

        void TakeFrame(NativeArray<Color32> source, int width, int height, string how)
        {
            if (!m_NextImage.IsCreated || m_NextImage.Length != width * height * 4)
            {
                if (m_NextImage.IsCreated)
                    m_NextImage.Dispose();
                m_NextImage = new NativeArray<byte>(width * height * 4, Allocator.Persistent);
            }
            CopyRows(source, m_NextImage, width, height);
            m_NextWidth = width;
            m_NextHeight = height;
            m_NextTimestampNs = m_PendingTimestampNs;
            m_HasNextImage = true;
            m_Reads++;
            if (!m_LoggedCamera)
            {
                m_LoggedCamera = true;
                Debug.Log($"[Pinch] The models see the camera at {width}x{height}; frames {how}");
            }
        }

        // Color32 is laid out as RGBA bytes, which is what the samplers read, so rows copy whole. Both
        // readbacks give the bottom row first, and the image is already upright, so the rows are
        // turned over.
        static void CopyRows(NativeArray<Color32> source, NativeArray<byte> target, int width, int height)
        {
            var pixels = target.Reinterpret<Color32>(1);
            for (var y = 0; y < height; y++)
                NativeArray<Color32>.Copy(source, (height - 1 - y) * width, pixels, y * width, width);
        }

        static void CopyTo(Tensor<float> tensor, float[] values)
        {
            var count = Mathf.Min(values.Length, tensor.shape.length);
            for (var i = 0; i < count; i++)
                values[i] = tensor[i];
        }

        // What it cost, main thread and models, so it can be judged against a video playing.
        void LogStatus()
        {
            var seconds = Time.unscaledTime - m_StatusStart;
            if (seconds < k_StatusSeconds)
                return;
            Debug.Log($"[Pinch] {m_Reads / seconds:F1} frames/s read ({m_ReadMs / Mathf.Max(1, m_Reads):F1} ms each on the main thread), " +
                      $"{m_Frames / seconds:F1} analyzed, " +
                      $"detector {m_Detections} runs at {m_DetectMs / Mathf.Max(1, m_Detections):F0} ms, " +
                      $"landmarker {m_Landmarks} runs at {m_LandmarkMs / Mathf.Max(1, m_Landmarks):F0} ms (including waits); " +
                      $"{FollowedCount()} hands in view, {m_Losses} lost, {m_Pinches} pinches, {m_TwoHanded} two-handed, " +
                      (float.IsPositiveInfinity(m_MinRatio) ? "no fingertips seen" : $"fingertip gap {m_MinRatio:F2}-{m_MaxRatio:F2} palm widths"));
            m_Reads = m_Frames = m_Detections = m_Landmarks = m_Losses = m_Pinches = m_TwoHanded = 0;
            m_ReadMs = m_DetectMs = m_LandmarkMs = 0;
            m_MinRatio = float.PositiveInfinity;
            m_MaxRatio = 0f;
            m_StatusStart = Time.unscaledTime;
        }
    }

    // When the fingertip gap makes a pinch: closing below PinchSensor.k_CloseRatio palm widths,
    // opening above k_OpenRatio, and only for a hand seen in more than k_SteadyResults results in a row.
    public class PinchRule
    {
        public enum Change
        {
            None,
            Closed,
            Opened,
        }

        int m_Seen;

        public bool IsPinched { get; private set; }

        // One result: whether a hand was seen, and if so its fingertip gap in palm widths.
        public Change Update(bool handSeen, float ratio)
        {
            if (!handSeen)
            {
                m_Seen = 0;
                var was = IsPinched;
                IsPinched = false;
                return was ? Change.Opened : Change.None;
            }
            m_Seen++;
            if (!IsPinched && ratio < PinchSensor.k_CloseRatio && m_Seen > PinchSensor.k_SteadyResults)
            {
                IsPinched = true;
                return Change.Closed;
            }
            if (IsPinched && ratio > PinchSensor.k_OpenRatio)
            {
                IsPinched = false;
                return Change.Opened;
            }
            return Change.None;
        }

        public void Reset()
        {
            m_Seen = 0;
            IsPinched = false;
        }
    }
}
