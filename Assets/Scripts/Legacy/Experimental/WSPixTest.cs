// using UnityEngine;
// using OpenCVForUnity.CoreModule;
// using OpenCVForUnity.ImgprocModule;
// using OpenCVForUnity.UnityIntegration;

// public class WSPixTest : MonoBehaviour {
//     [SerializeField] private Texture2D testTex;
//     [SerializeField] private RenderTexture output;

//     [Header("Preprocess")]
//     [SerializeField] private int blurKernelSize = 3; // odd, 0/1 to disable
//     [SerializeField] private double thresh = 0; // 0 -> Otsu
//     [SerializeField] private bool useOtsu = true;

//     [Header("Marker Build")]
//     [SerializeField] private int morphOpen = 1;
//     [SerializeField] private int morphDilate = 2;
//     [SerializeField] private double sureFgRatio = 0.4; // fraction of max dist to threshold sure-foreground (0..1)

//     [Header("Blend with Original")]
//     [SerializeField] private bool blendWithOriginal = true;
//     [SerializeField, Range(0f, 1f)] private float blendAlpha = 0.5f; // original weight

//     [Header("Run Options")]
//     [SerializeField] private bool runOnStart = false;

//     private Texture2D workingTexture;

//     private void Start() {
//         if (runOnStart) RunSegmentation();
//     }

//     [ContextMenu("Run Segmentation (Watershed)")]
//     private void RunSegmentation() {
//         if (testTex == null) {
//             Debug.LogWarning("WSPixTest: testTex is null.");
//             return;
//         }

//         int width = testTex.width;
//         int height = testTex.height;
//         EnsureOutput(width, height);
//         EnsureWorkingTexture(width, height);

//         Mat srcRgba = new Mat(height, width, CvType.CV_8UC4);
//         Mat srcBgr = new Mat(height, width, CvType.CV_8UC3);
//         Mat resultRgba = new Mat(height, width, CvType.CV_8UC4);

//         try {
//             var readableTex = EnsureReadable(testTex);
//             OpenCVMatUtils.Texture2DToMat(readableTex, srcRgba, flip: false);
//             Imgproc.cvtColor(srcRgba, srcBgr, Imgproc.COLOR_RGBA2BGR);

//             // 1) Grayscale
//             Mat gray = new Mat();
//             Imgproc.cvtColor(srcBgr, gray, Imgproc.COLOR_BGR2GRAY);

//             // 2) Optional blur
//             int k = Mathf.Max(1, blurKernelSize);
//             if (k % 2 == 0) k += 1;
//             if (k > 1) Imgproc.GaussianBlur(gray, gray, new Size(k, k), 0);

//             // 3) Threshold to binary (background/foreground rough)
//             Mat bin = new Mat();
//             int flags = Imgproc.THRESH_BINARY_INV;
//             if (useOtsu || thresh <= 0) flags |= Imgproc.THRESH_OTSU;
//             Imgproc.threshold(gray, bin, thresh, 255, flags);

//             // 4) Morphology to clean noise
//             Mat kernel = Imgproc.getStructuringElement(Imgproc.MORPH_RECT, new Size(3, 3));
//             if (morphOpen > 0) Imgproc.morphologyEx(bin, bin, Imgproc.MORPH_OPEN, kernel, new Point(-1, -1), morphOpen);
//             if (morphDilate > 0) Imgproc.dilate(bin, bin, kernel, new Point(-1, -1), morphDilate);

//             // 5) Distance transform to get sure foreground
//             Mat dist = new Mat();
//             Imgproc.distanceTransform(bin, dist, Imgproc.DIST_L2, 3);
//             Core.normalize(dist, dist, 0, 1.0, Core.NORM_MINMAX);

//             Mat sureFg = new Mat();
//             double fgThr = Mathf.Clamp01((float)sureFgRatio);
//             Imgproc.threshold(dist, sureFg, fgThr, 1.0, Imgproc.THRESH_BINARY);

//             // 6) Connected components on sureFg to get markers
//             Mat sureFg8U = new Mat();
//             sureFg.convertTo(sureFg8U, CvType.CV_8U, 255.0);

//             Mat labels = new Mat(); // 32SC1
//             int nLabels = Imgproc.connectedComponents(sureFg8U, labels);

