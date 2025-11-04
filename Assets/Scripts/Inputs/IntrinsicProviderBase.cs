using System;
using UnityEngine;

public abstract class IntrinsicProviderBase : IntrinsicProvider {
    [Header("Projection Matrix")]
    [SerializeField] private float nearMeters = 0.03f;
    [SerializeField] private float farMeters = 20f;

    // Computed properties using IntrinsicParam
    public float FxN => GetIntrinsics().FxN;
    public float FyN => GetIntrinsics().FyN;
    public float CxN => GetIntrinsics().CxN;
    public float CyN => GetIntrinsics().CyN;
    public Vector2 Resolution => GetIntrinsics().Resolution;
    
    public Matrix4x4 ProjectionMatrix {
        get {
            var intrinsics = GetIntrinsics();
            if (!intrinsics.isValid) throw new InvalidOperationException("IntrinsicProviderBase: intrinsics not valid");
            if (intrinsics.width <= 0 || intrinsics.height <= 0) throw new InvalidOperationException("IntrinsicProviderBase: invalid resolution");
            if (nearMeters <= 0f || farMeters <= nearMeters) throw new InvalidOperationException("IntrinsicProviderBase: invalid near/far");

            float W = (float)intrinsics.width;
            float H = (float)intrinsics.height;
            float n = nearMeters;
            float f = farMeters;

            // D3D-style projection (Unity clip space z in [0,1]) from intrinsics
            Matrix4x4 P = new Matrix4x4();
            P[0,0] = 2f * intrinsics.fxPx / W;
            P[0,1] = 0f;
            // P[0,2] = 1f - (2f * intrinsics.cxPx / W);
            P[0,2] = (2f * intrinsics.cxPx / W) - 1f;
            P[0,3] = 0f;

            P[1,0] = 0f;
            P[1,1] = 2f * intrinsics.fyPx / H;
            P[1,2] = (2f * intrinsics.cyPx / H) - 1f;
            P[1,3] = 0f;

            P[2,0] = 0f;
            P[2,1] = 0f;
            P[2,2] = f / (n - f);
            P[2,3] = (f * n) / (n - f);

            P[3,0] = 0f;
            P[3,1] = 0f;
            P[3,2] = -1f;
            P[3,3] = 0f;

            return P;
        }
    }
}
