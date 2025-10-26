using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class PosAxisStep : CalibStep {
    public enum AxisKind { X, Y, Z }

    [Header("Guide / Camera")]
    [SerializeField] private PoseDiffManager pose;
    [SerializeField] private StructureManager splatMan;
    [SerializeField] private Transform cameraTr;
    [SerializeField] private Transform guide;

    [Header("Mode")]
    [SerializeField] private AxisKind kind = AxisKind.X;
    [SerializeField] private bool reverse = false; // invert motion and record opposite param

    [Header("Guide Motion (meters, m/s)")]
    [SerializeField] private float guideDist = 1.0f; // forward distance
    [SerializeField] private float speedMps = 0.2f;  // along axis (+) or (-)

    [Header("Sampling")]
    [SerializeField] private float safety = 1.0f;

    [Header("Timing")]
    [SerializeField] private float startDelaySec = 0f;

    [Header("Parameter")]
    [SerializeField] private string paramId;

    [Header("Visual")]
    [SerializeField] private GameObject stepObject;

    [Header("Message")]
    [SerializeField] private string stepMessage = "Move body along axis following the guide, press when uncomfortable";

    public override string StepMessage => stepMessage;

    private bool _started;
    private float _offset;
    private float _delayRemain;

    public override void StartCalib(){
        if (pose == null) throw new NullReferenceException("PosAxisStep: pose not assigned");
        if (guide == null) throw new NullReferenceException("PosAxisStep: guide not assigned");
        if (splatMan == null) throw new NullReferenceException("PosAxisStep: splatMan not assigned");
        if (cameraTr == null){
            var main = Camera.main;
            if (main == null) throw new InvalidOperationException("PosAxisStep: camera not assigned and Camera.main not found");
            cameraTr = main.transform;
        }

        _offset = 0f;
        _delayRemain = Mathf.Max(0f, startDelaySec);
        Vector3 pos = cameraTr.position + cameraTr.forward * guideDist;
        guide.position = pos;
        guide.rotation = cameraTr.rotation;
        if (!guide.gameObject.activeSelf) guide.gameObject.SetActive(true);
        if (stepObject != null && !stepObject.activeSelf) stepObject.SetActive(true);

        _started = true;
    }

    private void Update(){
        if (!_started) return;
        float dt = Time.deltaTime;

        Vector3 pos = cameraTr.position + cameraTr.forward * guideDist;

        if (_delayRemain > 0f){
            _delayRemain -= dt;
            if (_delayRemain > 0f){
                guide.position = pos;
                guide.rotation = cameraTr.rotation;
                return;
            }
        }

        float dir = reverse ? -1f : 1f;
        _offset += speedMps * dir * dt;

        if (kind == AxisKind.X) pos += cameraTr.right * _offset;
        else if (kind == AxisKind.Y) pos += cameraTr.up * _offset;
        else pos += cameraTr.forward * _offset; // Z
        guide.position = pos;
        guide.rotation = cameraTr.rotation;
    }

    public override void RecordAndEnd(ICalibSuite recorder){
        if (!_started) throw new InvalidOperationException("PosAxisStep: StartCalib must be called before RecordAndEnd");
        if (recorder == null) throw new ArgumentNullException(nameof(recorder));

        if(!pose.TryGetDiffFrom(splatMan.Generation, out var t, out var _))
            throw new InvalidOperationException("PosAxisStep: poseDiff not avail");

        float mag;
        if (kind == AxisKind.X) mag = Mathf.Abs(t.x) * Mathf.Abs(safety);
        else if (kind == AxisKind.Y) mag = Mathf.Abs(t.y) * Mathf.Abs(safety);
        else mag = Mathf.Abs(t.z) * Mathf.Abs(safety);

        if (string.IsNullOrEmpty(paramId))
            throw new InvalidOperationException("PosAxisStep: paramId not set");

        var absSafety = Mathf.Abs(safety);
        recorder.RegisterParameter(paramId, new DistanceParam { Id = paramId, Value = mag, Safety = absSafety });

        if (guide.gameObject.activeSelf) guide.gameObject.SetActive(false);
        if (stepObject != null && stepObject.activeSelf) stepObject.SetActive(false);
        _started = false;
    }
}


