using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

[DisallowMultipleComponent]
public class ARCameraIntrinsicProvider : IntrinsicProviderBase {
    [Header("AR Camera")]
    [SerializeField] private ARCameraManager arCameraManager;

    public override IntrinsicParam GetIntrinsics() {
        return new IntrinsicParam(_fxPx, _fyPx, _cxPx, _cyPx, _width, _height, _hasIntrinsics);
    }

    private bool _hasIntrinsics;
    private float _fxPx, _fyPx, _cxPx, _cyPx;
    private int _width, _height;

    private void OnEnable(){
        if (arCameraManager == null) throw new System.NullReferenceException("ARCameraIntrinsicProvider: arCameraManager not assigned");
        StartCoroutine(WaitForIntrinsics());
    }

    private void OnDisable(){
        StopAllCoroutines();
    }

    private System.Collections.IEnumerator WaitForIntrinsics(){
        while (!_hasIntrinsics){
            TryInitIntrinsics();
            if (_hasIntrinsics) break;
            yield return null;
        }
    }

    private void TryInitIntrinsics(){
        if (_hasIntrinsics) return;
        if (arCameraManager != null && arCameraManager.TryGetIntrinsics(out XRCameraIntrinsics intr)){
            var res = intr.resolution;
            if (res.x > 0 && res.y > 0){
                _width = res.x;
                _height = res.y;
                _fxPx = intr.focalLength.x;
                _fyPx = intr.focalLength.y;
                _cxPx = intr.principalPoint.x;
                _cyPx = intr.principalPoint.y;
                _hasIntrinsics = true;
            }
        }
    }
}
