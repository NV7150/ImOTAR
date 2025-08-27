using UnityEngine;
using Unity.Collections;

public abstract class MotionFeatureExtractorBase : MonoBehaviour {
    public abstract NativeArray<float> ExtractFeature(MotionObtain motion);
}
