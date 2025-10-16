using System;
using Unity.Collections;
using UnityEngine;

public class AngularVelocityNormExtractor : MotionFeatureExtractorBase {
    private NativeArray<float> _features;
    [Header("Normalization (rad/s)")]
    [SerializeField] private float refAngularSpeed = 6.283f; // ~360 deg/s
    [SerializeField] private float deadzone = 0.05f; // rad/s

    private void OnEnable(){
        if (!_features.IsCreated) _features = new NativeArray<float>(1, Allocator.Persistent);
    }

    private void OnDisable(){
        if (_features.IsCreated) _features.Dispose();
    }

    public override NativeArray<float> ExtractFeature(MotionObtain motion){
        if (!_features.IsCreated) _features = new NativeArray<float>(1, Allocator.Persistent);

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

        float mag = w.magnitude;
        float y = 0f;
        if (mag >= deadzone && refAngularSpeed > 1e-6f){
            float r = mag / refAngularSpeed;
            y = Mathf.Min(r * r, 1f); // square for non-negativity, clamp to [0,1]
        }
        _features[0] = y;
        return _features;
    }
}


