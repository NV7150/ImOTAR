using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class PseudoMotion : MotionObtain {
    [Header("Initial Pose")]
    [SerializeField] private Vector3 initialPosition = Vector3.zero;
    [SerializeField] private Vector3 initialEulerDeg = Vector3.zero; // yaw-pitch-roll order not enforced; Unity uses ZXY internally for Quaternion.Euler

    [Header("Pending Delta (Camera-Local)")]
    [SerializeField] private Vector3 deltaPositionPending = Vector3.zero;    // meters, camera-local axes
    [SerializeField] private Vector3 deltaEulerDegPending = Vector3.zero;    // degrees, camera-local yaw/pitch/roll
    [SerializeField] private bool autoClearAfterCommit = true;

    [Header("Debug")] 
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private string logPrefix = "[PseudoMotion]";

    private readonly List<AbsoluteRotationData> _rotHist = new List<AbsoluteRotationData>(128);
    private readonly List<AbsolutePositionData> _posHist = new List<AbsolutePositionData>(128);
    private Quaternion _currentRotation;
    private Vector3 _currentPosition;

    private void Awake(){
        var ts = DateTime.Now;
        _currentRotation = Quaternion.Euler(initialEulerDeg);
        _currentPosition = initialPosition;
        _rotHist.Add(new AbsoluteRotationData(ts, _currentRotation));
        _posHist.Add(new AbsolutePositionData(ts, _currentPosition));
    }

    public override bool TryGetLatestData<T>(out T data){
        if (typeof(T) == typeof(AbsoluteRotationData)){
            data = (T)(object)new AbsoluteRotationData(DateTime.Now, _currentRotation);
            return true;
        }
        if (typeof(T) == typeof(AbsolutePositionData)){
            data = (T)(object)new AbsolutePositionData(DateTime.Now, _currentPosition);
            return true;
        }
        data = default;
        return false;
    }

    public override int CopyHistory<T>(DateTime from, DateTime to, Span<T> dst){
        if (typeof(T) == typeof(AbsoluteRotationData)){
            int count = 0;
            for (int i = 0; i < _rotHist.Count && count < dst.Length; i++){
                var item = _rotHist[i];
                var ts = item.Timestamp;
                if (ts < from || ts > to) continue;
                dst[count++] = (T)(object)item;
            }
            return count;
        }
        if (typeof(T) == typeof(AbsolutePositionData)){
            int count = 0;
            for (int i = 0; i < _posHist.Count && count < dst.Length; i++){
                var item = _posHist[i];
                var ts = item.Timestamp;
                if (ts < from || ts > to) continue;
                dst[count++] = (T)(object)item;
            }
            return count;
        }
        return 0;
    }

    public override int CopyLastN<T>(int n, Span<T> dst){
        if (typeof(T) == typeof(AbsoluteRotationData)){
            int start = Math.Max(0, _rotHist.Count - n);
            int count = 0;
            for (int i = start; i < _rotHist.Count && count < dst.Length; i++) dst[count++] = (T)(object)_rotHist[i];
            return count;
        }
        if (typeof(T) == typeof(AbsolutePositionData)){
            int start = Math.Max(0, _posHist.Count - n);
            int count = 0;
            for (int i = start; i < _posHist.Count && count < dst.Length; i++) dst[count++] = (T)(object)_posHist[i];
            return count;
        }
        return 0;
    }


    // Inspector buttons
    [ContextMenu("Move +Z 0.1m")]
    public void MoveForward(){ ApplyDeltaPosition(new Vector3(0,0,0.1f)); }

    [ContextMenu("Move -Z 0.1m")]
    public void MoveBackward(){ ApplyDeltaPosition(new Vector3(0,0,-0.1f)); }

    [ContextMenu("Yaw +5deg")]
    public void YawPlus(){ ApplyDeltaEuler(new Vector3(0, 5f, 0)); }

    [ContextMenu("Yaw -5deg")]
    public void YawMinus(){ ApplyDeltaEuler(new Vector3(0, -5f, 0)); }

    public void ApplyDeltaPosition(Vector3 delta){
        var now = DateTime.Now;
        // camera-local translation: rotate local delta by current rotation
        _currentPosition = _currentPosition + (_currentRotation * delta);
        _posHist.Add(new AbsolutePositionData(now, _currentPosition));
        if (verboseLogging) Debug.Log($"{logPrefix} Pos -> {_currentPosition}");
    }

    public void ApplyDeltaEuler(Vector3 deltaEulerDeg){
        var now = DateTime.Now;
        // camera-local rotation: right-multiply by delta
        _currentRotation = _currentRotation * Quaternion.Euler(deltaEulerDeg);
        _rotHist.Add(new AbsoluteRotationData(now, _currentRotation));
        if (verboseLogging) Debug.Log($"{logPrefix} Rot -> {_currentRotation.eulerAngles}");
    }

    [ContextMenu("Commit Pending Delta")]
    public void CommitPending(){
        // Apply rotation first, then translation in the updated local frame
        if (deltaEulerDegPending != Vector3.zero){
            ApplyDeltaEuler(deltaEulerDegPending);
        }
        if (deltaPositionPending != Vector3.zero){
            ApplyDeltaPosition(deltaPositionPending);
        }
        if (autoClearAfterCommit){
            deltaEulerDegPending = Vector3.zero;
            deltaPositionPending = Vector3.zero;
        }
        if (verboseLogging) Debug.Log($"{logPrefix} Commit pending delta");
    }

    [ContextMenu("Reset Pose")] 
    public void ResetPose(){
        var ts = DateTime.Now;
        _currentRotation = Quaternion.Euler(initialEulerDeg);
        _currentPosition = initialPosition;
        _rotHist.Add(new AbsoluteRotationData(ts, _currentRotation));
        _posHist.Add(new AbsolutePositionData(ts, _currentPosition));
        if (verboseLogging) Debug.Log($"{logPrefix} Reset pose -> pos {_currentPosition}, euler {initialEulerDeg}");
    }
}


