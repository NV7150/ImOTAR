using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.Collections;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;

public class WatershedCpuRegionProvider : RegionProviderBase {
    [Header("AR Input / Output")]
    [SerializeField] private ARCameraManager cameraManager;
    [SerializeField] private RenderTexture output; // ARGB32 (RGBA32 packed ID)

    [Header("Scale")]
    [SerializeField, Range(0.1f, 1f)] private float downscale = 1f;
    [SerializeField] private bool mirrorX = true; // match WSStreamTest default

    [Header("Preprocess")]
    [SerializeField] private int blurKernelSize = 3; // odd, 0/1 disables
    [SerializeField] private double thresh = 0; // 0 -> Otsu
    [SerializeField] private bool useOtsu = true;

    [Header("Unknown (edge) mask")]
    [SerializeField] private int gradApertureSize = 3; // Sobel ksize
    [SerializeField] private int unknownDilate = 1;    // widen boundaries

    [Header("Diagnostics")]
    [SerializeField] private bool verboseLogs = false;
    [SerializeField] private bool throwOnError = false;

    public override int CurrentRegionCount => _currentRegionCount;

    private Texture2D _cameraTex;       // RGBA32 staging from AR camera
    private Texture2D _idTex;           // RGBA32 staging for IDs
    private byte[] _idBytes;            // reused raw buffer (w*h*4)
    private int[] _labelBuffer;         // reused labels buffer

    private Mat _rgbaMat, _bgrMat, _grayMat, _gx, _gy, _mag, _mag8, _unknown, _kernel, _markers;
    private int _procW, _procH;
    private int _currentRegionCount = 0;

    private void OnEnable() {
        if (cameraManager != null) {
            cameraManager.frameReceived += OnFrameReceived;
            if (verboseLogs) Debug.Log("[WatershedCpuRegionProvider] Subscribed to ARCameraManager.");
        } else {
            Report("ARCameraManager is not assigned.", true);
        }
    }

    private void OnDisable() {
        if (cameraManager != null) {
            cameraManager.frameReceived -= OnFrameReceived;
        }
    }

