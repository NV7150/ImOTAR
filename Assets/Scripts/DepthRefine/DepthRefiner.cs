    using UnityEngine;

    public abstract class DepthRefiner : MonoBehaviour {
        public abstract RenderTexture Refine(RenderTexture tex);
    }