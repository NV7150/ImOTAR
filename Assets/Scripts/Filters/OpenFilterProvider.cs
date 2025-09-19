using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

/// <summary>
/// Performs grayscale morphological opening (erosion then dilation) on an R32Float depth frame.
/// Uses a persistent intermediate RenderTexture to guarantee ordering and lifetime.
/// No fallbacks; misconfigurations throw exceptions.
/// </summary>
[DisallowMultipleComponent]
public sealed class OpenFilterProvider : FrameProvider {
	// Shader property IDs
	private static readonly int ShaderPropInput = Shader.PropertyToID("_Input");
	private static readonly int ShaderPropOutput = Shader.PropertyToID("_Output");
	private static readonly int ShaderPropOutSize = Shader.PropertyToID("_OutSize");
	private static readonly int ShaderPropRadius = Shader.PropertyToID("_Radius");
	private static readonly int ShaderPropInvalid = Shader.PropertyToID("_InvalidVal");

	// Kernel names
	private const string KernelErode = "CSMain_Erode_Box";
	private const string KernelDilate = "CSMain_Dilate_Box";

	[SerializeField] private ComputeShader shader;
	[SerializeField] private FrameProvider inputProvider;

	[SerializeField] private int radius = 1; // Serialized parameter; must be >=1
	[SerializeField] private double timeToleranceMs = 16.0; // Serialized as requested
	[SerializeField] private float invalidVal = -1f; // Serialized invalid marker

	[SerializeField] private RenderTexture output; // Must be assigned via Inspector

	private RenderTexture mid; // persistent intermediate
	private DateTime lastTimestamp;
	private int kernelErodeIdx;
	private int kernelDilateIdx;
	private uint kTx, kTy, kTz;
	private int lastProcessedInputTick = -1;
	private bool ready;

	public override RenderTexture FrameTex => output;
	public override DateTime TimeStamp => lastTimestamp;

	private void OnEnable() {
		ValidateSerializedFields();
		ResolveKernelsAndThreadGroupSizes();
		SubscribeProviders();
		TryEnsureTargets();
	}

	private void OnDisable() {
		UnsubscribeProviders();
		ReleaseMid();
		ready = false;
		IsInitTexture = false;
	}

	private void ValidateSerializedFields() {
		if (shader == null) throw new InvalidOperationException("ComputeShader is not assigned.");
		if (inputProvider == null) throw new InvalidOperationException("Input FrameProvider is not assigned.");
		if (output == null) throw new InvalidOperationException("Output RenderTexture must be assigned via Inspector.");
		if (radius < 1) throw new InvalidOperationException("radius must be >= 1.");
		if (timeToleranceMs < 0) throw new InvalidOperationException("timeToleranceMs must be non-negative.");
	}

	private void ResolveKernelsAndThreadGroupSizes() {
		kernelErodeIdx = shader.FindKernel(KernelErode);
		kernelDilateIdx = shader.FindKernel(KernelDilate);
		shader.GetKernelThreadGroupSizes(kernelErodeIdx, out kTx, out kTy, out kTz);
	}

	private void SubscribeProviders() {
		inputProvider.OnFrameTexInit += OnInputInit;
		inputProvider.OnFrameUpdated += OnInputUpdated;
	}

	private void UnsubscribeProviders() {
		if (inputProvider != null) {
			inputProvider.OnFrameTexInit -= OnInputInit;
			inputProvider.OnFrameUpdated -= OnInputUpdated;
		}
	}

	private void OnInputInit(RenderTexture _) {
		TryEnsureTargets();
	}

	private void OnInputUpdated(RenderTexture _) {
		TryRun();
	}

	private void TryEnsureTargets() {
		var inTex = inputProvider.FrameTex;
		if (inTex == null) return;
		if (!inTex.IsCreated()) return;

		EnsureInputFormatOrThrow(inTex);
		EnsureOutputOrThrow(inTex);
		EnsureMidOrThrow(inTex);
		ready = true;
	}

	private static void EnsureInputFormatOrThrow(RenderTexture inTex) {
		if (inTex.graphicsFormat != GraphicsFormat.R32_SFloat)
			throw new InvalidOperationException("Input RenderTexture must be R32_SFloat (R32Float).");
	}

