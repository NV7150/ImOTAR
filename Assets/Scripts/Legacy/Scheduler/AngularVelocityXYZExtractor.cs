using System;
using Unity.Collections;
using UnityEngine;

public class AngularVelocityXYZExtractor : MotionFeatureExtractorBase {
    private NativeArray<float> _features;
    [Header("Normalization per-axis (rad/s)")]
    [SerializeField] private Vector3 refAngularSpeed = new Vector3(6f, 6f, 6f);
    [SerializeField] private float deadzone = 0.05f; // rad/s

    private void OnEnable(){
        if (!_features.IsCreated) _features = new NativeArray<float>(3, Allocator.Persistent);
    }

    private void OnDisable(){
        if (_features.IsCreated) _features.Dispose();
    }

    public override NativeArray<float> ExtractFeature(MotionObtain motion){
        if (!_features.IsCreated) _features = new NativeArray<float>(3, Allocator.Persistent);

        var tmp = new RotationDeltaData[2];
        var span = new Span<RotationDeltaData>(tmp);
        int got = motion.CopyLastN(span.Length, span);

        Vector3 w = Vector3.zero;
        if (got >= 2){
            var a = tmp[0];
            var b = tmp[1];
            float dt = (float)(b.Timestamp - a.Timestamp).TotalSeconds;
            if (dt > 1e-6f){
                b.Delta.ToAngleAxis(out float angleDeg, out Vector3 axis);
                if (!float.IsNaN(axis.x) && !float.IsNaN(axis.y) && !float.IsNaN(axis.z) && axis.sqrMagnitude > 0f){
                    float angleRad = angleDeg * Mathf.Deg2Rad;
                    w = axis.normalized * (angleRad / dt);
                }
            }
        }

        // Per-axis squared-normalization with deadzone and clamp [0,1]
        _features[0] = NormalizeAxis(w.x, refAngularSpeed.x);
        _features[1] = NormalizeAxis(w.y, refAngularSpeed.y);
        _features[2] = NormalizeAxis(w.z, refAngularSpeed.z);
        return _features;
    }

    private float NormalizeAxis(float v, float refV){
        float av = Mathf.Abs(v);
        if (av < deadzone || refV <= 1e-6f) return 0f;
        float r = av / refV;
        return Mathf.Min(r * r, 1f);
    }
}


