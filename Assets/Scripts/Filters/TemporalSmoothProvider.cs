using System;
using UnityEngine;

public class TemporalSmoothProvider : FrameProvider
{
	private const string KERNEL_NAME = "CSMain";
	private const float DEFAULT_INVALID_VALUE = -1.0f;
	private const float DEFAULT_ALPHA = 0.2f;

	[SerializeField] private FrameProvider source;
	[SerializeField] private RenderTexture output;
	[SerializeField] private ComputeShader compute;
	[SerializeField, Range(0f, 1f)] private float alpha = DEFAULT_ALPHA;
	[SerializeField] private float invalidValue = DEFAULT_INVALID_VALUE;
	[SerializeField] private bool verboseLogs = false;

	private int kernel;
	private RenderTexture history;
	private bool isInitialized;
	private DateTime lastOutputTimestamp;

	public override RenderTexture FrameTex => output;
	public override DateTime TimeStamp => lastOutputTimestamp;

	private void Awake()
	{
		ValidateConfiguration();
		InitializeComputeShader();
	}

	private void OnEnable()
	{
		if (source != null) source.OnFrameUpdated += OnSourceFrameUpdated;
		else if (verboseLogs) Debug.LogWarning($"[TemporalSmooth] Source is null on {gameObject.name}");

		EnsureOutputCreated();
	}

	private void OnDisable()
	{
		if (source != null) source.OnFrameUpdated -= OnSourceFrameUpdated;
	}

	private void ValidateConfiguration()
	{
		if (source == null) throw new InvalidOperationException("Source FrameProvider is not assigned");
		if (output == null) throw new InvalidOperationException("Output RenderTexture is not assigned");
		if (compute == null) throw new InvalidOperationException("ComputeShader is not assigned");
		if (alpha < 0f || alpha > 1f) throw new InvalidOperationException($"Alpha must be in [0,1], got {alpha}");
	}

	private void InitializeComputeShader()
	{
		kernel = compute.FindKernel(KERNEL_NAME);
		if (kernel < 0) throw new InvalidOperationException($"Kernel '{KERNEL_NAME}' not found in compute shader");
	}

	private void EnsureOutputCreated()
	{
		if (output != null && !output.IsCreated()) output.Create();
		ValidateOutputConfiguration();
	}

	private void ValidateOutputConfiguration()
	{
		if (output == null) return;
		if (output.format != RenderTextureFormat.RFloat)
			throw new InvalidOperationException($"Output RenderTexture format must be RFloat, but got {output.format}");
		if (!output.enableRandomWrite)
			throw new InvalidOperationException("Output RenderTexture must have enableRandomWrite set to true");
		if (source.FrameTex != null)
		{
			if (output.width != source.FrameTex.width || output.height != source.FrameTex.height)
				throw new InvalidOperationException($"Output size ({output.width}x{output.height}) must match source size ({source.FrameTex.width}x{source.FrameTex.height})");
		}
	}

	private void EnsureHistory()
	{
		if (history != null && history.IsCreated() && history.width == output.width && history.height == output.height) return;

		ReleaseHistory();
		history = new RenderTexture(output.width, output.height, 0, RenderTextureFormat.RFloat)
		{
			enableRandomWrite = false
		};
		history.Create();
	}

	private void ReleaseHistory()
	{
		if (history != null)
		{
			history.Release();
			history = null;
		}
	}

	private void OnDestroy()
	{
		ReleaseHistory();
	}

	private void OnSourceFrameUpdated(RenderTexture frameTex)
	{
		if (!isInitialized)
		{
			ValidateOutputConfiguration();
			EnsureHistory();

			if (source.FrameTex == null)
			{
				if (verboseLogs) Debug.LogWarning($"[TemporalSmooth] Source FrameTex is null, skipping init copy on {gameObject.name}");
				return;
			}

			Graphics.CopyTexture(source.FrameTex, output);
			Graphics.CopyTexture(output, history);
			isInitialized = true;
			lastOutputTimestamp = source.TimeStamp;
			OnFrameTexInitialized();
			TickUp();
			return;
		}

		if (source.FrameTex == null)
		{
			if (verboseLogs) Debug.LogWarning($"[TemporalSmooth] Source FrameTex is null, skipping compute on {gameObject.name}");
			return;
		}

		ExecuteCompute();
		lastOutputTimestamp = source.TimeStamp;
		TickUp();
	}

	private void ExecuteCompute()
	{
		// Set resources
		compute.SetTexture(kernel, "_Input", source.FrameTex);
		compute.SetTexture(kernel, "_History", history);
		compute.SetTexture(kernel, "_Output", output);

		compute.SetFloat("_Alpha", alpha);
		compute.SetFloat("_InvalidValue", invalidValue);
		compute.SetVector("_Size", new Vector2(output.width, output.height));

		int tgx = Mathf.CeilToInt(output.width / 8.0f);
		int tgy = Mathf.CeilToInt(output.height / 8.0f);
		compute.Dispatch(kernel, tgx, tgy, 1);

		Graphics.CopyTexture(output, history);
	}
}
