using System;
using UnityEngine;

[DisallowMultipleComponent]
public class BirthByMotion : MonoBehaviour {
    [Header("Inputs")]
    [SerializeField] private MotionObtain motion;
    [SerializeField] private StateManager state;

    [Header("Stability")]
    [SerializeField, Min(1f)] private float stableTimeMs = 400f;
    [SerializeField, Min(0f)] private float rotVelStableDegPerSec = 2.0f;
    [SerializeField, Min(0f)] private float posVelStableMps = 0.01f;
    [SerializeField, Range(0f, 1f)] private float smoothFactor = 0.2f; // EMA smoothing for velocities

    [Header("Debug")]
    [SerializeField] private bool logVerbose = false;
    [SerializeField] private string logPrefix = "[BirthByMotion]";

    private bool _hasPrev;
    private DateTime _prevTs;
    private Quaternion _prevRot = Quaternion.identity;
    private Vector3 _prevPos = Vector3.zero;

    private float _emaRotVel;
    private float _emaPosVel;
    private float _stableAccumMs;

    // Debug getters (read-only)
    public float EmaRotVel => _emaRotVel;
    public float EmaPosVel => _emaPosVel;
    public float StableAccumMs => _stableAccumMs;
    public float StableTimeMs => stableTimeMs;
    public float RotVelStableDegPerSec => rotVelStableDegPerSec;
    public float PosVelStableMps => posVelStableMps;
    public bool IsStableNow => (_emaRotVel <= rotVelStableDegPerSec) && (_emaPosVel <= posVelStableMps);

    private void OnEnable(){
        if (motion == null) throw new NullReferenceException("BirthByMotion: motion not assigned");
        if (state  == null) throw new NullReferenceException("BirthByMotion: state not assigned");
        _hasPrev = false;
        _emaRotVel = 0f;
        _emaPosVel = 0f;
        _stableAccumMs = 0f;
    }

    private void Update(){
        if (state.CurrState != State.INACTIVE) {
            // Only monitors for BIRTH when DEAD
            _hasPrev = false; // reset to avoid large deltas across state changes
            _stableAccumMs = 0f;
            return;
        }

        if (!motion.TryGetLatestData<AbsoluteRotationData>(out var rotData)) return;
        if (!motion.TryGetLatestData<AbsolutePositionData>(out var posData)) return;

        var ts = rotData.Timestamp;
        var rot = rotData.Rotation;
        var pos = posData.Position;

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

        // EMA smoothing
        float a = Mathf.Clamp01(smoothFactor);
        _emaRotVel = Mathf.Lerp(rotVel, _emaRotVel, 1f - a);
        _emaPosVel = Mathf.Lerp(posVel, _emaPosVel, 1f - a);

        bool rotStable = _emaRotVel <= rotVelStableDegPerSec;
        bool posStable = _emaPosVel <= posVelStableMps;
        bool stable = rotStable && posStable;

        if (stable){
            _stableAccumMs += dt * 1000f;
        } else {
            _stableAccumMs = 0f;
        }

        if (_stableAccumMs >= stableTimeMs){
            if (logVerbose) Debug.Log($"{logPrefix} Birth: rotVel={_emaRotVel:F3} deg/s, posVel={_emaPosVel:F4} m/s");
            state.Generate();
            // reset to avoid immediate re-triggering
            _stableAccumMs = 0f;
            _hasPrev = false;
        }

        _prevTs = ts;
        _prevRot = rot;
        _prevPos = pos;
    }
}


