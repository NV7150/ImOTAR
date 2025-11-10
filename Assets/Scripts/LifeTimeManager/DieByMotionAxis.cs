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
    [SerializeField] private StructureManager splat;

    [Header("Thresholds")]
    [SerializeField] private Vector3 rotDegThreshDeg = new Vector3(5f, 5f, 5f);
    [SerializeField] private Vector3 posThreshMeters = new Vector3(0.03f, 0.03f, 0.03f);
    [Tooltip("Pitch + direction threshold (deg). Euler mode only.")]
    [SerializeField] private float rotPitchPosThreshDeg = 5f;
    [Tooltip("Pitch - direction threshold (deg). Euler mode only.")]
    [SerializeField] private float rotPitchNegThreshDeg = 5f;
    [Tooltip("Z + direction translation threshold (m).")]
    [SerializeField] private float posZPosThreshMeters = 0.03f;
    [Tooltip("Z - direction translation threshold (m).")]
    [SerializeField] private float posZNegThreshMeters = 0.03f;

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
        var guid = splat.Generation;
        
        if (!poseDiff.TryGetDiffFrom(guid, out var trans, out var rot)) 
            return;
        bool dieRot = false;
        bool diePos = false;

        // Rotation evaluation
        if (rotationProjection == RotationProjection.Euler){
            Vector3 e = GetRotationAxisValuesDeg(rot); // normalized Euler
            float pitch = e.x;
            bool dieRotPitchPos = (pitch > 0f) && (pitch >= rotPitchPosThreshDeg);
            bool dieRotPitchNeg = (pitch < 0f) && (Mathf.Abs(pitch) >= rotPitchNegThreshDeg);
            bool dieRotOther = (Mathf.Abs(e.y) >= rotDegThreshDeg.y) || (Mathf.Abs(e.z) >= rotDegThreshDeg.z);
            dieRot = dieRotPitchPos || dieRotPitchNeg || dieRotOther;
        } else {
            // RotationVector mode retains original absolute axis threshold logic
            Vector3 rv = GetRotationAxisValuesDeg(rot);
            dieRot = (Mathf.Abs(rv.x) >= rotDegThreshDeg.x)
                  || (Mathf.Abs(rv.y) >= rotDegThreshDeg.y)
                  || (Mathf.Abs(rv.z) >= rotDegThreshDeg.z);
        }

        // Translation evaluation
        Vector3 t = trans;
        bool diePosZPos = t.z >= posZPosThreshMeters;
        bool diePosZNeg = t.z <= -posZNegThreshMeters;
        bool diePosXY = (Mathf.Abs(t.x) >= posThreshMeters.x) || (Mathf.Abs(t.y) >= posThreshMeters.y);
        diePos = diePosZPos || diePosZNeg || diePosXY;

        if (dieRot || diePos){
            if (logVerbose){
                if (rotationProjection == RotationProjection.Euler){
                    Vector3 e = rot.eulerAngles;
                    e.x = Normalize180(e.x);
                    e.y = Normalize180(e.y);
                    e.z = Normalize180(e.z);
                    Debug.Log($"{logPrefix} DIE: pitch={e.x:F2}deg thr(+{rotPitchPosThreshDeg:F2}/-{rotPitchNegThreshDeg:F2}) | rotY={e.y:F2}/{rotDegThreshDeg.y:F2} rotZ={e.z:F2}/{rotDegThreshDeg.z:F2} | posXY=({t.x:F3},{t.y:F3}) thr=({posThreshMeters.x:F3},{posThreshMeters.y:F3}) | posZ={t.z:F3}m thr(+{posZPosThreshMeters:F3}/-{posZNegThreshMeters:F3})");
                } else {
                    Vector3 rv = GetRotationAxisValuesDeg(rot);
                    Debug.Log($"{logPrefix} DIE: rotRV=({rv.x:F2},{rv.y:F2},{rv.z:F2})deg thr=({rotDegThreshDeg.x:F2},{rotDegThreshDeg.y:F2},{rotDegThreshDeg.z:F2}) | pos=({t.x:F3},{t.y:F3},{t.z:F3})m thrXY=({posThreshMeters.x:F3},{posThreshMeters.y:F3}) thrZ(+{posZPosThreshMeters:F3}/-{posZNegThreshMeters:F3})");
                }
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
        // X component maps to pitch positive/negative thresholds (both same) for backward compatibility
        float pitchAbs = Mathf.Abs(Normalize180(eulerDeg.x));
        rotPitchPosThreshDeg = pitchAbs;
        rotPitchNegThreshDeg = pitchAbs;
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
        posZPosThreshMeters = Mathf.Abs(meters.z);
        posZNegThreshMeters = Mathf.Abs(meters.z);
        posThreshMeters = new Vector3(Mathf.Abs(meters.x), Mathf.Abs(meters.y), Mathf.Abs(meters.z));
    }

    // Pitch specific setter (Euler mode only logical usage)
    public void SetRotPitchThreshDeg(float posDeg, float negDeg){
        if (posDeg < 0f || negDeg < 0f) throw new ArgumentException("DieByMotionAxis: pitch thresholds must be >=0");
        rotPitchPosThreshDeg = posDeg;
        rotPitchNegThreshDeg = negDeg;
    }

    // Z translation specific setter
    public void SetPosZThreshMeters(float pos, float neg){
        if (pos < 0f || neg < 0f) throw new ArgumentException("DieByMotionAxis: Z thresholds must be >=0");
        posZPosThreshMeters = pos;
        posZNegThreshMeters = neg;
    }
}


