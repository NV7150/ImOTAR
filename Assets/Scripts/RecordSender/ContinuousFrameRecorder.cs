using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using UnityEngine.Rendering;

namespace ImOTAR.RecordSender
{
	/// <summary>
	/// Continuously records camera and lists of RenderTextures (RFloat and RGB) at a given interval, stores all in memory, then bulk uploads as ZIP archives.
	/// File naming: {type}-{experimentId}-{yyyyMMdd_HHmmss.fff} with type = cam | rgb{index} | depth{index}.
	/// Depth files are raw little-endian float32 binary (.bin). PNG files for camera/RGB.
	/// </summary>
	public sealed class ContinuousFrameRecorder : MonoBehaviour
	{
		private const string FieldSubject = "subject_id";
		private const string FieldExp = "exp_id";
		private const string FieldArchive = "archive";
		private const string MimeZip = "application/zip";
		private const string MimePng = "image/png";
		private const string MimeBin = "application/octet-stream";
		private const int DepthBits = 24;

		[SerializeField] private string serverUrl;
		[SerializeField] private string subjectId;
		[SerializeField] private Camera targetCamera;
		[SerializeField] private List<RenderTexture> renderTexturesFloat = new List<RenderTexture>();
		[SerializeField] private List<RenderTexture> renderTexturesRGB = new List<RenderTexture>();
	public string ExperimentId { get; set; }
		[SerializeField] private float intervalSec; // 0 = every frame
		[SerializeField] private TMP_Text statusText; // Optional UI Text to show status (TextMeshPro)

		public bool Uploaded { get; private set; }

		public enum RecorderStatus
		{
			Idle,
			Recording,
			Uploading,
			Uploaded
		}

		public RecorderStatus Status { get; private set; } = RecorderStatus.Idle;

		private bool _recording;
		private Coroutine _loop;
		private float _lastCaptureTime;
		private int _pendingDepth;

		private readonly List<FramePng> _cameraFrames = new List<FramePng>();
		private readonly List<FramePng> _rgbFrames = new List<FramePng>();
		private readonly List<FrameDepth> _depthFrames = new List<FrameDepth>();

		private struct FramePng
		{
			public string FileName;
			public byte[] Data;
		}

		private struct FrameDepth
		{
			public string FileName;
			public byte[] Data;
			public int Width;
			public int Height;
		}

		/// <summary>Start continuous recording.</summary>
		public void StartRecord()
		{
			ValidateStartState();
			if (_recording) throw new InvalidOperationException("Already recording.");
			Uploaded = false;
			_recording = true;
			_lastCaptureTime = -intervalSec; // force immediate capture
			_loop = StartCoroutine(CaptureLoop());
			SetStatus(RecorderStatus.Recording);
		}

		/// <summary>
		/// End recording (async). Builds ZIP archives, uploads, then sets Uploaded flag.
		/// Use this from code when you want to await completion.
		/// </summary>
		public async Task EndRecordAsync()
		{
			if (!_recording) throw new InvalidOperationException("Not recording.");
			_recording = false;
			if (_loop != null) StopCoroutine(_loop);
			_loop = null;

			// Wait for pending depth readbacks to finish
			while (_pendingDepth > 0)
			{
				await Task.Yield();
			}

			// Build and upload archives
			SetStatus(RecorderStatus.Uploading);
			await UploadAllAsync();
			Uploaded = true;
			SetStatus(RecorderStatus.Uploaded);
		}

		/// <summary>
		/// End recording (UI/Button friendly). Starts a coroutine to wait for async completion.
		/// </summary>
		public void EndRecord()
		{
			StartCoroutine(EndRecordRoutine());
		}

		private System.Collections.IEnumerator EndRecordRoutine()
		{
			Task t;
			try
			{
				t = EndRecordAsync();
			}
			catch (Exception ex)
			{
				Debug.LogException(ex);
				yield break;
			}
			while (!t.IsCompleted) yield return null;
			if (t.IsFaulted && t.Exception != null)
			{
				Debug.LogException(t.Exception);
			}
		}

		private void ValidateStartState()
		{
			if (string.IsNullOrWhiteSpace(serverUrl)) throw new InvalidOperationException("Server URL not set.");
			if (string.IsNullOrWhiteSpace(subjectId)) throw new InvalidOperationException("Subject ID not set.");
			if (targetCamera == null) throw new InvalidOperationException("Target Camera not set.");
		}

		private System.Collections.IEnumerator CaptureLoop()
		{
			while (_recording)
			{
				yield return new WaitForEndOfFrame();
				if (!_recording) break;
				if (intervalSec > 0f)
				{
					if (Time.realtimeSinceStartup - _lastCaptureTime < intervalSec) continue;
				}
				_lastCaptureTime = Time.realtimeSinceStartup;
				CaptureFrame();
			}
		}

