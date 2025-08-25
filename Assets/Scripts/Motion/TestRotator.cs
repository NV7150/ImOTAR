using UnityEngine;

[DisallowMultipleComponent]
public class TestRotator : FrameProvider {
    [Header("Inputs")]
    [SerializeField] private RenderTexture sourceMask; // R32_Float color-only

    [Header("Output (this FrameProvider)")]
    [SerializeField] private RenderTexture rotatedMask;

    [Header("Material (assign ImOTAR/ImuRotationProjective)")]
    [SerializeField] private Material rotationMaterial;

    [Header("Rotation (quaternion)")]
    [SerializeField] private Vector3 eulerDegrees; // used for full matrix

    [Header("Parameters")]
    [Tooltip("Normalized principal point for projective shader")] 
    [SerializeField] private Vector2 cxcy = new Vector2(0.5f, 0.5f);
    [Tooltip("Normalized focal length for projective shader")] 
    [SerializeField] private Vector2 fxfy = new Vector2(1.0f, 1.0f);

    private System.DateTime _timestamp;
    public override RenderTexture FrameTex => rotatedMask;
    public override System.DateTime TimeStamp => _timestamp;

    private void OnEnable(){
        if (sourceMask == null) throw new System.NullReferenceException("TestRotator: sourceMask not assigned");
        if (rotatedMask == null) throw new System.NullReferenceException("TestRotator: rotatedMask not assigned");
        if (rotationMaterial == null) throw new System.NullReferenceException("TestRotator: rotationMaterial not assigned");
        ValidateOutputRT();
        if (!IsInitTexture){
            OnFrameTexInitialized();
            IsInitTexture = true;
        }
    }

    private void ValidateOutputRT(){
        if (sourceMask == null) throw new System.NullReferenceException("TestRotator: sourceMask not assigned");
        if (rotatedMask == null) throw new System.NullReferenceException("TestRotator: rotatedMask not assigned");
        if (rotatedMask.width != sourceMask.width || rotatedMask.height != sourceMask.height)
            throw new System.InvalidOperationException("TestRotator: rotatedMask size must match sourceMask size");
    }

    private void LateUpdate(){
        if (!IsInitTexture) return;
        ValidateOutputRT();

        // Projective shader properties
        rotationMaterial.SetFloat("_Cx", cxcy.x);
        rotationMaterial.SetFloat("_Cy", cxcy.y);
        rotationMaterial.SetFloat("_Fx", fxfy.x);
        rotationMaterial.SetFloat("_Fy", fxfy.y);

        // Provide full rotation matrix
        var R = Matrix4x4.Rotate(Quaternion.Euler(eulerDegrees));
        rotationMaterial.SetMatrix("_R", R);

        rotationMaterial.SetTexture("_MainTex", sourceMask);
        Graphics.Blit(sourceMask, rotatedMask, rotationMaterial, 0);

        _timestamp = System.DateTime.Now;
        TickUp();
    }

    public RenderTexture Output => rotatedMask;
}