	private void EnsureOutputOrThrow(RenderTexture inTex) {
		if (output == null) throw new InvalidOperationException("Output RenderTexture must be assigned via Inspector.");
		bool same = output.width == inTex.width && output.height == inTex.height && output.graphicsFormat == GraphicsFormat.R32_SFloat;
		if (!same) throw new InvalidOperationException("Assigned output RenderTexture must match input dimensions and be R32_SFloat.");
		if (!output.enableRandomWrite) throw new InvalidOperationException("Output RenderTexture must have enableRandomWrite=true before Create().");
		if (!output.IsCreated()) {
			output.Create();
		}
		if (!IsInitTexture && output.IsCreated()) {
			IsInitTexture = true;
			OnFrameTexInitialized();
		}
	}

	private void EnsureMidOrThrow(RenderTexture inTex) {
		bool needCreate = mid == null || mid.width != inTex.width || mid.height != inTex.height || mid.graphicsFormat != GraphicsFormat.R32_SFloat;
		if (needCreate) {
			ReleaseMid();
			mid = new RenderTexture(inTex.width, inTex.height, 0) {
				graphicsFormat = GraphicsFormat.R32_SFloat,
				enableRandomWrite = true,
				useMipMap = false,
				autoGenerateMips = false,
				wrapMode = TextureWrapMode.Clamp,
				filterMode = FilterMode.Point
			};
		}
		if (!mid.IsCreated()) mid.Create();
	}

	private void ReleaseMid() {
		if (mid != null) {
			if (mid.IsCreated()) mid.Release();
			UnityEngine.Object.Destroy(mid);
			mid = null;
		}
	}

	private void TryRun() {
		var inTex = inputProvider.FrameTex;
		if (inTex == null) return;
		if (!inTex.IsCreated()) return;

		if (!ready) { TryEnsureTargets(); if (!ready) return; }
		EnsureInputFormatOrThrow(inTex);
		EnsureOutputOrThrow(inTex);
		EnsureMidOrThrow(inTex);

		int inTick = inputProvider.Tick;
		if (inTick == lastProcessedInputTick) return;

		DispatchErode(inTex, mid);
		DispatchDilate(mid, output);

		lastProcessedInputTick = inTick;
		lastTimestamp = inputProvider.TimeStamp;
		TickUp();
	}

	public void ExecuteNow() {
		var inTex = inputProvider?.FrameTex;
		if (inTex == null) throw new InvalidOperationException("Input provider not ready.");
		if (!inTex.IsCreated()) throw new InvalidOperationException("Input texture is not created.");
		EnsureInputFormatOrThrow(inTex);
		EnsureOutputOrThrow(inTex);
		EnsureMidOrThrow(inTex);

		DispatchErode(inTex, mid);
		DispatchDilate(mid, output);

		lastTimestamp = inputProvider.TimeStamp;
		TickUp();
	}

	// No alignment enforcement; ceil-dispatch and guard in shader.

	private void DispatchErode(RenderTexture inTex, RenderTexture outTex) {
		shader.SetInts(ShaderPropOutSize, output.width, output.height);
		shader.SetInt(ShaderPropRadius, radius);
		shader.SetFloat(ShaderPropInvalid, invalidVal);
		shader.SetTexture(kernelErodeIdx, ShaderPropInput, inTex);
		shader.SetTexture(kernelErodeIdx, ShaderPropOutput, outTex);

		int groupsX = Mathf.CeilToInt(output.width / (float)kTx);
		int groupsY = Mathf.CeilToInt(output.height / (float)kTy);
		shader.Dispatch(kernelErodeIdx, groupsX, groupsY, 1);
	}

	private void DispatchDilate(RenderTexture inTex, RenderTexture outTex) {
		shader.SetInts(ShaderPropOutSize, output.width, output.height);
		shader.SetInt(ShaderPropRadius, radius);
		shader.SetFloat(ShaderPropInvalid, invalidVal);
		shader.SetTexture(kernelDilateIdx, ShaderPropInput, inTex);
		shader.SetTexture(kernelDilateIdx, ShaderPropOutput, outTex);

		int groupsX = Mathf.CeilToInt(output.width / (float)kTx);
		int groupsY = Mathf.CeilToInt(output.height / (float)kTy);
		shader.Dispatch(kernelDilateIdx, groupsX, groupsY, 1);
	}

	private void OnValidate() {
		if (radius < 1) radius = 1;
		if (timeToleranceMs < 0) timeToleranceMs = 0;
	}
}


