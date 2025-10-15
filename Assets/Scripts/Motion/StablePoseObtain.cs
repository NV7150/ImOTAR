using System;
using UnityEngine;

[DisallowMultipleComponent]
public class StablePoseObtain : MotionObtainBase {
    [Header("Source")]
    [SerializeField] private MotionObtain source;

    [Header("Stability")]
    [SerializeField, Min(1f)] private float stableTimeMs = 400f;
    [SerializeField, Min(0f)] private float rotVelStableDegPerSec = 2.0f;
    [SerializeField, Min(0f)] private float posVelStableMps = 0.01f;
    [SerializeField, Range(0f, 1f)] private float smoothFactor = 0.2f;

    [Header("Freshness")]
    [SerializeField, Min(1f)] private float maxReferenceAgeMs = 200f;

    private bool _hasPrev;
    private DateTime _prevTs;
    private Quaternion _prevRot = Quaternion.identity;
    private Vector3 _prevPos = Vector3.zero;

    private float _emaRotVel;
    private float _emaPosVel;
    private float _stableAccumMs;

    private bool _hasRef;
    private Quaternion _refRot = Quaternion.identity;
    private Vector3 _refPos = Vector3.zero;

    private void OnEnable(){
        if (source == null) throw new NullReferenceException("StablePoseObtain: source not assigned");
        _hasPrev = false;
        _emaRotVel = 0f;
        _emaPosVel = 0f;
        _stableAccumMs = 0f;
        _hasRef = false;
        ClearAllHistory();
    }

    private void Update(){
        if (!source.TryGetLatestData<AbsoluteRotationData>(out var r)) return;
        if (!source.TryGetLatestData<AbsolutePositionData>(out var p)) return;

        var ts = r.Timestamp;
        var rot = r.Rotation;
        var pos = p.Position;

        if (!_hasPrev){
            _prevTs = ts;
            _prevRot = rot;
            _prevPos = pos;
            _hasPrev = true;
            return;
        }

        float dt = Mathf.Max(1e-3f, (float)(ts - _prevTs).TotalSeconds);
        float angDeg = Quaternion.Angle(_prevRot, rot);
        float rotVel = angDeg / dt;
        float posVel = (pos - _prevPos).magnitude / dt;

        float a = Mathf.Clamp01(smoothFactor);
        _emaRotVel = Mathf.Lerp(rotVel, _emaRotVel, 1f - a);
        _emaPosVel = Mathf.Lerp(posVel, _emaPosVel, 1f - a);

        bool isStable = (_emaRotVel <= rotVelStableDegPerSec) && (_emaPosVel <= posVelStableMps);
        if (isStable){
            _stableAccumMs += dt * 1000f;

            if (!_hasRef){
                _refRot = rot;
                _refPos = pos;
                _hasRef = true;
            } else {
                float w = Mathf.Clamp01(dt);
                _refRot = Quaternion.Slerp(_refRot, rot, w);
                _refPos = Vector3.Lerp(_refPos, pos, w);
            }
        } else {
            _stableAccumMs = 0f;
            _hasRef = false;
        }

        var refData = new ReferencePoseData(
            ts, _refRot, _refPos,
            _emaRotVel, _emaPosVel,
            isStable, _stableAccumMs
        );
        Record(refData);

        // Mirror absolute for consumers that read from this wrapper
        Record(new AbsoluteRotationData(ts, rot));
        Record(new AbsolutePositionData(ts, pos));

        _prevTs = ts;
        _prevRot = rot;
        _prevPos = pos;
    }

    public bool TryGetFreshReference(out ReferencePoseData data){
        if (!TryGetLatestData<ReferencePoseData>(out data)) return false;
        float ageMs = (float)(DateTime.UtcNow - data.Timestamp).TotalMilliseconds;
        return ageMs <= maxReferenceAgeMs;
    }
}


