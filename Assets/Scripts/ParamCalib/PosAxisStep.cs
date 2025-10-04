using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class PosAxisStep : CalibStep {
    public enum AxisKind { X, Y, Z }

    [Header("Guide / Camera")]
    [SerializeField] private PoseDiffManager pose;
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

    [Header("Message")]
    [SerializeField] private string stepMessage = "Move body along axis following the guide, press when uncomfortable";

    public new string StepMessage => stepMessage;

    private bool _started;
    private float _offset;

    public override void StartCalib(){
        if (pose == null) throw new NullReferenceException("PosAxisStep: pose not assigned");
        if (guide == null) throw new NullReferenceException("PosAxisStep: guide not assigned");
        if (cameraTr == null){
            var main = Camera.main;
            if (main == null) throw new InvalidOperationException("PosAxisStep: camera not assigned and Camera.main not found");
            cameraTr = main.transform;
        }

        pose.Reset();

        _offset = 0f;
        Vector3 pos = cameraTr.position + cameraTr.forward * guideDist;
        guide.position = pos;
        guide.rotation = cameraTr.rotation;
        if (!guide.gameObject.activeSelf) guide.gameObject.SetActive(true);

        _started = true;
    }

    private void Update(){
        if (!_started) return;
        float dt = Time.deltaTime;
        float dir = reverse ? -1f : 1f;
        _offset += speedMps * dir * dt;

        Vector3 pos = cameraTr.position + cameraTr.forward * guideDist;
        if (kind == AxisKind.X) pos += cameraTr.right * _offset;
        else if (kind == AxisKind.Y) pos += cameraTr.up * _offset;
        else pos += cameraTr.forward * _offset; // Z
        guide.position = pos;
        guide.rotation = cameraTr.rotation;
    }

    public override void RecordAndEnd(ICalibSuite recorder){
        if (!_started) throw new InvalidOperationException("PosAxisStep: StartCalib must be called before RecordAndEnd");
        if (recorder == null) throw new ArgumentNullException(nameof(recorder));

        Vector3 t = pose.Translation;
        float mag;
        if (kind == AxisKind.X) mag = Mathf.Abs(t.x) * Mathf.Abs(safety);
        else if (kind == AxisKind.Y) mag = Mathf.Abs(t.y) * Mathf.Abs(safety);
        else mag = Mathf.Abs(t.z) * Mathf.Abs(safety);

        if (kind == AxisKind.X){
            if (reverse) recorder.RegisterParameter(new LeftParam   { Value = mag, Safety = Mathf.Abs(safety) });
            else         recorder.RegisterParameter(new RightParam  { Value = mag, Safety = Mathf.Abs(safety) });
        } else if (kind == AxisKind.Y){
            if (reverse) recorder.RegisterParameter(new DownParam   { Value = mag, Safety = Mathf.Abs(safety) });
            else         recorder.RegisterParameter(new UpParam     { Value = mag, Safety = Mathf.Abs(safety) });
        } else {
            if (reverse) recorder.RegisterParameter(new BackParam   { Value = mag, Safety = Mathf.Abs(safety) });
            else         recorder.RegisterParameter(new ForwardParam{ Value = mag, Safety = Mathf.Abs(safety) });
        }

        if (guide.gameObject.activeSelf) guide.gameObject.SetActive(false);
        _started = false;
    }
}


