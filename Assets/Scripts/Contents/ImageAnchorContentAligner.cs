using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class ImageAnchorContentAligner : MonoBehaviour
{
    [SerializeField] private ARTrackedImageManager trackedImageManager;
    [SerializeField] private Transform contentRoot;        // 180度展開するコンテンツの親
    [SerializeField] private string targetImageName;       // 対象マーカ名 (空なら最初に見つかった一枚を使う)
    [SerializeField] private float yOffset;                // マーカーローカル+Z方向へのオフセット
    [SerializeField] private float smoothingSpeed = 8f;

    private ARAnchor currentAnchor;
    private bool isAligned;
    private TrackableId alignedImageId = TrackableId.invalidId;

    private void OnEnable()
    {
        if (trackedImageManager != null)
        {
            trackedImageManager.trackablesChanged.AddListener(OnTrackablesChanged);
        }
    }

    private void OnDisable()
    {
        if (trackedImageManager != null)
        {
            trackedImageManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
        }
    }

    private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> args)
    {
        if (contentRoot == null)
            return;

        // 追加と更新の両方で位置合わせを試す
        foreach (var img in args.added)
        {
            TryAlignToImage(img);
        }

        foreach (var img in args.updated)
        {
            TryAlignToImage(img);
        }

    }

    private void TryAlignToImage(ARTrackedImage image)
    {
        // 対象画像名のフィルタ
        if (!string.IsNullOrEmpty(targetImageName) &&
            image.referenceImage.name != targetImageName)
        {
            return;
        }

        if (isAligned && alignedImageId != TrackableId.invalidId && image.trackableId != alignedImageId)
        {
            return;
        }

        // きちんとトラッキングできている時だけ使う
        if (image.trackingState != TrackingState.Tracking)
        {
            if (alignedImageId == image.trackableId)
            {
                isAligned = false;
                alignedImageId = TrackableId.invalidId;
            }
            return;
        }

        // アンカー用オブジェクトを生成または再利用
        if (currentAnchor == null)
        {
            var anchorObject = new GameObject("ContentAnchor");
            currentAnchor = anchorObject.AddComponent<ARAnchor>();
        }

        Vector3 targetPosition = image.transform.position;
        Quaternion adjustedRotation = image.transform.rotation * Quaternion.Euler(90f, 0f, 0f);

        if (!isAligned)
        {
            currentAnchor.transform.SetPositionAndRotation(targetPosition, adjustedRotation);
        }
        else
        {
            float lerpFactor = smoothingSpeed <= 0f ? 1f : 1f - Mathf.Exp(-smoothingSpeed * Time.deltaTime);
            Vector3 smoothedPosition = Vector3.Lerp(currentAnchor.transform.position, targetPosition, lerpFactor);
            Quaternion smoothedRotation = Quaternion.Slerp(currentAnchor.transform.rotation, adjustedRotation, lerpFactor);
            currentAnchor.transform.SetPositionAndRotation(smoothedPosition, smoothedRotation);
        }

        float distanceToTarget = Vector3.Distance(currentAnchor.transform.position, targetPosition);
        float angleToTarget = Quaternion.Angle(currentAnchor.transform.rotation, adjustedRotation);
        // Debug.Log($"[ImageAnchorContentAligner] name={image.referenceImage.name} distance={distanceToTarget:F4} angle={angleToTarget:F2} scale={contentRoot.lossyScale}");

        if (contentRoot.parent != currentAnchor.transform)
        {
            contentRoot.SetParent(currentAnchor.transform, worldPositionStays: false);
        }

        contentRoot.localPosition = new Vector3(0f, 0f, yOffset);
        alignedImageId = image.trackableId;
        isAligned = true;
    }
}
