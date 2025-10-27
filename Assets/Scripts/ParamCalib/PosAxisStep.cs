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

    [Header("Limit")]
    [SerializeField] private float maxOffsetM = 0.5f;

    [Header("Sampling")]
    [SerializeField] private float safety = 1.0f;

    [Header("Timing")]
    [SerializeField] private float startDelaySec = 0f;

    [Header("Parameter")]
    [SerializeField] private string paramId;

    [Header("Visual")]
    [SerializeField] private GameObject stepObject;

    [Header("Visual Feedback")]
    [SerializeField] private Color resetColor = Color.white;
    [SerializeField] private Color maxColor = Color.red;
    [SerializeField] private Material guideMaterial;

    [Header("Message")]
    [SerializeField] private string stepMessage = "Move body along axis following the guide, press when uncomfortable";

    public override string StepMessage => stepMessage;

    private bool _started;
    private float _offset;
    private float _delayRemain;
    private Vector3 _camPos0;
    private Quaternion _camRot0;
    private Vector3 _fwd0, _up0, _right0;
    private bool _atMax;
    private Material _material;

    public override void StartCalib(){
        if (pose == null) throw new NullReferenceException("PosAxisStep: pose not assigned");
        if (guide == null) throw new NullReferenceException("PosAxisStep: guide not assigned");
        if (splatMan == null) throw new NullReferenceException("PosAxisStep: splatMan not assigned");
        if (maxOffsetM <= 0f) throw new InvalidOperationException("PosAxisStep: maxOffsetM must be > 0");
        if (cameraTr == null){
            var main = Camera.main;
            if (main == null) throw new InvalidOperationException("PosAxisStep: camera not assigned and Camera.main not found");
            cameraTr = main.transform;
        }

        _camPos0 = cameraTr.position;
        _camRot0 = cameraTr.rotation;
        _fwd0 = cameraTr.forward;
        _up0 = cameraTr.up;
        _right0 = cameraTr.right;

        _offset = 0f;
        _delayRemain = Mathf.Max(0f, startDelaySec);
        Vector3 pos = _camPos0 + _fwd0 * guideDist;
        guide.position = pos;
        guide.rotation = _camRot0;
        if (guideMaterial == null) throw new NullReferenceException("PosAxisStep: guideMaterial not assigned");
        _material = guideMaterial;
        if (!_material.HasProperty("_BaseColor")) throw new InvalidOperationException("PosAxisStep: material must have _BaseColor");
        SetGuideColor(resetColor);
        if (!guide.gameObject.activeSelf) guide.gameObject.SetActive(true);
        if (stepObject != null && !stepObject.activeSelf) stepObject.SetActive(true);

        _started = true;
    }

    private void Update(){
        if (!_started) return;
        float dt = Time.deltaTime;

        Vector3 pos = _camPos0 + _fwd0 * guideDist;

        if (_delayRemain > 0f){
            _delayRemain -= dt;
            if (_delayRemain > 0f){
                guide.position = pos;
                guide.rotation = _camRot0;
                return;
            }
        }

        float dir = reverse ? -1f : 1f;
        _offset += speedMps * dir * dt;
        float absMax = Mathf.Abs(maxOffsetM);
        float clamped = Mathf.Clamp(_offset, -absMax, absMax);
        bool hitMax = (clamped == absMax) || (clamped == -absMax);
        _offset = clamped;

        if (kind == AxisKind.X) pos += _right0 * _offset;
        else if (kind == AxisKind.Y) pos += _up0 * _offset;
        else pos += _fwd0 * _offset; // Z
        guide.position = pos;
        guide.rotation = _camRot0;

        if (hitMax != _atMax){
            SetGuideColor(hitMax ? maxColor : resetColor);
            _atMax = hitMax;
        }
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

    private void SetGuideColor(Color color){
        _material.SetColor("_BaseColor", color);
    }
}


