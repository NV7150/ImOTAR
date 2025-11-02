using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using Unity.Collections;

namespace ImOTAR.RecordSender
{
	/// <summary>
	/// Captures a Camera and a list of RenderTextures, encodes to PNG in-memory, and uploads each image via multipart/form-data.
	/// No on-disk writes are performed.
	/// </summary>
	public sealed class ImageUploader : MonoBehaviour
	{
		private const string FieldSubject = "subject_id";
		private const string FieldExp = "exp_id";
		private const string FieldFile = "file";
		private const string MimePng = "image/png";
		private const string MimeBin = "application/octet-stream";
		private const int DepthBits = 24;

		[SerializeField] private string subjectId;
		[SerializeField] private string serverUrl;
		[SerializeField] private Camera targetCamera;
		[SerializeField] private List<RenderTexture> renderTextures = new List<RenderTexture>();

		/// <summary>
		/// Experiment id base. Final exp_id is formatted as "{ExpId}-{id}".
		/// </summary>
		public string ExpId { get; set; }

		/// <summary>
		/// Capture camera and each RenderTexture, then upload with the same exp_id.
		/// </summary>
		/// <param name="id">Suffix id to build exp_id as "{ExpId}-{id}".</param>
		public async Task Take(string id)
		{
			ValidateState(id);
			var exp = string.Concat(ExpId, "-", id);
			var baseUrl = serverUrl.TrimEnd('/');
			var imagesUrl = string.Concat(baseUrl, "/images");
			var depthsUrl = string.Concat(baseUrl, "/depths");

			// Collect tasks to run in parallel (uploads, and readback->upload chains)
			var tasks = new List<Task>(1 + renderTextures.Count);

			// 1) Camera capture (async readback) -> start upload task immediately
			var camPng = await ReadbackCameraPngAsync(targetCamera); // keep on main thread after await
			tasks.Add(Upload(camPng, "cam.png", subjectId, exp, imagesUrl));

			// 2) Depth RTs: schedule GPU readback + upload per RT, run all in parallel
			for (int i = 0; i < renderTextures.Count; i++)
			{
				var rt = renderTextures[i];
				if (rt == null) throw new InvalidOperationException("RenderTexture list contains null.");
				if (rt.format != RenderTextureFormat.RFloat)
					throw new InvalidOperationException($"RenderTexture[{i}] is not RFloat.");

				int index = i;
				tasks.Add(UploadDepthForRt(rt, index, subjectId, exp, depthsUrl));
			}

			// Await all uploads (and readbacks) without blocking main thread
			await Task.WhenAll(tasks);
		}

		private void ValidateState(string id)
		{
			if (string.IsNullOrWhiteSpace(serverUrl)) throw new InvalidOperationException("Server URL is not set.");
			if (string.IsNullOrWhiteSpace(subjectId)) throw new InvalidOperationException("Subject ID is not set.");
			if (string.IsNullOrWhiteSpace(ExpId)) throw new InvalidOperationException("ExpId is not set.");
			if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("id is not set.");
			if (targetCamera == null) throw new InvalidOperationException("Target Camera is not set.");
		}

		private static async Task<byte[]> ReadbackCameraPngAsync(Camera cam)
		{
			if (cam == null) throw new ArgumentNullException(nameof(cam));
			int w = cam.pixelWidth;
			int h = cam.pixelHeight;
			if (w <= 0 || h <= 0) throw new InvalidOperationException("Camera pixel size is invalid.");

			RenderTexture prevActive = RenderTexture.active;
			RenderTexture prevTarget = cam.targetTexture;
			RenderTexture rt = null;
			Texture2D tex = null;
			try
			{
				rt = RenderTexture.GetTemporary(w, h, DepthBits, RenderTextureFormat.ARGB32);
				cam.targetTexture = rt;
				cam.Render();

				var tcs = new TaskCompletionSource<NativeArray<byte>>();
				AsyncGPUReadback.Request(rt, 0, request =>
				{
					if (request.hasError)
					{
						tcs.SetException(new Exception("GPU readback error."));
						return;
					}
					tcs.SetResult(request.GetData<byte>());
				});
				var raw = await tcs.Task; // resume on Unity context

				tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
				tex.LoadRawTextureData(raw);
				tex.Apply(false, false);
				return tex.EncodeToPNG();
			}
			finally
			{
				cam.targetTexture = prevTarget;
				RenderTexture.active = prevActive;
				if (rt != null) RenderTexture.ReleaseTemporary(rt);
				if (tex != null) Destroy(tex);
			}
		}

