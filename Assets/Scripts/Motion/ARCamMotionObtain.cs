using UnityEngine;
using Unity.XR.CoreUtils;

[DisallowMultipleComponent]
public class ARCamMotionObtain : MotionObtainBase {
    [Header("XROrigin and Camera (must be assigned)")]
    [SerializeField] private XROrigin origin;
    [SerializeField] private Transform cameraTransform;

    private bool _hasPrev;
    private Quaternion _prevRotation = Quaternion.identity;
    private Quaternion _lastFrameRotationDelta = Quaternion.identity;

    private Vector3 _prevPosition = Vector3.zero;
    private Vector3 _lastFramePositionDelta = Vector3.zero;
    private void Awake() {
        if (origin == null) throw new System.InvalidOperationException("XROrigin is not set.");
        if (cameraTransform == null) throw new System.InvalidOperationException("Camera transform is not set.");

        var camComponent = cameraTransform.GetComponent<Camera>();
        if (camComponent == null) throw new System.InvalidOperationException("cameraTransform does not have a Camera component.");
        if (origin.Camera == null) throw new System.InvalidOperationException("XROrigin.Camera is not set.");
        if (origin.Camera.transform != cameraTransform) throw new System.InvalidOperationException("cameraTransform must match XROrigin.Camera.transform.");
        if (origin.Origin == null) throw new System.InvalidOperationException("XROrigin.Origin is not set.");
    }

    private void OnEnable() {
        _hasPrev = false;
        _lastFrameRotationDelta = Quaternion.identity;
        _lastFramePositionDelta = Vector3.zero;
        ClearAllHistory();
    }

    private void Update() {
        // Validate required references
        if (origin == null) throw new System.InvalidOperationException("XROrigin is not set.");
        if (cameraTransform == null) throw new System.InvalidOperationException("Camera transform is not set.");
        if (origin.Camera == null) throw new System.InvalidOperationException("XROrigin.Camera is not set.");
        if (origin.Origin == null) throw new System.InvalidOperationException("XROrigin.Origin is not set.");
        if (origin.Camera.transform != cameraTransform) throw new System.InvalidOperationException("cameraTransform must match XROrigin.Camera.transform.");

        // Origin-space pose: position from XROrigin, rotation with origin rotation removed (all axes)
        var currRot = Quaternion.Inverse(origin.Origin.transform.rotation) * cameraTransform.rotation;
        var currPos = origin.CameraInOriginSpacePos;

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

        var now = System.DateTime.UtcNow;
        Record(new AbsoluteRotationData(now, currRot));
        Record(new RotationDeltaData(now, _lastFrameRotationDelta));
        Record(new AbsolutePositionData(now, currPos));
        Record(new PositionDeltaData(now, _lastFramePositionDelta));

        _prevRotation = currRot;
        _prevPosition = currPos;
    }

    public void ResetOriginToCurrent() {
        if (origin == null) throw new System.InvalidOperationException("XROrigin is not set.");
        if (cameraTransform == null) throw new System.InvalidOperationException("Camera transform is not set.");
        if (origin.Camera == null) throw new System.InvalidOperationException("XROrigin.Camera is not set.");
        if (origin.Origin == null) throw new System.InvalidOperationException("XROrigin.Origin is not set.");
        if (origin.Camera.transform != cameraTransform) throw new System.InvalidOperationException("cameraTransform must match XROrigin.Camera.transform.");

        var currRot = Quaternion.Inverse(origin.Origin.transform.rotation) * cameraTransform.rotation;
        var currPos = origin.CameraInOriginSpacePos;

        _prevRotation = currRot;
        _prevPosition = currPos;
        _lastFrameRotationDelta = Quaternion.identity;
        _lastFramePositionDelta = Vector3.zero;
        _hasPrev = true;
        var now = System.DateTime.UtcNow;
        Record(new AbsoluteRotationData(now, currRot));
        Record(new RotationDeltaData(now, Quaternion.identity));
    }
}


