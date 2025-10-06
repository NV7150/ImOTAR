using System;
using UnityEngine;

[DisallowMultipleComponent]
public class DieByMotionAxis : MonoBehaviour {
    public enum RotationProjection {
        Euler,
        RotationVector
    }

    [Header("Inputs")]
    [SerializeField] private PoseDiffManager poseDiff;
    [SerializeField] private StateManager state;
    [SerializeField] private SplatManager splat;

    [Header("Thresholds")]
    [SerializeField] private Vector3 rotDegThreshDeg = new Vector3(5f, 5f, 5f);
    [SerializeField] private Vector3 posThreshMeters = new Vector3(0.03f, 0.03f, 0.03f);

    [Header("Rotation Mode")]
    [SerializeField] private RotationProjection rotationProjection = RotationProjection.Euler;

    [Header("Debug")]
    [SerializeField] private bool logVerbose = false;
    [SerializeField] private string logPrefix = "[DieByMotionAxis]";

    public Vector3 RotDegThreshDeg => rotDegThreshDeg;
    public Vector3 PosThreshMeters => posThreshMeters;
    public RotationProjection RotProjection => rotationProjection;

    private void OnEnable(){
        if (poseDiff == null) throw new NullReferenceException("DieByMotionAxis: poseDiff not assigned");
        if (state == null) throw new NullReferenceException("DieByMotionAxis: state not assigned");
    }

    private void Update(){
        if (state.CurrState != State.ACTIVE) 
            return;
        var guid = splat.SplatGeneration;
        
        if (!poseDiff.TryGetDiffFrom(guid, out var trans, out var rot)) 
            return;

        // Evaluate rotation threshold per-axis
        Vector3 rotAxisValuesDeg = GetRotationAxisValuesDeg(rot);
        bool dieRot = (Mathf.Abs(rotAxisValuesDeg.x) >= rotDegThreshDeg.x)
                   || (Mathf.Abs(rotAxisValuesDeg.y) >= rotDegThreshDeg.y)
                   || (Mathf.Abs(rotAxisValuesDeg.z) >= rotDegThreshDeg.z);

        // Evaluate translation threshold per-axis (current-local axes)
        Vector3 t = trans;
        bool diePos = (Mathf.Abs(t.x) >= posThreshMeters.x)
                   || (Mathf.Abs(t.y) >= posThreshMeters.y)
                   || (Mathf.Abs(t.z) >= posThreshMeters.z);

        if (dieRot || diePos){
            if (logVerbose){
                Debug.Log($"{logPrefix} DIE: rot=({rotAxisValuesDeg.x:F2},{rotAxisValuesDeg.y:F2},{rotAxisValuesDeg.z:F2})deg thr=({rotDegThreshDeg.x:F2},{rotDegThreshDeg.y:F2},{rotDegThreshDeg.z:F2}) | pos=({t.x:F3},{t.y:F3},{t.z:F3})m thr=({posThreshMeters.x:F3},{posThreshMeters.y:F3},{posThreshMeters.z:F3})");
            }
            state.Discard();
        }
    }

    private Vector3 GetRotationAxisValuesDeg(Quaternion q){
        switch (rotationProjection){
            case RotationProjection.Euler:
                {
                    Vector3 euler = q.eulerAngles; // 0..360
                    // Normalize to [-180,180]
                    euler.x = Normalize180(euler.x);
                    euler.y = Normalize180(euler.y);
                    euler.z = Normalize180(euler.z);
                    return euler;
                }
            case RotationProjection.RotationVector:
                {
                    q.ToAngleAxis(out float angDeg, out Vector3 axis);
                    if (float.IsNaN(axis.x) || float.IsNaN(axis.y) || float.IsNaN(axis.z))
                        throw new InvalidOperationException("DieByMotionAxis: invalid quaternion axis");
                    // Ensure unit axis for safety
                    if (axis == Vector3.zero)
                        return Vector3.zero;
                    axis.Normalize();
                    Vector3 rDeg = axis * angDeg;
                    return rDeg;
                }
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static float Normalize180(float deg){
        float d = Mathf.Repeat(deg + 180f, 360f) - 180f;
        return d;
    }

    // Public setters (external calibration can update after creation)
    public void SetRotationProjection(RotationProjection mode){
        rotationProjection = mode;
    }

    public void SetRotThreshEulerDeg(Vector3 eulerDeg){
        rotDegThreshDeg = new Vector3(Mathf.Abs(eulerDeg.x), Mathf.Abs(eulerDeg.y), Mathf.Abs(eulerDeg.z));
    }

    public void SetRotThreshQuaternion(Quaternion q){
        if (rotationProjection == RotationProjection.Euler){
            Vector3 e = q.eulerAngles;
            e.x = Mathf.Abs(Normalize180(e.x));
            e.y = Mathf.Abs(Normalize180(e.y));
            e.z = Mathf.Abs(Normalize180(e.z));
            rotDegThreshDeg = e;
            return;
        }
        if (rotationProjection == RotationProjection.RotationVector){
            q.ToAngleAxis(out float angDeg, out Vector3 axis);
            if (axis == Vector3.zero){
                rotDegThreshDeg = Vector3.zero;
                return;
            }
            axis.Normalize();
            Vector3 rDeg = axis * Mathf.Abs(angDeg);
            rotDegThreshDeg = new Vector3(Mathf.Abs(rDeg.x), Mathf.Abs(rDeg.y), Mathf.Abs(rDeg.z));
            return;
        }
        throw new ArgumentOutOfRangeException();
    }

    public void SetPosThreshMeters(Vector3 meters){
        posThreshMeters = new Vector3(Mathf.Abs(meters.x), Mathf.Abs(meters.y), Mathf.Abs(meters.z));
    }
}


