using UnityEngine;

[DisallowMultipleComponent]
public class ARCamMotionObtain : MotionObtainBase {
    [Header("Source Camera (optional, defaults to Camera.main)")]
    [SerializeField] private Transform cameraTransform;

    private bool _hasPrev;
    private Quaternion _prevRotation = Quaternion.identity;
    private Quaternion _lastFrameRotationDelta = Quaternion.identity;

    private Vector3 _prevPosition = Vector3.zero;
    private Vector3 _lastFramePositionDelta = Vector3.zero;

    public override bool RotationEnabled => true;
    public override Quaternion AbsoluteQuat => cameraTransform != null ? cameraTransform.rotation : Quaternion.identity;
    public override Quaternion LastQuatDif => _lastFrameRotationDelta;

    public override bool PositionEnabled => true;
    public override Vector3 AbsolutePosition => cameraTransform != null ? cameraTransform.position : Vector3.zero;
    public override Vector3 LastPositionDif => _lastFramePositionDelta;

    private void Awake() {
        if (cameraTransform == null) {
            var mainCam = Camera.main;
            if (mainCam != null) {
                cameraTransform = mainCam.transform;
            } else {
                var anyCam = Object.FindFirstObjectByType<Camera>();
                if (anyCam != null) cameraTransform = anyCam.transform;
            }
        }
    }

    private void OnEnable() {
        _hasPrev = false;
        _lastFrameRotationDelta = Quaternion.identity;
        _lastFramePositionDelta = Vector3.zero;
    }

    private void Update() {
        if (cameraTransform == null) return;

        var currRot = cameraTransform.rotation;
        var currPos = cameraTransform.position;

        if (!_hasPrev) {
            _prevRotation = currRot;
            _prevPosition = currPos;
            _lastFrameRotationDelta = Quaternion.identity;
            _lastFramePositionDelta = Vector3.zero;
            _hasPrev = true;
            return;
        }

        _lastFrameRotationDelta = Quaternion.Inverse(_prevRotation) * currRot;
        _lastFramePositionDelta = currPos - _prevPosition;

        _prevRotation = currRot;
        _prevPosition = currPos;
    }

    public void ResetOriginToCurrent() {
        if (cameraTransform == null) return;
        _prevRotation = cameraTransform.rotation;
        _prevPosition = cameraTransform.position;
        _lastFrameRotationDelta = Quaternion.identity;
        _lastFramePositionDelta = Vector3.zero;
        _hasPrev = true;
    }
}