    private void OnFrameReceived(ARCameraFrameEventArgs args) {
        if (cameraManager == null || output == null) return;
        if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image)) return;
        using (image) {
            int srcW = image.width;
            int srcH = image.height;
            int w = Mathf.Max(1, Mathf.RoundToInt(srcW * Mathf.Clamp01(downscale)));
            int h = Mathf.Max(1, Mathf.RoundToInt(srcH * Mathf.Clamp01(downscale)));

            if (output.format != RenderTextureFormat.ARGB32) { Report("Output must be ARGB32.", true); return; }
            if (output.width != w || output.height != h) { Report($"Output size {output.width}x{output.height} != expected {w}x{h}.", true); return; }

            var conv = new XRCpuImage.ConversionParams {
                inputRect = new RectInt(0, 0, srcW, srcH),
                outputDimensions = new Vector2Int(w, h),
                outputFormat = TextureFormat.RGBA32,
                transformation = mirrorX ? XRCpuImage.Transformation.MirrorX : XRCpuImage.Transformation.None
            };
            int dataSize = image.GetConvertedDataSize(conv);
            var buffer = new NativeArray<byte>(dataSize, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            image.Convert(conv, buffer);

            EnsureCameraTex(w, h);
            _cameraTex.LoadRawTextureData(buffer);
            _cameraTex.Apply(false, false);
            buffer.Dispose();

            ProcessFrame(_cameraTex, w, h);
        }
    }

    private void ProcessFrame(Texture2D frameTex, int w, int h) {
        EnsureMats(w, h);

        // Texture2D -> RGBA Mat
        OpenCVMatUtils.Texture2DToMat(frameTex, _rgbaMat, flip: false);

        // RGBA -> BGR, then GRAY
        Imgproc.cvtColor(_rgbaMat, _bgrMat, Imgproc.COLOR_RGBA2BGR);
        Imgproc.cvtColor(_bgrMat, _grayMat, Imgproc.COLOR_BGR2GRAY);

        int k = Mathf.Max(1, blurKernelSize); if (k % 2 == 0) k += 1; if (k > 1)
            Imgproc.GaussianBlur(_grayMat, _grayMat, new Size(k, k), 0);
        
        // Sobel gradient magnitude -> Otsu threshold for unknown
        Imgproc.Sobel(_grayMat, _gx, CvType.CV_32F, 1, 0, Mathf.Max(1, gradApertureSize));
        Imgproc.Sobel(_grayMat, _gy, CvType.CV_32F, 0, 1, Mathf.Max(1, gradApertureSize));
        Core.magnitude(_gx, _gy, _mag);
        Core.normalize(_mag, _mag, 0, 255, Core.NORM_MINMAX);
        _mag.convertTo(_mag8, CvType.CV_8U);
        int flags = Imgproc.THRESH_BINARY;
        if (useOtsu || thresh <= 0) flags |= Imgproc.THRESH_OTSU;
        Imgproc.threshold(_mag8, _unknown, thresh, 255, flags);
        if (unknownDilate > 0) Imgproc.dilate(_unknown, _unknown, _kernel, new Point(-1, -1), unknownDilate);

        // Seeds: connected components on non-unknown
        Mat notUnknown = new Mat();
        Core.bitwise_not(_unknown, notUnknown);
        Mat seeds = new Mat();
        Imgproc.threshold(notUnknown, seeds, 1, 255, Imgproc.THRESH_BINARY);
        Imgproc.connectedComponents(seeds, _markers);
        Core.add(_markers, new Scalar(1), _markers);
        // unknown -> 0
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (_unknown.get(y, x)[0] != 0) _markers.put(y, x, 0);

        Imgproc.watershed(_bgrMat, _markers);

        // Pack labels to RGBA32 (flip vertically to match Unity texture origin)
        int total = w * h;
        if (_idBytes == null || _idBytes.Length != total * 4) _idBytes = new byte[total * 4];
        if (_labelBuffer == null || _labelBuffer.Length != total) _labelBuffer = new int[total];
        _markers.get(0, 0, _labelBuffer);
        int maxLabel = 0;
        for (int i = 0; i < total; i++) if (_labelBuffer[i] > maxLabel) maxLabel = _labelBuffer[i];
        _currentRegionCount = Mathf.Max(0, maxLabel);
        for (int y = 0; y < h; y++) {
            int srcY = (h - 1) - y; // flip vertically
            int srcRow = srcY * w;
            int dstBi = (y * w) * 4;
            for (int x = 0; x < w; x++, dstBi += 4) {
                int id = _labelBuffer[srcRow + x];
                if (id < 0) id = 0; // boundary -> 0
                _idBytes[dstBi + 0] = (byte)(id & 0xFF);
                _idBytes[dstBi + 1] = (byte)((id >> 8) & 0xFF);
                _idBytes[dstBi + 2] = (byte)((id >> 16) & 0xFF);
                _idBytes[dstBi + 3] = (byte)((id >> 24) & 0xFF);
            }
        }

        EnsureIdTex(w, h);
        _idTex.LoadRawTextureData(_idBytes);
        _idTex.Apply(false, false);
        Graphics.Blit(_idTex, output);

        TickUp(output);

        notUnknown.Dispose(); seeds.Dispose();
    }

    private void EnsureMats(int w, int h) {
        if (_procW == w && _procH == h && _rgbaMat != null) return;
        DisposeMats();
        _procW = w; _procH = h;
        _rgbaMat = new Mat(h, w, CvType.CV_8UC4);
        _bgrMat = new Mat(h, w, CvType.CV_8UC3);
        _grayMat = new Mat(h, w, CvType.CV_8UC1);
        _gx = new Mat(h, w, CvType.CV_32F);
        _gy = new Mat(h, w, CvType.CV_32F);
        _mag = new Mat(h, w, CvType.CV_32F);
        _mag8 = new Mat(h, w, CvType.CV_8U);
        _unknown = new Mat(h, w, CvType.CV_8U);
        _kernel = Imgproc.getStructuringElement(Imgproc.MORPH_RECT, new Size(3, 3));
        _markers = new Mat(h, w, CvType.CV_32S);
        EnsureIdTex(w, h);
        EnsureCameraTex(w, h);
    }

    private void EnsureCameraTex(int w, int h) {
        if (_cameraTex == null || _cameraTex.width != w || _cameraTex.height != h) {
            _cameraTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            _cameraTex.wrapMode = TextureWrapMode.Clamp;
            _cameraTex.filterMode = FilterMode.Bilinear;
        }
    }

    private void EnsureIdTex(int w, int h) {
        if (_idTex == null || _idTex.width != w || _idTex.height != h) {
            _idTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            _idTex.wrapMode = TextureWrapMode.Clamp;
            _idTex.filterMode = FilterMode.Point;
        }
    }

    private void DisposeMats() {
        _rgbaMat?.Dispose(); _rgbaMat = null;
        _bgrMat?.Dispose(); _bgrMat = null;
        _grayMat?.Dispose(); _grayMat = null;
        _gx?.Dispose(); _gx = null;
        _gy?.Dispose(); _gy = null;
        _mag?.Dispose(); _mag = null;
        _mag8?.Dispose(); _mag8 = null;
        _unknown?.Dispose(); _unknown = null;
        _kernel?.Dispose(); _kernel = null;
        _markers?.Dispose(); _markers = null;
        
    }

    private void OnDestroy() {
        DisposeMats();
    }

    private void Report(string message, bool isError) {
        string msg = "[WatershedCpuRegionProvider] " + message;
        if (isError) {
            Debug.LogError(msg);
            if (throwOnError) throw new System.InvalidOperationException(msg);
        } else if (verboseLogs) {
            Debug.Log(msg);
        }
    }
}


