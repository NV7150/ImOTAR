    using UnityEngine;

    public abstract class DepthRefiner : FrameProvider {
        [Header("Debugging")]
        [Tooltip("When enabled, the refiner will emit verbose Debug.Log messages for validation, allocation, and dispatch steps.")]
        [SerializeField] protected bool verboseLogs = false;

        public abstract RenderTexture Refine(RenderTexture tex);
    }