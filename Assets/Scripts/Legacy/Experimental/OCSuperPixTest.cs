// using UnityEngine;
// using System.Collections.Generic;
// using OpenCVForUnity.CoreModule;
// using OpenCVForUnity.ImgprocModule;
// using OpenCVForUnity.UnityIntegration;
// using OpenCVForUnity.XimgprocModule;

// public class OCSuperPixTest : MonoBehaviour {
//     [SerializeField] private Texture2D testTex;
//     [SerializeField] private RenderTexture output;
//     [Header("Superpixel (SLIC)")]
//     [SerializeField] private int algorithm = Ximgproc.SLICO; // SLIC, SLICO, MSLIC
//     [SerializeField] private int regionSize = 20; // approx superpixel size in pixels
//     [SerializeField] private float ruler = 10f;   // compactness; larger => spatial compactness重視
//     [SerializeField] private int iterate = 10;
//     [SerializeField] private bool enforceConnectivity = true;
//     [SerializeField] private int minElementPercent = 25; // enforce connectivity param
//     [SerializeField] private bool overlayContours = true;

//     [Header("Blend with Original")]
//     [SerializeField] private bool blendWithOriginal = true;
//     [SerializeField, Range(0f, 1f)] private float blendAlpha = 0.5f; // original weight

//     [Header("Run Options")]
//     [SerializeField] private bool runOnStart = false;

//     private Texture2D workingTexture;

//     private void Start() {
//         if (runOnStart) {
//             RunSegmentation();
//         }
//     }

//     [ContextMenu("Run Segmentation (Superpixels)")] 
//     private void RunSegmentation() {
//         if (testTex == null) {
//             Debug.LogWarning("OCSuperPixTest: testTex is null.");
//             return;
//         }

//         int width = testTex.width;
//         int height = testTex.height;

//         EnsureOutput(width, height);
//         EnsureWorkingTexture(width, height);

//         Mat srcRgba = new Mat(height, width, CvType.CV_8UC4);
//         Mat resultRgba = new Mat(height, width, CvType.CV_8UC4);

//         try {
//             var readableTex = EnsureReadable(testTex);
//             OpenCVMatUtils.Texture2DToMat(readableTex, srcRgba, flip: false);
//             // Run SuperpixelSLIC
//             Mat srcBgr = new Mat();
//             Imgproc.cvtColor(srcRgba, srcBgr, Imgproc.COLOR_RGBA2BGR);
//             SuperpixelSLIC slic = Ximgproc.createSuperpixelSLIC(srcBgr, algorithm, Mathf.Max(1, regionSize), Mathf.Max(0.1f, ruler));
//             slic.iterate(Mathf.Max(1, iterate));
//             if (enforceConnectivity) slic.enforceLabelConnectivity(Mathf.Clamp(minElementPercent, 0, 100));

//             // Retrieve labels (CV_32SC1) and visualize as random colors
//             Mat labels = new Mat();
//             slic.getLabels(labels);
//             int num = slic.getNumberOfSuperpixels();

//             Mat colored = new Mat(height, width, CvType.CV_8UC3);
//             int total = width * height;
//             int[] labelData = new int[total];
//             labels.get(0, 0, labelData);

//             // Prepare a color table
//             Scalar[] lut = new Scalar[num > 0 ? num : 1];
//             System.Random rng = new System.Random(12345);
//             for (int i = 0; i < lut.Length; i++) {
//                 lut[i] = new Scalar(rng.Next(0, 256), rng.Next(0, 256), rng.Next(0, 256)); // BGR
//             }

//             // Fill colored Mat from labels (row-major)
//             byte[] bgr = new byte[total * 3];
//             for (int i = 0; i < total; i++) {
//                 int lid = labelData[i];
//                 if (lid < 0) lid = 0;
//                 if (lid >= lut.Length) lid %= lut.Length;
//                 int bi = i * 3;
//                 Scalar c = lut[lid];
//                 bgr[bi + 0] = (byte)c.val[0];
//                 bgr[bi + 1] = (byte)c.val[1];
//                 bgr[bi + 2] = (byte)c.val[2];
//             }
//             colored.put(0, 0, bgr);

//             if (overlayContours) {
//                 Mat mask = new Mat();
//                 slic.getLabelContourMask(mask, true);
//                 // draw contours in black on top of colored
//                 colored.setTo(new Scalar(0, 0, 0), mask);
//                 mask.Dispose();
//             }

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
//             resultRgba.Dispose();
//         }
//     }

//     private void EnsureOutput(int width, int height) {
//         if (output == null || output.width != width || output.height != height) {
//             if (output != null) {
//                 output.Release();
//             }
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
//         // If already readable (most import settings), return as-is
//         try {
//             source.GetPixels32();
//             return source;
//         } catch {
//             // Make a readable copy via temporary RenderTexture
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

//     // RandomBgr no longer needed

// }