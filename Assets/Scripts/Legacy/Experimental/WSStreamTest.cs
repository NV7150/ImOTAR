using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.Collections;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using OpenCVForUnity.XimgprocModule;

public class WSStreamTest : MonoBehaviour {
    [Header("AR Input")]
    [SerializeField] private ARCameraManager cameraManager;
    [SerializeField] private RenderTexture output;

    [Header("Preprocess")]
    [SerializeField] private int blurKernelSize = 3; // odd, 0/1 to disable
    [SerializeField] private double thresh = 0; // 0 -> Otsu
    [SerializeField] private bool useOtsu = true;

    [Header("Superpixel Seeds (recommended)")]
    [SerializeField] private bool useSlicSeeds = true;
    [SerializeField] private int slicAlgorithm = Ximgproc.SLICO;
    [SerializeField] private int slicRegionSize = 12;
    [SerializeField] private float slicRuler = 10f;
    [SerializeField] private int slicIterate = 10;

    [Header("Unknown (edge) mask")]
    [SerializeField] private int gradApertureSize = 3; // Sobel ksize
    [SerializeField] private int unknownDilate = 1; // widen boundaries

    [Header("Blend with Original")]
    [SerializeField] private bool blendWithOriginal = true;
    [SerializeField, Range(0f, 1f)] private float blendAlpha = 0.5f; // original weight

    [Header("Diagnostics")]
    [SerializeField] private bool verboseLogs = true;
    [SerializeField] private bool throwOnError = false;

    private Texture2D cameraTexture;
    private Texture2D workingTexture;

    private void OnEnable() {
        if (cameraManager != null) {
            cameraManager.frameReceived += OnFrameReceived;
            if (verboseLogs) Debug.Log("[WSStreamTest] Subscribed to ARCameraManager frameReceived.");
        } else {
            Report("ARCameraManager is not assigned.", true);
        }
    }

    private void OnDisable() {
        if (cameraManager != null) {
            cameraManager.frameReceived -= OnFrameReceived;
            if (verboseLogs) Debug.Log("[WSStreamTest] Unsubscribed from ARCameraManager frameReceived.");
        }
    }

