using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ImOTAR.RecordSender
{
	/// <summary>
	/// UI Button caller that triggers ImageUploader.Take with a timestamp ID.
	/// Supports concurrent presses and exposes completion status.
	/// </summary>
	public sealed class ImageUploadButton : MonoBehaviour
	{
		public enum UploadStatus {
			Idle,
			Uploading,
			Success,
			Error
		}

		private const string TimeFmt = "yyyyMMdd_HHmmssfff";

		[SerializeField] private ImageUploader uploader;

		public UploadStatus Status { get; private set; } = UploadStatus.Idle;
		public string LastError { get; private set; }

		private int inflight = 0;

		/// <summary>
		/// UI onClick entrypoint. Generates UTC timestamp id and starts upload without blocking UI.
		/// </summary>
		public void Snap()
		{
			Status = UploadStatus.Uploading;
			LastError = null;
			var id = DateTime.UtcNow.ToString(TimeFmt, CultureInfo.InvariantCulture);
			_ = RunSnapAsync(id).ContinueWith(
				t => Debug.LogException(t.Exception),
				TaskContinuationOptions.OnlyOnFaulted
			);
		}

	private async Task RunSnapAsync(string id)
	{
		if (uploader == null) throw new InvalidOperationException("ImageUploader is not set.");
		Interlocked.Increment(ref inflight);
		try
		{
			await uploader.Take(id);
			if (Interlocked.Decrement(ref inflight) == 0)
			{
				Status = UploadStatus.Success;
			}
		}
		catch (Exception ex)
		{
			LastError = $"Upload failed: {ex.Message}";
			if (Interlocked.Decrement(ref inflight) == 0)
			{
				Status = UploadStatus.Error;
			}
			throw;
		}
	}
	}
}


