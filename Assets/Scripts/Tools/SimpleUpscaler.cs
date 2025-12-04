using System;
using UnityEngine;

[DisallowMultipleComponent]
public class SimpleUpscaler : FrameProvider
{
    [Header("Source")]
    [Tooltip("FrameProvider that supplies the low-resolution depth texture.")]
    [SerializeField] private FrameProvider _source;

    [Header("Target Resolution")]
    [Tooltip("Width of the upscaled RenderTexture.")]
    [SerializeField] private int _targetWidth = 0;

    [Tooltip("Height of the upscaled RenderTexture.")]
    [SerializeField] private int _targetHeight = 0;

    [Header("Output")]
    [Tooltip("Upscaled RenderTexture (RFloat). Leave empty to let this component allocate.")]
    [SerializeField] private RenderTexture _output;

    public override RenderTexture FrameTex => _output;
    public override DateTime TimeStamp => _timestamp;

    private DateTime _timestamp;
    private bool _ownsOutput;

    private void OnEnable()
    {
        if (_source == null)
            throw new NullReferenceException("SimpleUpscaler: Source FrameProvider is not assigned.");
        if (_targetWidth <= 0 || _targetHeight <= 0)
            throw new InvalidOperationException("SimpleUpscaler: Target width and height must be positive.");

        _source.OnFrameTexInit += OnSourceInit;
        _source.OnFrameUpdated += OnSourceUpdated;

        if (_source.IsInitTexture)
        {
            TryUpscale(_source.FrameTex);
        }
    }

    private void OnDisable()
    {
        if (_source != null)
        {
            _source.OnFrameTexInit -= OnSourceInit;
            _source.OnFrameUpdated -= OnSourceUpdated;
        }

        if (_ownsOutput)
        {
            ReleaseOutput();
        }
    }

    private void OnSourceInit(RenderTexture sourceTex)
    {
        TryUpscale(sourceTex);
    }

    private void OnSourceUpdated(RenderTexture sourceTex)
    {
        TryUpscale(sourceTex);
    }

    private void TryUpscale(RenderTexture sourceTex)
    {
        if (sourceTex == null)
            throw new ArgumentNullException(nameof(sourceTex), "SimpleUpscaler: Source RenderTexture is null.");
        if (!sourceTex.IsCreated())
            throw new InvalidOperationException("SimpleUpscaler: Source RenderTexture is not created.");
        if (sourceTex.format != RenderTextureFormat.RFloat)
            throw new ArgumentException("SimpleUpscaler: Source RenderTexture must use RenderTextureFormat.RFloat.", nameof(sourceTex));

        EnsureOutput();
        Graphics.Blit(sourceTex, _output);
        _timestamp = DateTime.Now;

        if (!IsInitTexture)
        {
            OnFrameTexInitialized();
            IsInitTexture = true;
        }

        TickUp();
    }

    private void EnsureOutput()
    {
        if (_output == null)
        {
            _output = new RenderTexture(_targetWidth, _targetHeight, 0, RenderTextureFormat.RFloat)
            {
                useMipMap = false,
                autoGenerateMips = false,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            _output.Create();
            _ownsOutput = true;
            return;
        }

        _ownsOutput = false;

        if (_output.width != _targetWidth || _output.height != _targetHeight)
        {
            throw new InvalidOperationException("SimpleUpscaler: Provided output RenderTexture size does not match target resolution.");
        }

        if (_output.format != RenderTextureFormat.RFloat)
        {
            throw new InvalidOperationException("SimpleUpscaler: Provided output RenderTexture must use RenderTextureFormat.RFloat.");
        }

        if (!_output.IsCreated())
        {
            throw new InvalidOperationException("SimpleUpscaler: Provided output RenderTexture is not created.");
        }

        _output.wrapMode = TextureWrapMode.Clamp;
        _output.filterMode = FilterMode.Bilinear;
    }

    private void ReleaseOutput()
    {
        if (_output == null)
        {
            return;
        }

        if (_output.IsCreated())
        {
            _output.Release();
        }

        _output = null;
    }
}