		private void CaptureFrame()
		{
			string expStr = ExperimentId;
			string ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss.fff", CultureInfo.InvariantCulture);

			// Camera PNG
			var camPng = CaptureCameraToPng(targetCamera);
			_cameraFrames.Add(new FramePng
			{
				FileName = string.Concat("cam-", expStr, "-", ts, ".png"),
				Data = camPng
			});

			// RGB textures PNGs
			for (int i = 0; i < renderTexturesRGB.Count; i++)
			{
				var rt = renderTexturesRGB[i];
				if (rt == null) throw new InvalidOperationException($"RGB RenderTexture[{i}] null.");
				// Skip when assigned but not created yet
				if (!rt.IsCreated())
				{
					continue;
				}
				var png = CaptureRgbRtToPng(rt);
				_rgbFrames.Add(new FramePng
				{
					FileName = string.Concat("rgb", i.ToString(CultureInfo.InvariantCulture), "-", expStr, "-", ts, ".png"),
					Data = png
				});
			}

			// Depth textures raw float32 (non-blocking GPU readback)
			for (int i = 0; i < renderTexturesFloat.Count; i++)
			{
				var rt = renderTexturesFloat[i];
				if (rt == null) throw new InvalidOperationException($"Depth RenderTexture[{i}] null.");
				// Skip when assigned but not created yet
				if (!rt.IsCreated())
				{
					continue;
				}
				if (rt.format != RenderTextureFormat.RFloat)
					throw new InvalidOperationException($"Depth RenderTexture[{i}] must be RFloat.");
				EnqueueDepthRt(rt, i, expStr, ts);
			}
		}

		private void SetStatus(RecorderStatus s)
		{
			Status = s;
			if (statusText != null)
			{
				switch (s)
				{
					case RecorderStatus.Idle: statusText.text = "Idle"; break;
					case RecorderStatus.Recording: statusText.text = "Recording"; break;
					case RecorderStatus.Uploading: statusText.text = "Uploading"; break;
					case RecorderStatus.Uploaded: statusText.text = "Uploaded"; break;
				}
			}
		}

		private void EnqueueDepthRt(RenderTexture rt, int index, string expStr, string ts)
		{
			int w = rt.width;
			int h = rt.height;
			string name = string.Concat("depth", index.ToString(CultureInfo.InvariantCulture), "-", expStr, "-", ts, ".bin");
			_checkedRtRFloat(rt, index);
			_pendingDepth++;
			AsyncGPUReadback.Request(rt, 0, request =>
			{
				try
				{
					if (request.hasError) throw new Exception("GPU readback error.");
					var data = request.GetData<float>();
					int count = data.Length;
					var floats = new float[count];
					data.CopyTo(floats);
					var bytes = new byte[count * sizeof(float)];
					if (BitConverter.IsLittleEndian)
					{
						Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
					}
					else
					{
						int bi = 0;
						for (int i = 0; i < count; i++)
						{
							var b = BitConverter.GetBytes(floats[i]);
							Array.Reverse(b);
							Buffer.BlockCopy(b, 0, bytes, bi, sizeof(float));
							bi += sizeof(float);
						}
					}
					_depthFrames.Add(new FrameDepth
					{
						FileName = name,
						Data = bytes,
						Width = w,
						Height = h
					});
				}
				finally
				{
					_pendingDepth--;
				}
			});
		}

		private static void _checkedRtRFloat(RenderTexture rt, int index)
		{
			if (rt == null) throw new ArgumentNullException(nameof(rt));
			if (rt.format != RenderTextureFormat.RFloat)
				throw new InvalidOperationException($"Depth RenderTexture[{index}] must be RFloat.");
		}

		private static byte[] CaptureCameraToPng(Camera cam)
		{
			if (cam == null) throw new ArgumentNullException(nameof(cam));
			int w = cam.pixelWidth;
			int h = cam.pixelHeight;
			if (w <= 0 || h <= 0) throw new InvalidOperationException("Camera size invalid.");
			RenderTexture prevTarget = cam.targetTexture;
			RenderTexture prevActive = RenderTexture.active;
			RenderTexture temp = null;
			Texture2D tex = null;
			try
			{
				temp = RenderTexture.GetTemporary(w, h, DepthBits, RenderTextureFormat.ARGB32);
				cam.targetTexture = temp;
				cam.Render();
				RenderTexture.active = temp;
				tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
				tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
				tex.Apply(false, false);
				return tex.EncodeToPNG();
			}
			finally
			{
				cam.targetTexture = prevTarget;
				RenderTexture.active = prevActive;
				if (temp != null) RenderTexture.ReleaseTemporary(temp);
				if (tex != null) UnityEngine.Object.Destroy(tex);
			}
		}

		private static byte[] CaptureRgbRtToPng(RenderTexture rt)
		{
			if (rt == null) throw new ArgumentNullException(nameof(rt));
			int w = rt.width;
			int h = rt.height;
			if (w <= 0 || h <= 0) throw new InvalidOperationException("RGB RT size invalid.");
			RenderTexture prevActive = RenderTexture.active;
			Texture2D tex = null;
			try
			{
				RenderTexture.active = rt;
				tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
				tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
				tex.Apply(false, false);
				return tex.EncodeToPNG();
			}
			finally
			{
				RenderTexture.active = prevActive;
				if (tex != null) UnityEngine.Object.Destroy(tex);
			}
		}


