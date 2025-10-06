using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class RotAxisStep : CalibStep {
    public enum AxisKind { X, Y, Z }

    [Header("Guide / Camera")]
    [SerializeField] private PoseDiffManager pose;
    [SerializeField] private SplatManager splatMan;
    [SerializeField] private Transform cameraTr;
    [SerializeField] private Transform guide;

    [Header("Mode")]
    [SerializeField] private AxisKind kind = AxisKind.Y;
    [SerializeField] private bool reverse = false; // invert angular direction and record counter params

    [Header("Guide Orbit (deg/sec, meters)")]
    [SerializeField] private float angVelDegPerSec = 20f;
    [SerializeField] private float startDeg = 0f;
    [SerializeField] private float guideDist = 1.0f; // used for X/Y
    [SerializeField] private float orbitRadiusM = 0.2f; // used for Z

    [Header("Sampling")]
    [SerializeField] private float safety = 1.0f;

    [Header("Message")]
    [SerializeField] private string stepMessage = "Rotate device following the guide, press when uncomfortable";

    public override string StepMessage => stepMessage;

    private bool _started;
    private float _angleDeg;
    private Vector3 _center; // only for Z
    private Vector3 _offset; // only for Z

    public override void StartCalib(){
        if (pose == null) throw new NullReferenceException("RotAxisStep: pose not assigned");
        if (guide == null) throw new NullReferenceException("RotAxisStep: guide not assigned");
        if (splatMan == null) throw new NullReferenceException("RotAxisStep: splatMan not assigned");

        if (cameraTr == null){
            var main = Camera.main;
            if (main == null) throw new InvalidOperationException("RotAxisStep: camera not assigned and Camera.main not found");
            cameraTr = main.transform;
        }

        _angleDeg = startDeg;

        if (kind == AxisKind.Z){
            _center = cameraTr.position + cameraTr.forward * guideDist;
            _offset = cameraTr.right * orbitRadiusM;
            guide.position = _center + _offset;
        } else {
            Vector3 dir = XYOrbitDir(cameraTr, kind, _angleDeg);
            guide.position = cameraTr.position + dir * guideDist;
        }
        guide.rotation = cameraTr.rotation;
        if (!guide.gameObject.activeSelf) guide.gameObject.SetActive(true);

        _started = true;
    }

    private void Update(){
        if (!_started) return;
        float dt = Time.deltaTime;
        float sgn = reverse ? -1f : 1f;
        _angleDeg += sgn * angVelDegPerSec * dt;

        if (kind == AxisKind.Z){
            Quaternion q = Quaternion.AngleAxis(_angleDeg, cameraTr.forward);
            guide.position = _center + q * _offset;
        } else {
            Vector3 dir = XYOrbitDir(cameraTr, kind, _angleDeg);
            guide.position = cameraTr.position + dir * guideDist;
        }
        guide.rotation = cameraTr.rotation;
    }

    public override void RecordAndEnd(ICalibSuite recorder){
        if (!_started)
            throw new InvalidOperationException("RotAxisStep: StartCalib must be called before RecordAndEnd");
        if (recorder == null) 
            throw new ArgumentNullException(nameof(recorder));
        
        if(!pose.TryGetDiffFrom(splatMan.SplatGeneration, out var _, out var q))
            throw new InvalidOperationException("RotAxisStep: generation not avail");

        Vector3 e = q.eulerAngles;
        e.x = Normalize180(e.x);
        e.y = Normalize180(e.y);
        e.z = Normalize180(e.z);

        float mag;
        switch (kind){
            case AxisKind.X: mag = Mathf.Abs(e.x) * Mathf.Abs(safety); break;
            case AxisKind.Y: mag = Mathf.Abs(e.y) * Mathf.Abs(safety); break;
            case AxisKind.Z: mag = Mathf.Abs(e.z) * Mathf.Abs(safety); break;
            default: throw new ArgumentOutOfRangeException();
        }

        // Register corresponding parameter
        if (kind == AxisKind.X){
            if (reverse) recorder.RegisterParameter(new CounterPitchParam { Value = mag, Safety = Mathf.Abs(safety) });
            else         recorder.RegisterParameter(new PitchParam         { Value = mag, Safety = Mathf.Abs(safety) });
        } else if (kind == AxisKind.Y){
            if (reverse) recorder.RegisterParameter(new CounterYawParam   { Value = mag, Safety = Mathf.Abs(safety) });
            else         recorder.RegisterParameter(new YawParam           { Value = mag, Safety = Mathf.Abs(safety) });
        } else { // Z
            if (reverse) recorder.RegisterParameter(new CounterRollParam  { Value = mag, Safety = Mathf.Abs(safety) });
            else         recorder.RegisterParameter(new RollParam          { Value = mag, Safety = Mathf.Abs(safety) });
        }

        if (guide.gameObject.activeSelf) guide.gameObject.SetActive(false);
        _started = false;
    }

    private static float Normalize180(float deg){
        return Mathf.Repeat(deg + 180f, 360f) - 180f;
    }

    private static Vector3 XYOrbitDir(Transform cam, AxisKind k, float angleDeg){
        if (k == AxisKind.X){
            Quaternion q = Quaternion.AngleAxis(-angleDeg, cam.right);
            return q * cam.forward;
        } else if (k == AxisKind.Y){
            Quaternion q = Quaternion.AngleAxis(angleDeg, cam.up);
            return q * cam.forward;
        }
        throw new ArgumentOutOfRangeException();
    }
}


