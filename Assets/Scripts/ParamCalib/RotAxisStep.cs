using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class RotAxisStep : CalibStep {
    public enum AxisKind { X, Y, Z }

    [Header("Guide / Camera")]
    [SerializeField] private PoseDiffManager pose;
    [SerializeField] private StructureManager splatMan;
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
    
    [Header("Limit")]
    [SerializeField] private float maxAngleDeg = 90f;

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
    [SerializeField] private string stepMessage = "Rotate device following the guide, press when uncomfortable";

    public override string StepMessage => stepMessage;

    private bool _started;
    private float _angleDeg;
    private float _delayRemain;
    private Vector3 _center; // only for Z
    private float _startAngleDeg;
    private Vector3 _camPos0;
    private Quaternion _camRot0;
    private Vector3 _fwd0, _up0, _right0;
    private bool _atMax;
    private Material _material;

    public override void StartCalib(){
        if (pose == null) throw new NullReferenceException("RotAxisStep: pose not assigned");
        if (guide == null) throw new NullReferenceException("RotAxisStep: guide not assigned");
        if (splatMan == null) throw new NullReferenceException("RotAxisStep: splatMan not assigned");
        if (maxAngleDeg <= 0f) throw new InvalidOperationException("RotAxisStep: maxAngleDeg must be > 0");

        if (cameraTr == null){
            var main = Camera.main;
            if (main == null) throw new InvalidOperationException("RotAxisStep: camera not assigned and Camera.main not found");
            cameraTr = main.transform;
        }

        _camPos0 = cameraTr.position;
        _camRot0 = cameraTr.rotation;
        _fwd0 = cameraTr.forward;
        _up0 = cameraTr.up;
        _right0 = cameraTr.right;

        _angleDeg = startDeg;
        _delayRemain = Mathf.Max(0f, startDelaySec);
        _startAngleDeg = _angleDeg;

        if (kind == AxisKind.Z){
            _center = _camPos0 + _fwd0 * guideDist;
            guide.position = _center; // spin in place for Z
        } else {
            Vector3 dir = XYOrbitDirBasis(_fwd0, _right0, _up0, kind, _angleDeg);
            guide.position = _camPos0 + dir * guideDist;
        }
        guide.rotation = _camRot0;
        if (guideMaterial == null) throw new NullReferenceException("RotAxisStep: guideMaterial not assigned");
        _material = guideMaterial;
        if (!_material.HasProperty("_BaseColor")) throw new InvalidOperationException("RotAxisStep: material must have _BaseColor");
        _atMax = false;
        SetGuideColor(resetColor);
        if (!guide.gameObject.activeSelf) guide.gameObject.SetActive(true);
        if (stepObject != null && !stepObject.activeSelf) stepObject.SetActive(true);

        _started = true;
    }

    private void Update(){
        if (!_started) return;
        float dt = Time.deltaTime;

        if (_delayRemain > 0f){
            _delayRemain -= dt;
            if (_delayRemain > 0f){
                // Keep guide at initial position
                if (kind == AxisKind.Z){
                    guide.position = _center; // hold at center during delay
                } else {
                    Vector3 dirHold = XYOrbitDirBasis(_fwd0, _right0, _up0, kind, _angleDeg);
                    guide.position = _camPos0 + dirHold * guideDist;
                }
                guide.rotation = _camRot0;
                return;
            }
        }

        float sgn = reverse ? -1f : 1f;
        _angleDeg += sgn * angVelDegPerSec * dt;
        float halfRange = Mathf.Abs(maxAngleDeg);
        float minA = _startAngleDeg - halfRange;
        float maxA = _startAngleDeg + halfRange;
        float beforeClamp = _angleDeg;
        _angleDeg = Mathf.Clamp(_angleDeg, minA, maxA);
        bool hitMax = !Mathf.Approximately(beforeClamp, _angleDeg) || Mathf.Approximately(_angleDeg, minA) || Mathf.Approximately(_angleDeg, maxA);

        if (kind == AxisKind.Z){
            Quaternion q = Quaternion.AngleAxis(-_angleDeg, _fwd0);
            guide.position = _center; // no orbit for Z
            guide.rotation = q * _camRot0; // spin (roll) in place
        } else {
            Vector3 dir = XYOrbitDirBasis(_fwd0, _right0, _up0, kind, _angleDeg);
            guide.position = _camPos0 + dir * guideDist;
            guide.rotation = _camRot0;
        }
        if (hitMax != _atMax){
            SetGuideColor(hitMax ? maxColor : resetColor);
            _atMax = hitMax;
        }
    }

    public override void RecordAndEnd(ICalibSuite recorder){
        if (!_started)
            throw new InvalidOperationException("RotAxisStep: StartCalib must be called before RecordAndEnd");
        if (recorder == null) 
            throw new ArgumentNullException(nameof(recorder));
        
        if(!pose.TryGetDiffFrom(splatMan.Generation, out var _, out var q))
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

        if (string.IsNullOrEmpty(paramId))
            throw new InvalidOperationException("RotAxisStep: paramId not set");

        var absSafety = Mathf.Abs(safety);
        recorder.RegisterParameter(paramId, new AngleParam { Id = paramId, Value = mag, Safety = absSafety });

        if (guide.gameObject.activeSelf) guide.gameObject.SetActive(false);
        if (stepObject != null && stepObject.activeSelf) stepObject.SetActive(false);
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

    private static Vector3 XYOrbitDirBasis(Vector3 fwd, Vector3 right, Vector3 up, AxisKind k, float angleDeg){
        if (k == AxisKind.X){
            Quaternion q = Quaternion.AngleAxis(-angleDeg, right);
            return q * fwd;
        } else if (k == AxisKind.Y){
            Quaternion q = Quaternion.AngleAxis(angleDeg, up);
            return q * fwd;
        }
        throw new ArgumentOutOfRangeException();
    }

    private void SetGuideColor(Color color){
        _material.SetColor("_BaseColor", color);
    }
}


