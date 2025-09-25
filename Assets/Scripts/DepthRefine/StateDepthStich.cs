using UnityEngine;
using System;

public class StateDepthStich : FrameProvider
{
    [Header("State")]
    [SerializeField] private StateManager state;

    [Header("Providers (event-driven)")]
    [SerializeField] private FrameProvider corrected;   // corrected stream (ex-src)
    [SerializeField] private FrameProvider immediate;   // immediate stream (ex-support)

    [Header("Material")]
    [SerializeField] private Material stitchMaterial; // ImOTAR/DepthStich

    [Header("Output")]
    [SerializeField] private RenderTexture output;   // RFloat

    [Header("Sync")]
    [SerializeField] private float maxTimeSyncDifferenceMs = 50f;

    [SerializeField] private bool verboseLogs = false;

    private DateTime _timestamp;
    public override RenderTexture FrameTex => output;
    public override DateTime TimeStamp => _timestamp;

    // Latest frames cache (consumed after stitch)
    private RenderTexture _latestCorrectedRT;
    private DateTime _latestCorrectedTs;
    private bool _hasCorrected;

    private RenderTexture _latestImmediateRT;
    private DateTime _latestImmediateTs;
    private bool _hasImmediate;

    private void OnEnable(){
        if (state == null) throw new NullReferenceException("StateDepthStich: state not assigned");
        if (corrected == null) throw new NullReferenceException("StateDepthStich: corrected not assigned");
        if (immediate == null) throw new NullReferenceException("StateDepthStich: immediate not assigned");
        if (stitchMaterial == null) throw new NullReferenceException("StateDepthStich: stitchMaterial not assigned");

        corrected.OnFrameUpdated += OnCorrectedUpdated;
        immediate.OnFrameUpdated += OnImmediateUpdated;

        if (!output.IsCreated()) 
            output.Create();

        _hasCorrected = false;
        _hasImmediate = false;
    }

    private void OnDisable(){
        corrected.OnFrameUpdated -= OnCorrectedUpdated;
        immediate.OnFrameUpdated -= OnImmediateUpdated;
    }

    private void OnCorrectedUpdated(RenderTexture rt){
        if (state.CurrState != State.ALIVE){
            // Ignore corrected when not alive
            return;
        }
        _latestCorrectedRT = rt;
        _latestCorrectedTs = corrected.TimeStamp;
        _hasCorrected = true;
        TryStitchIfReady();
    }

    private void OnImmediateUpdated(RenderTexture rt){
        if (state.CurrState == State.ALIVE){
            _latestImmediateRT = rt;
            _latestImmediateTs = immediate.TimeStamp;
            _hasImmediate = true;
            TryStitchIfReady();

            return;
        }

        // Not ALIVE: pass-through immediate → output, resampled to corrected size via Blit
        if (rt == null || !rt.IsCreated())
            throw new InvalidOperationException("StateDepthStich: immediate texture not ready in non-ALIVE state");
        if (rt.format != RenderTextureFormat.RFloat)
            throw new InvalidOperationException("StateDepthStich: immediate must be RFloat in non-ALIVE state");
        var corrRT = corrected.FrameTex;
        if (corrRT == null || corrRT.format != RenderTextureFormat.RFloat)
            throw new InvalidOperationException("StateDepthStich: corrected must be RFloat in non-ALIVE state");
        if (!corrRT.IsCreated()){
            if(verboseLogs)
                Debug.LogWarning("StateDepthStich: corrRT not created");
            return;
        }

        EnsureOutput(corrRT.width, corrRT.height);

        // Always Blit to allow scaling to corrected size (CopyTexture cannot scale)
        Graphics.Blit(rt, output);

        if (!IsInitTexture) {
            OnFrameTexInitialized();
            IsInitTexture = true;
        }
        _timestamp = DateTime.Now;
        TickUp();
    }

    private void TryStitchIfReady(){
        if (state.CurrState != State.ALIVE) return;
        if (!_hasCorrected || !_hasImmediate) return;
        if (_latestCorrectedRT == null || _latestImmediateRT == null) return;
        if (!_latestCorrectedRT.IsCreated() || !_latestImmediateRT.IsCreated()) return;
        if (_latestCorrectedRT.format != RenderTextureFormat.RFloat || _latestImmediateRT.format != RenderTextureFormat.RFloat)
            throw new InvalidOperationException("StateDepthStich: inputs must be RFloat");

        var dtMs = Mathf.Abs((float)(_latestCorrectedTs - _latestImmediateTs).TotalMilliseconds);
        if (dtMs > maxTimeSyncDifferenceMs) return;

        // Align output size to corrected. immediate is resampled in shader.
        EnsureOutput(_latestCorrectedRT.width, _latestCorrectedRT.height);

        stitchMaterial.SetTexture("_Src", _latestCorrectedRT);
        stitchMaterial.SetTexture("_Support", _latestImmediateRT);
        Graphics.Blit(null, output, stitchMaterial, 0);

        if (!IsInitTexture)
        {
            OnFrameTexInitialized();
            IsInitTexture = true;
        }
        _timestamp = DateTime.Now;
        TickUp();

        // consume both
        _hasCorrected = false;
        _hasImmediate = false;
    }

    private void EnsureOutput(int w, int h){
        if (output == null)
            throw new NullReferenceException("StateDepthStich: output not assigned");
        if (output.format != RenderTextureFormat.RFloat)
            throw new InvalidOperationException("StateDepthStich: output must be RFloat");
        if (output.width != w || output.height != h)
            throw new InvalidOperationException("StateDepthStich: output size mismatch with input");
    }
}