		private async Task UploadAllAsync()
		{
			string baseUrl = serverUrl.TrimEnd('/');
			string expStr = ExperimentId;
			string expStrRgb = string.Concat(expStr, "-RGB");

			// Camera images bulk (original exp id)
			if (_cameraFrames.Count > 0)
			{
				var camZip = BuildZip(_cameraFrames, new List<FramePng>());
				await UploadArchive(camZip, baseUrl + "/images/bulk", subjectId, expStr);
			}

			// RGB images bulk (exp id with -RGB suffix)
			if (_rgbFrames.Count > 0)
			{
				var rgbZip = BuildZip(new List<FramePng>(), _rgbFrames);
				await UploadArchive(rgbZip, baseUrl + "/images/bulk", subjectId, expStrRgb);
			}

			// Depths (bulk with global width/height fields)
			if (_depthFrames.Count > 0)
			{
				int w = _depthFrames[0].Width;
				int h = _depthFrames[0].Height;
				if (w <= 0 || h <= 0) throw new InvalidOperationException("Depth width/height invalid.");
				for (int i = 1; i < _depthFrames.Count; i++)
				{
					if (_depthFrames[i].Width != w || _depthFrames[i].Height != h)
						throw new InvalidOperationException("All depth frames must share identical width/height for bulk upload.");
				}

				var depthsZip = BuildZipDepth(_depthFrames);
				await UploadDepthArchive(depthsZip, baseUrl + "/depths/bulk", subjectId, expStr, w, h);
			}
		}

		private static byte[] BuildZip(List<FramePng> cameraFrames, List<FramePng> rgbFrames)
		{
			using (var ms = new MemoryStream())
			{
				using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
				{
					for (int i = 0; i < cameraFrames.Count; i++)
					{
						var f = cameraFrames[i];
						var entry = zip.CreateEntry(f.FileName, System.IO.Compression.CompressionLevel.Optimal);
						using (var s = entry.Open())
						{
							s.Write(f.Data, 0, f.Data.Length);
						}
					}
					for (int i = 0; i < rgbFrames.Count; i++)
					{
						var f = rgbFrames[i];
						var entry = zip.CreateEntry(f.FileName, System.IO.Compression.CompressionLevel.Optimal);
						using (var s = entry.Open())
						{
							s.Write(f.Data, 0, f.Data.Length);
						}
					}
				}
				return ms.ToArray();
			}
		}

		private static byte[] BuildZipDepth(List<FrameDepth> depthFrames)
		{
			using (var ms = new MemoryStream())
			{
				using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
				{
					for (int i = 0; i < depthFrames.Count; i++)
					{
						var f = depthFrames[i];
						var entry = zip.CreateEntry(f.FileName, System.IO.Compression.CompressionLevel.Optimal);
						using (var s = entry.Open())
						{
							s.Write(f.Data, 0, f.Data.Length);
						}
					}
				}
				return ms.ToArray();
			}
		}

		private static async Task UploadArchive(byte[] zipBytes, string url, string subj, string exp)
		{
			if (zipBytes == null || zipBytes.Length == 0) throw new InvalidOperationException("ZIP empty.");
			if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("URL empty.");
			if (string.IsNullOrWhiteSpace(subj)) throw new InvalidOperationException("Subject empty.");
			if (string.IsNullOrWhiteSpace(exp)) throw new InvalidOperationException("Exp empty.");

			var form = new WWWForm();
			form.AddField(FieldSubject, subj);
			form.AddField(FieldExp, exp);
			form.AddBinaryData(FieldArchive, zipBytes, "archive.zip", MimeZip);

			using (var req = UnityWebRequest.Post(url, form))
			{
				var op = req.SendWebRequest();
				while (!op.isDone) await Task.Yield();
				if (req.result != UnityWebRequest.Result.Success)
				{
					throw new Exception($"Bulk upload failed: {req.responseCode} {req.error}");
				}
			}
		}

		private static async Task UploadDepthArchive(byte[] zipBytes, string url, string subj, string exp, int width, int height)
		{
			if (zipBytes == null || zipBytes.Length == 0) throw new InvalidOperationException("ZIP empty.");
			if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("URL empty.");
			if (string.IsNullOrWhiteSpace(subj)) throw new InvalidOperationException("Subject empty.");
			if (string.IsNullOrWhiteSpace(exp)) throw new InvalidOperationException("Exp empty.");
			if (width <= 0 || height <= 0) throw new InvalidOperationException("Width/Height invalid.");

			var form = new WWWForm();
			form.AddField(FieldSubject, subj);
			form.AddField(FieldExp, exp);
			form.AddField("width", width);
			form.AddField("height", height);
			form.AddBinaryData(FieldArchive, zipBytes, "archive.zip", MimeZip);

			using (var req = UnityWebRequest.Post(url, form))
			{
				var op = req.SendWebRequest();
				while (!op.isDone) await Task.Yield();
				if (req.result != UnityWebRequest.Result.Success)
				{
					throw new Exception($"Bulk depth upload failed: {req.responseCode} {req.error}");
				}
			}
		}
	}
}
