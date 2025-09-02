using UnityEngine;
using System;

public class RefineProcessor : AsyncFrameProvider {
    [SerializeField] private DepthRefiner refiner;
    [SerializeField] private FrameProvider baseProvider;

    private RenderTexture _output;
    private DateTime _timestamp;

    public override RenderTexture FrameTex => _output;
    public override DateTime TimeStamp => _timestamp;

    private void OnEnable(){
        if (baseProvider != null){
            baseProvider.OnFrameUpdated += OnBaseUpdated;
        }
    }

    private void OnDisable(){
        if (baseProvider != null){
            baseProvider.OnFrameUpdated -= OnBaseUpdated;
        }
    }

    private void OnBaseUpdated(RenderTexture baseTex){
        if (refiner == null || baseTex == null) return;
        var id = ProcessStart();
        _output = refiner.Refine(baseTex);
        if (!IsInitTexture){
            OnFrameTexInitialized();
            IsInitTexture = true;
        }
        _timestamp = DateTime.Now;
        ProcessEnd(id);
    }
}