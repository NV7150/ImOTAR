using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

/// <summary>
/// Filters input frames by a mask using a threshold on GPU and provides the result as a FrameProvider.
/// R32Float only; no fallbacks. Any misconfiguration throws exceptions.
/// </summary>
public sealed class MaskFilterProvider : FrameProvider {
	public enum ThresholdMode {
		Greater = 0,
		Less = 1,
	}

	// Shader/kernel identifiers
	private static readonly int ShaderPropInput = Shader.PropertyToID("_Input");
	private static readonly int ShaderPropMask = Shader.PropertyToID("_Mask");
	private static readonly int ShaderPropOutput = Shader.PropertyToID("_Output");
	private static readonly int ShaderPropThreshold = Shader.PropertyToID("_Threshold");
	private static readonly int ShaderPropFillValue = Shader.PropertyToID("_FillValue");
	private static readonly int ShaderPropWidth = Shader.PropertyToID("_Width");
	private static readonly int ShaderPropHeight = Shader.PropertyToID("_Height");

	private static readonly string KernelNameGreaterEqual = "CSMain_GreaterEqual";
	private static readonly string KernelNameLessEqual = "CSMain_LessEqual";

	[SerializeField] private ComputeShader shader;

	[SerializeField] private FrameProvider inputProvider;
	[SerializeField] private FrameProvider maskProvider;

	[SerializeField] private ThresholdMode mode = ThresholdMode.Greater;
	[SerializeField, Range(0f, 1f)] private float threshold = 0.5f;
	[SerializeField] private float fillVal = 0f;
	[SerializeField] private double timeToleranceMs = 16.0;

	[SerializeField] private RenderTexture output;
	private DateTime lastTimestamp;
	private int kernelGreaterIdx;
	private int kernelLessIdx;
	private uint kTx, kTy, kTz; // thread group sizes (queried)
	private int lastProcessedInputTick = -1;
	private int lastProcessedMaskTick = -1;

	public override RenderTexture FrameTex => output;
	public override DateTime TimeStamp => lastTimestamp;

	private void OnEnable() {
		ValidateSerializedFields();
		ResolveKernelsAndThreadGroupSizes();
		SubscribeProviders();
		TryEnsureOutput();
	}

	private void OnDisable() {
		UnsubscribeProviders();
		// Do not destroy or release output as it may be an external asset.
	}

	private void ValidateSerializedFields() {
		if (shader == null) throw new InvalidOperationException("ComputeShader is not assigned.");
		if (output == null) throw new InvalidOperationException("Output RenderTexture must be assigned via Inspector.");
		if (inputProvider == null) throw new InvalidOperationException("Input FrameProvider is not assigned.");
		if (maskProvider == null) throw new InvalidOperationException("Mask FrameProvider is not assigned.");
		if (timeToleranceMs < 0) throw new InvalidOperationException("timeToleranceMs must be non-negative.");
	}

	private void ResolveKernelsAndThreadGroupSizes() {
		kernelGreaterIdx = shader.FindKernel(KernelNameGreaterEqual);
		kernelLessIdx = shader.FindKernel(KernelNameLessEqual);
		shader.GetKernelThreadGroupSizes(kernelGreaterIdx, out kTx, out kTy, out kTz);
	}

	private void SubscribeProviders() {
		inputProvider.OnFrameTexInit += OnAnyProviderInit;
		maskProvider.OnFrameTexInit += OnAnyProviderInit;
		inputProvider.OnFrameUpdated += OnAnyProviderUpdated;
		maskProvider.OnFrameUpdated += OnAnyProviderUpdated;
	}

	private void UnsubscribeProviders() {
		if (inputProvider != null) {
			inputProvider.OnFrameTexInit -= OnAnyProviderInit;
			inputProvider.OnFrameUpdated -= OnAnyProviderUpdated;
		}
		if (maskProvider != null) {
			maskProvider.OnFrameTexInit -= OnAnyProviderInit;
			maskProvider.OnFrameUpdated -= OnAnyProviderUpdated;
		}
	}

	private void OnAnyProviderInit(RenderTexture _) {
		TryEnsureOutput();
	}

	private void OnAnyProviderUpdated(RenderTexture _) {
		TryRun();
	}

	private void TryEnsureOutput() {
		var inTex = inputProvider.FrameTex;
		var mTex = maskProvider.FrameTex;
		if (inTex == null || mTex == null) return; // wait until providers initialize
		if (!inTex.IsCreated() || !mTex.IsCreated()) return;

		EnsureInputMaskCompatibilityOrThrow(inTex, mTex);
		EnsureThreadAlignmentOrThrow(inTex.width, inTex.height);
		EnsureOutputOrThrow(inTex);
	}

