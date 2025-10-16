using System;
using UnityEngine;

public class SqDifProvider : FrameProvider
{
    private const string KERNEL_NAME = "CSMain";
    private const float DEFAULT_INVALID_VALUE = -1.0f;
    private const double DEFAULT_TIME_THRESHOLD = 0.033;
    
    [SerializeField] private FrameProvider source;
    [SerializeField] private FrameProvider target;
    [SerializeField] private RenderTexture output;
    [SerializeField] private ComputeShader compute;
    [SerializeField] private double timeThresholdSec = DEFAULT_TIME_THRESHOLD;
    [SerializeField] private float invalidValue = DEFAULT_INVALID_VALUE;
    [SerializeField] private bool verboseLogs = false;
    
    private int kernel;
    private DateTime lastOutputTimestamp;
    private bool isInitialized;
    
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
        else if (verboseLogs) Debug.LogWarning($"[SqDifProvider] Source is null, cannot subscribe to OnFrameUpdated on {gameObject.name}");
        
        if (target != null) target.OnFrameUpdated += OnTargetFrameUpdated;
        else if (verboseLogs) Debug.LogWarning($"[SqDifProvider] Target is null, cannot subscribe to OnFrameUpdated on {gameObject.name}");
        
        EnsureOutputCreated();
    }
    
    private void OnDisable()
    {
        if (source != null) source.OnFrameUpdated -= OnSourceFrameUpdated;
        if (target != null) target.OnFrameUpdated -= OnTargetFrameUpdated;
    }
    
    private void ValidateConfiguration()
    {
        if (source == null)
            throw new InvalidOperationException("Source FrameProvider is not assigned");
        
        if (target == null)
            throw new InvalidOperationException("Target FrameProvider is not assigned");
        
        if (output == null)
            throw new InvalidOperationException("Output RenderTexture is not assigned");
        
        if (compute == null)
            throw new InvalidOperationException("ComputeShader is not assigned");
    }
    
    private void InitializeComputeShader()
    {
        kernel = compute.FindKernel(KERNEL_NAME);
        if (kernel < 0)
            throw new InvalidOperationException($"Kernel '{KERNEL_NAME}' not found in compute shader");
    }
    
    private void EnsureOutputCreated()
    {
        if (output != null && !output.IsCreated())
        {
            output.Create();
        }
        
        ValidateOutputConfiguration();
    }
    
    private void ValidateOutputConfiguration()
    {
        if (output == null)
        {
            if (verboseLogs) Debug.LogWarning($"[SqDifProvider] Output is null, skipping validation on {gameObject.name}");
            return;
        }
            
        if (output.format != RenderTextureFormat.RFloat)
            throw new InvalidOperationException($"Output RenderTexture format must be RFloat, but got {output.format}");
        
        if (!output.enableRandomWrite)
            throw new InvalidOperationException("Output RenderTexture must have enableRandomWrite set to true");
        
        if (source.FrameTex != null)
        {
            if (output.width != source.FrameTex.width || output.height != source.FrameTex.height)
                throw new InvalidOperationException($"Output RenderTexture size ({output.width}x{output.height}) must match source size ({source.FrameTex.width}x{source.FrameTex.height})");
        }
        else if (verboseLogs) Debug.Log($"[SqDifProvider] Source FrameTex is null, skipping size validation on {gameObject.name}");
    }
    
    private void OnSourceFrameUpdated(RenderTexture frameTex)
    {
        if (!isInitialized)
        {
            ValidateOutputConfiguration();
            isInitialized = true;
            OnFrameTexInitialized();
        }
        
        ProcessFramePair(source.TimeStamp, target.TimeStamp);
    }
    
    private void OnTargetFrameUpdated(RenderTexture frameTex)
    {
        if (!isInitialized)
        {
            ValidateOutputConfiguration();
            isInitialized = true;
            OnFrameTexInitialized();
        }
        
        ProcessFramePair(source.TimeStamp, target.TimeStamp);
    }
    
    private void ProcessFramePair(DateTime sourceTimestamp, DateTime targetTimestamp)
    {
        double timeDifference = Math.Abs((sourceTimestamp - targetTimestamp).TotalSeconds);
        
        if (timeDifference <= timeThresholdSec)
        {
            ExecuteComputeShader(sourceTimestamp, targetTimestamp);
        }
        else if (verboseLogs) Debug.Log($"[SqDifProvider] Time difference {timeDifference:F4}s exceeds threshold {timeThresholdSec:F4}s, skipping compute on {gameObject.name}");
    }
    
    private void ExecuteComputeShader(DateTime sourceTimestamp, DateTime targetTimestamp)
    {
        if (source.FrameTex == null || target.FrameTex == null)
        {
            if (verboseLogs) Debug.LogWarning($"[SqDifProvider] Source or target FrameTex is null, skipping compute on {gameObject.name}");
            return;
        }
        
        // Set textures
        compute.SetTexture(kernel, "_Source", source.FrameTex);
        compute.SetTexture(kernel, "_Target", target.FrameTex);
        compute.SetTexture(kernel, "_Output", output);
        
        // Set constants
        compute.SetFloat("_InvalidValue", invalidValue);
        compute.SetVector("_TargetSize", new Vector2(target.FrameTex.width, target.FrameTex.height));
        compute.SetVector("_SourceSize", new Vector2(source.FrameTex.width, source.FrameTex.height));
        
        // Dispatch
        int threadGroupsX = Mathf.CeilToInt(output.width / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(output.height / 8.0f);
        compute.Dispatch(kernel, threadGroupsX, threadGroupsY, 1);
        
        // Update timestamp (use later timestamp)
        lastOutputTimestamp = sourceTimestamp > targetTimestamp ? sourceTimestamp : targetTimestamp;
        
        // Notify frame update
        TickUp();
    }
}
