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

    // History is recorded via base.Record<T>()

    // Capabilities are now queried via TryGetLatestData<T>

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
        ClearAllHistory();
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

        var now = System.DateTime.UtcNow;
        Record(new AbsoluteRotationData(now, currRot));
        Record(new RotationDeltaData(now, _lastFrameRotationDelta));
        Record(new AbsolutePositionData(now, currPos));
        Record(new PositionDeltaData(now, _lastFramePositionDelta));

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
        Record(new AbsoluteRotationData(System.DateTime.UtcNow, cameraTransform.rotation));
        Record(new RotationDeltaData(System.DateTime.UtcNow, Quaternion.identity));
    }
}