	private static void EnsureInputMaskCompatibilityOrThrow(RenderTexture inTex, RenderTexture mTex) {
		if (inTex.graphicsFormat != GraphicsFormat.R32_SFloat)
			throw new InvalidOperationException("Input RenderTexture must be R32_SFloat (R32Float).");
		if (mTex.graphicsFormat != GraphicsFormat.R32_SFloat)
			throw new InvalidOperationException("Mask RenderTexture must be R32_SFloat (R32Float).");
		if (inTex.width != mTex.width || inTex.height != mTex.height)
			throw new InvalidOperationException("Input and Mask must have identical dimensions.");
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

	private void TryRun() {
		var inTex = inputProvider.FrameTex;
		var mTex = maskProvider.FrameTex;
		if (inTex == null || mTex == null) return; // not ready yet
		if (!inTex.IsCreated() || !mTex.IsCreated()) return;
		EnsureInputMaskCompatibilityOrThrow(inTex, mTex);
		EnsureThreadAlignmentOrThrow(inTex.width, inTex.height);
		EnsureOutputOrThrow(inTex);

		var inTs = inputProvider.TimeStamp;
		var mTs = maskProvider.TimeStamp;
		double dtMs = Math.Abs((inTs - mTs).TotalMilliseconds);
		if (dtMs > timeToleranceMs) return; // outside tolerance -> skip until matching timestamps arrive

		int inTick = inputProvider.Tick;
		int mTick = maskProvider.Tick;
		if (inTick == lastProcessedInputTick && mTick == lastProcessedMaskTick) return; // avoid duplicate processing

		Dispatch(inTex, mTex, output);

		lastProcessedInputTick = inTick;
		lastProcessedMaskTick = mTick;
		lastTimestamp = (inTs >= mTs) ? inTs : mTs;
		TickUp();
	}

	public void ExecuteNow() {
		var inTex = inputProvider?.FrameTex;
		var mTex = maskProvider?.FrameTex;
		if (inTex == null || mTex == null) throw new InvalidOperationException("Providers not ready.");
		if (!inTex.IsCreated() || !mTex.IsCreated()) throw new InvalidOperationException("Provider textures are not created.");
		EnsureInputMaskCompatibilityOrThrow(inTex, mTex);
		EnsureThreadAlignmentOrThrow(inTex.width, inTex.height);
		EnsureOutputOrThrow(inTex);
		Dispatch(inTex, mTex, output);
		lastTimestamp = (inputProvider.TimeStamp >= maskProvider.TimeStamp) ? inputProvider.TimeStamp : maskProvider.TimeStamp;
		TickUp();
	}

	private void EnsureThreadAlignmentOrThrow(int width, int height) {
		if (kTx == 0 || kTy == 0) throw new InvalidOperationException("Kernel thread group sizes are invalid.");
		if (width % (int)kTx != 0 || height % (int)kTy != 0)
			throw new InvalidOperationException($"Dimensions must be multiples of thread group size {(int)kTx}x{(int)kTy}. Current: {width}x{height}");
	}

	private void Dispatch(RenderTexture inTex, RenderTexture mTex, RenderTexture outTex) {
		shader.SetTexture(mode == ThresholdMode.Greater ? kernelGreaterIdx : kernelLessIdx, ShaderPropInput, inTex);
		shader.SetTexture(mode == ThresholdMode.Greater ? kernelGreaterIdx : kernelLessIdx, ShaderPropMask, mTex);
		shader.SetTexture(mode == ThresholdMode.Greater ? kernelGreaterIdx : kernelLessIdx, ShaderPropOutput, outTex);
		shader.SetFloat(ShaderPropThreshold, threshold);
		shader.SetFloat(ShaderPropFillValue, fillVal);
		shader.SetInt(ShaderPropWidth, inTex.width);
		shader.SetInt(ShaderPropHeight, inTex.height);

		uint gx = kTx;
		uint gy = kTy;
		int groupsX = inTex.width / (int)gx;
		int groupsY = inTex.height / (int)gy;
		int kernel = mode == ThresholdMode.Greater ? kernelGreaterIdx : kernelLessIdx;
		shader.Dispatch(kernel, groupsX, groupsY, 1);
	}

	private void OnValidate() {
		if (threshold < 0f) threshold = 0f;
		if (threshold > 1f) threshold = 1f;
		if (timeToleranceMs < 0) timeToleranceMs = 0;
	}
}