    private void OnFrameReceived(ARCameraFrameEventArgs args) {
        if (cameraManager == null) { Report("ARCameraManager is null on frameReceived.", true); return; }
        if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image)) { if (verboseLogs) Debug.Log("[WSStreamTest] TryAcquireLatestCpuImage failed (no CPU image available this frame)."); return; }

        using (image) {
            var conversionParams = new XRCpuImage.ConversionParams {
                inputRect = new RectInt(0, 0, image.width, image.height),
                outputDimensions = new Vector2Int(image.width, image.height),
                outputFormat = TextureFormat.RGBA32,
                transformation = XRCpuImage.Transformation.MirrorX
            };

            if (image.width <= 0 || image.height <= 0) { Report($"Invalid CPU image size: {image.width}x{image.height}.", true); return; }

            int dataSize = image.GetConvertedDataSize(conversionParams);
            var buffer = new NativeArray<byte>(dataSize, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            image.Convert(conversionParams, buffer);

            EnsureCameraTexture(image.width, image.height);
            cameraTexture.LoadRawTextureData(buffer);
            cameraTexture.Apply(false, false);
            buffer.Dispose();

            // Ensure output/working targets
            EnsureOutput(image.width, image.height);
            EnsureWorkingTexture(image.width, image.height);

            // Prepare Mats
            Mat srcRgba = new Mat(image.height, image.width, CvType.CV_8UC4);
            Mat srcBgr = new Mat(image.height, image.width, CvType.CV_8UC3);
            Mat resultRgba = new Mat(image.height, image.width, CvType.CV_8UC4);

            try {
                OpenCVMatUtils.Texture2DToMat(cameraTexture, srcRgba, flip: false);
                Imgproc.cvtColor(srcRgba, srcBgr, Imgproc.COLOR_RGBA2BGR);

                Mat colored = RunWatershed(srcBgr);

                if (blendWithOriginal) {
                    float a = Mathf.Clamp01(blendAlpha);
                    Core.addWeighted(srcBgr, a, colored, 1f - a, 0.0, colored);
                }

                Imgproc.cvtColor(colored, resultRgba, Imgproc.COLOR_BGR2RGBA);
                OpenCVMatUtils.MatToTexture2D(resultRgba, workingTexture, flip: false);
                if (verboseLogs) Debug.Log($"[WSStreamTest] After MatToTexture2D: workingTexture={workingTexture.width}x{workingTexture.height}");
                if (output == null) { Report("Output RenderTexture is null before blit.", true); return; }
                if (verboseLogs) Debug.Log($"[WSStreamTest] Before Blit: output={output.width}x{output.height}, IsCreated={output.IsCreated()}\n    srcTex valid={(workingTexture != null)}");
                Graphics.Blit(workingTexture, output);
                if (verboseLogs) Debug.Log("[WSStreamTest] Blit completed.");
            }
            finally {
                srcRgba.Dispose();
                srcBgr.Dispose();
                resultRgba.Dispose();
            }
        }
    }

    private Mat RunWatershed(Mat srcBgr) {
        int width = srcBgr.cols();
        int height = srcBgr.rows();

        Mat gray = new Mat();
        Imgproc.cvtColor(srcBgr, gray, Imgproc.COLOR_BGR2GRAY);

        int k = Mathf.Max(1, blurKernelSize);
        if (k % 2 == 0) k += 1;
        if (k > 1) Imgproc.GaussianBlur(gray, gray, new Size(k, k), 0);

        // Compute gradient magnitude and unknown mask by Otsu threshold
        Mat gx = new Mat();
        Mat gy = new Mat();
        Imgproc.Sobel(gray, gx, CvType.CV_32F, 1, 0, Mathf.Max(1, gradApertureSize));
        Imgproc.Sobel(gray, gy, CvType.CV_32F, 0, 1, Mathf.Max(1, gradApertureSize));
        Mat mag = new Mat();
        Core.magnitude(gx, gy, mag);
        Core.normalize(mag, mag, 0, 255, Core.NORM_MINMAX);
        Mat mag8 = new Mat();
        mag.convertTo(mag8, CvType.CV_8U);

        Mat unknown = new Mat();
        Imgproc.threshold(mag8, unknown, 0, 255, Imgproc.THRESH_BINARY | Imgproc.THRESH_OTSU);
        Mat kernel = Imgproc.getStructuringElement(Imgproc.MORPH_RECT, new Size(3, 3));
        if (unknownDilate > 0) Imgproc.dilate(unknown, unknown, kernel, new Point(-1, -1), unknownDilate);

        // Build markers
        Mat markers;
        if (useSlicSeeds) {
            SuperpixelSLIC slic = Ximgproc.createSuperpixelSLIC(srcBgr, slicAlgorithm, Mathf.Max(1, slicRegionSize), Mathf.Max(0.1f, slicRuler));
            slic.iterate(Mathf.Max(1, slicIterate));
            Mat labels = new Mat();
            slic.getLabels(labels); // CV_32S
            markers = labels.clone();
            Core.add(markers, new Scalar(1), markers);
            labels.Dispose();
        } else {
            // Fallback: connected components on non-edge regions
            Mat notUnknown = new Mat();
            Core.bitwise_not(unknown, notUnknown);
            Mat sureFg8U = new Mat();
            notUnknown.copyTo(sureFg8U);
            Imgproc.threshold(sureFg8U, sureFg8U, 1, 255, Imgproc.THRESH_BINARY);
            markers = new Mat();
            Imgproc.connectedComponents(sureFg8U, markers);
            Core.add(markers, new Scalar(1), markers);
            notUnknown.Dispose();
            sureFg8U.Dispose();
        }

        // Apply unknown mask: set to 0 where unknown>0
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                if (unknown.get(y, x)[0] != 0) markers.put(y, x, 0);
            }
        }

        Imgproc.watershed(srcBgr, markers);

        Mat colored = new Mat(height, width, CvType.CV_8UC3);
        int total = width * height;
        int[] labelData = new int[total];
        markers.get(0, 0, labelData);

        int maxLabel = 0;
        for (int i = 0; i < total; i++) if (labelData[i] > maxLabel) maxLabel = labelData[i];
        if (maxLabel < 1) maxLabel = 1;
        Scalar[] lut = new Scalar[maxLabel + 1];
        System.Random rng = new System.Random(12345);
        lut[0] = new Scalar(0, 0, 0);
        for (int i = 1; i <= maxLabel; i++) lut[i] = new Scalar(rng.Next(0, 256), rng.Next(0, 256), rng.Next(0, 256));

        byte[] bgr = new byte[total * 3];
        for (int i = 0; i < total; i++) {
            int lid = labelData[i];
            int bi = i * 3;
            if (lid == -1) {
                bgr[bi + 0] = 0; bgr[bi + 1] = 0; bgr[bi + 2] = 0;
            } else {
                if (lid < 0) lid = 0; if (lid > maxLabel) lid = maxLabel;
                Scalar c = lut[lid];
                bgr[bi + 0] = (byte)c.val[0];
                bgr[bi + 1] = (byte)c.val[1];
                bgr[bi + 2] = (byte)c.val[2];
            }
        }
        colored.put(0, 0, bgr);

        // cleanup small mats
        gray.Dispose(); gx.Dispose(); gy.Dispose(); mag.Dispose(); mag8.Dispose(); kernel.Dispose(); unknown.Dispose();

        return colored;
    }

    private void EnsureCameraTexture(int width, int height) {
        if (width <= 0 || height <= 0) { Report($"EnsureCameraTexture invalid size {width}x{height}.", true); return; }
        if (cameraTexture == null || cameraTexture.width != width || cameraTexture.height != height) {
            if (verboseLogs) Debug.Log($"[WSStreamTest] (Re)creating cameraTexture {width}x{height}.");
            try {
                cameraTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                cameraTexture.wrapMode = TextureWrapMode.Clamp;
                cameraTexture.filterMode = FilterMode.Bilinear;
            } catch (System.Exception ex) {
                Report($"Failed to create cameraTexture {width}x{height}: {ex.Message}", true);
            }
        }
    }

    private void EnsureOutput(int width, int height) {
        if (width <= 0 || height <= 0) { Report($"EnsureOutput invalid size {width}x{height}.", true); return; }
        if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32)) {
            Report("RenderTextureFormat.ARGB32 not supported on this device.", true);
            return;
        }
        if (output == null) {
            if (verboseLogs) Debug.Log($"[WSStreamTest] Creating output RT {width}x{height}.");
            try {
                output = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                output.enableRandomWrite = false;
                output.useMipMap = false;
                output.wrapMode = TextureWrapMode.Clamp;
                output.filterMode = FilterMode.Bilinear;
                output.Create();
                if (!output.IsCreated()) Report("Output RenderTexture.Create() failed (IsCreated=false).", true);
            } catch (System.Exception ex) {
                Report($"Failed to create output RenderTexture {width}x{height}: {ex.Message}", true);
            }
        } else if (output.width != width || output.height != height) {
            if (verboseLogs) Debug.Log($"[WSStreamTest] Resizing output RT from {output.width}x{output.height} to {width}x{height}.");
            try {
                output.Release();
                output.width = width;
                output.height = height;
                output.enableRandomWrite = false;
                output.useMipMap = false;
                output.wrapMode = TextureWrapMode.Clamp;
                output.filterMode = FilterMode.Bilinear;
                output.Create();
                if (!output.IsCreated()) Report("Output RenderTexture.Create() failed after resize.", true);
            } catch (System.Exception ex) {
                Report($"Failed to resize output RenderTexture to {width}x{height}: {ex.Message}", true);
            }
        }
    }

    private void EnsureWorkingTexture(int width, int height) {
        if (width <= 0 || height <= 0) { Report($"EnsureWorkingTexture invalid size {width}x{height}.", true); return; }
        if (workingTexture == null || workingTexture.width != width || workingTexture.height != height) {
            if (verboseLogs) Debug.Log($"[WSStreamTest] (Re)creating workingTexture {width}x{height}.");
            try {
                workingTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                workingTexture.wrapMode = TextureWrapMode.Clamp;
                workingTexture.filterMode = FilterMode.Bilinear;
            } catch (System.Exception ex) {
                Report($"Failed to create workingTexture {width}x{height}: {ex.Message}", true);
            }
        }
    }

    private void Report(string message, bool isError) {
        string msg = "[WSStreamTest] " + message;
        if (isError) {
            Debug.LogError(msg);
            if (throwOnError) throw new System.InvalidOperationException(msg);
        } else if (verboseLogs) {
            Debug.Log(msg);
        }
    }
}