//             // 7) Mark unknown region (bin==0 -> background, others unknown)
//             Mat unknown = new Mat();
//             Core.subtract(bin, sureFg8U, unknown);

//             // Add background as 1, shift labels by +1 to ensure background=1
//             // labels: 0..nLabels-1 -> 1..nLabels
//             Core.add(labels, new Scalar(1), labels);

//             // Set unknown regions to 0 in markers
//             // unknown>0 => marker=0
//             for (int y = 0; y < height; y++) {
//                 for (int x = 0; x < width; x++) {
//                     byte unk = (byte)unknown.get(y, x)[0];
//                     if (unk != 0) labels.put(y, x, 0);
//                 }
//             }

//             // 8) Watershed
//             Imgproc.watershed(srcBgr, labels);

//             // 9) Colorize labels (boundary=-1)
//             Mat colored = new Mat(height, width, CvType.CV_8UC3);
//             int total = width * height;
//             int[] labelData = new int[total];
//             labels.get(0, 0, labelData);

//             // Build LUT up to max label id
//             int maxLabel = 0;
//             for (int i = 0; i < total; i++) if (labelData[i] > maxLabel) maxLabel = labelData[i];
//             if (maxLabel < 1) maxLabel = 1;
//             Scalar[] lut = new Scalar[maxLabel + 1];
//             System.Random rng = new System.Random(12345);
//             lut[0] = new Scalar(0, 0, 0);
//             for (int i = 1; i <= maxLabel; i++) lut[i] = new Scalar(rng.Next(0, 256), rng.Next(0, 256), rng.Next(0, 256));

//             byte[] bgr = new byte[total * 3];
//             for (int i = 0; i < total; i++) {
//                 int lid = labelData[i];
//                 int bi = i * 3;
//                 if (lid == -1) { // boundary
//                     bgr[bi + 0] = 0; bgr[bi + 1] = 0; bgr[bi + 2] = 0;
//                 } else {
//                     if (lid < 0) lid = 0; if (lid > maxLabel) lid = maxLabel;
//                     Scalar c = lut[lid];
//                     bgr[bi + 0] = (byte)c.val[0];
//                     bgr[bi + 1] = (byte)c.val[1];
//                     bgr[bi + 2] = (byte)c.val[2];
//                 }
//             }
//             colored.put(0, 0, bgr);

//             // 10) Optional blend
//             if (blendWithOriginal) {
//                 float a = Mathf.Clamp01(blendAlpha);
//                 Core.addWeighted(srcBgr, a, colored, 1f - a, 0.0, colored);
//             }

//             Imgproc.cvtColor(colored, resultRgba, Imgproc.COLOR_BGR2RGBA);
//             OpenCVMatUtils.MatToTexture2D(resultRgba, workingTexture, flip: false);
//             Graphics.Blit(workingTexture, output);
//         }
//         finally {
//             srcRgba.Dispose();
//             srcBgr.Dispose();
//             resultRgba.Dispose();
//         }
//     }

//     private void EnsureOutput(int width, int height) {
//         if (output == null || output.width != width || output.height != height) {
//             if (output != null) output.Release();
//             output = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) {
//                 enableRandomWrite = false,
//                 useMipMap = false,
//                 wrapMode = TextureWrapMode.Clamp,
//                 filterMode = FilterMode.Bilinear
//             };
//             output.Create();
//         }
//     }

//     private void EnsureWorkingTexture(int width, int height) {
//         if (workingTexture == null || workingTexture.width != width || workingTexture.height != height) {
//             workingTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
//             workingTexture.wrapMode = TextureWrapMode.Clamp;
//             workingTexture.filterMode = FilterMode.Bilinear;
//         }
//     }

//     private static Texture2D EnsureReadable(Texture2D source) {
//         if (source == null) return null;
//         try { source.GetPixels32(); return source; }
//         catch {
//             RenderTexture tmp = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
//             Graphics.Blit(source, tmp);
//             RenderTexture prev = RenderTexture.active;
//             RenderTexture.active = tmp;
//             Texture2D copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
//             copy.ReadPixels(new UnityEngine.Rect(0, 0, source.width, source.height), 0, 0);
//             copy.Apply(false, false);
//             RenderTexture.active = prev;
//             RenderTexture.ReleaseTemporary(tmp);
//             return copy;
//         }
//     }
// }