		private static async Task UploadDepthForRt(RenderTexture rt, int index, string subj, string exp, string depthsUrl)
		{
			if (rt == null) throw new ArgumentNullException(nameof(rt));
			if (rt.format != RenderTextureFormat.RFloat)
				throw new InvalidOperationException($"RenderTexture[{index}] is not RFloat.");

			// Read Unity properties before awaiting
			int w = rt.width;
			int h = rt.height;

			var depthBytes = await ReadbackRFloatAsync(rt);
			await UploadDepth(depthBytes, w, h, $"depth_{index}.bin", subj, exp, depthsUrl);
		}

		private static byte[] CaptureCameraToPng(Camera cam)
		{
			if (cam == null) throw new ArgumentNullException(nameof(cam));
			int w = cam.pixelWidth;
			int h = cam.pixelHeight;
			if (w <= 0 || h <= 0) throw new InvalidOperationException("Camera pixel size is invalid.");

			RenderTexture prevActive = RenderTexture.active;
			RenderTexture prevTarget = cam.targetTexture;
			Texture2D readTex = null;
			RenderTexture tempRt = null;
			try
			{
				tempRt = RenderTexture.GetTemporary(w, h, DepthBits, RenderTextureFormat.ARGB32);
				cam.targetTexture = tempRt;
				cam.Render();

				RenderTexture.active = tempRt;
				readTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
				readTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
				readTex.Apply(false, false);

				return readTex.EncodeToPNG();
			}
			finally
			{
				cam.targetTexture = prevTarget;
				RenderTexture.active = prevActive;
				if (tempRt != null) RenderTexture.ReleaseTemporary(tempRt);
				if (readTex != null) Destroy(readTex);
			}
		}

		private static async Task<byte[]> ReadbackRFloatAsync(RenderTexture rt)
		{
			if (rt == null) throw new ArgumentNullException(nameof(rt));
			if (rt.format != RenderTextureFormat.RFloat)
				throw new InvalidOperationException("RenderTexture must be RFloat.");

			var tcs = new TaskCompletionSource<byte[]>();
			AsyncGPUReadback.Request(rt, 0, request =>
			{
				if (request.hasError)
				{
					tcs.SetException(new Exception("GPU readback error."));
					return;
				}
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
				tcs.SetResult(bytes);
			});
			return await tcs.Task.ConfigureAwait(false);
		}

		private static async Task UploadDepth(byte[] depthBytes, int width, int height, string fileName, string subj, string exp, string url)
		{
			if (depthBytes == null || depthBytes.Length == 0) throw new InvalidOperationException("Depth buffer is empty.");
			if (width <= 0 || height <= 0) throw new InvalidOperationException("Invalid depth size.");
			if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("Filename is empty.", nameof(fileName));
			if (string.IsNullOrWhiteSpace(subj)) throw new ArgumentException("Subject is empty.", nameof(subj));
			if (string.IsNullOrWhiteSpace(exp)) throw new ArgumentException("Exp is empty.", nameof(exp));
			if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL is empty.", nameof(url));

			var form = new WWWForm();
			form.AddField(FieldSubject, subj);
			form.AddField(FieldExp, exp);
			form.AddField("width", width);
			form.AddField("height", height);
			form.AddBinaryData(FieldFile, depthBytes, fileName, MimeBin);

			using (var req = UnityWebRequest.Post(url, form))
			{
				var op = req.SendWebRequest();
				while (!op.isDone) await Task.Yield();
				if (req.result != UnityWebRequest.Result.Success)
				{
					throw new Exception($"Upload failed ({fileName}): {req.responseCode} {req.error}");
				}
			}
		}

		private static async Task Upload(byte[] pngBytes, string fileName, string subj, string exp, string url)
		{
			if (pngBytes == null || pngBytes.Length == 0) throw new InvalidOperationException("PNG data is empty.");
			if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("Filename is empty.", nameof(fileName));
			if (string.IsNullOrWhiteSpace(subj)) throw new ArgumentException("Subject is empty.", nameof(subj));
			if (string.IsNullOrWhiteSpace(exp)) throw new ArgumentException("Exp is empty.", nameof(exp));
			if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL is empty.", nameof(url));

			var form = new WWWForm();
			form.AddField(FieldSubject, subj);
			form.AddField(FieldExp, exp);
			form.AddBinaryData(FieldFile, pngBytes, fileName, MimePng);

			using (var req = UnityWebRequest.Post(url, form))
			{
				var op = req.SendWebRequest();
				while (!op.isDone) await Task.Yield();
				if (req.result != UnityWebRequest.Result.Success)
				{
					throw new Exception($"Upload failed ({fileName}): {req.responseCode} {req.error}");
				}
			}
		}
	}
}


