using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class ImageAnchorContentAligner : MonoBehaviour
{
    [SerializeField] private ARTrackedImageManager trackedImageManager;
    [SerializeField] private Transform contentRoot;        // 180度展開するコンテンツの親
    [SerializeField] private string targetImageName;       // 対象マーカ名 (空なら最初に見つかった一枚を使う)
    [SerializeField] private float yOffset;                // マーカーローカル+Z方向へのオフセット

    private ARAnchor currentAnchor;
    private bool isAligned;

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
        if (isAligned || contentRoot == null)
            return;

        // 追加と更新の両方で位置合わせを試す
        foreach (var img in args.added)
        {
            TryAlignToImage(img);
            if (isAligned) return;
        }

        foreach (var img in args.updated)
        {
            TryAlignToImage(img);
            if (isAligned) return;
        }
    }

    private void TryAlignToImage(ARTrackedImage image)
    {
        if (isAligned)
            return;

        // 対象画像名のフィルタ
        if (!string.IsNullOrEmpty(targetImageName) &&
            image.referenceImage.name != targetImageName)
        {
            return;
        }

        // きちんとトラッキングできている時だけ使う
        if (image.trackingState != TrackingState.Tracking)
            return;

        // アンカー用オブジェクトを作成し，マーカ位置に配置
        var anchorObject = new GameObject("ContentAnchor");
        Quaternion adjustedRotation = image.transform.rotation * Quaternion.Euler(90f, 0f, 0f);
        anchorObject.transform.SetPositionAndRotation(image.transform.position, adjustedRotation);

        currentAnchor = anchorObject.AddComponent<ARAnchor>();

        // コンテンツ親をアンカーの子にして位置合わせ
        // contentRoot は原点周りに配置しておくことを想定
        contentRoot.SetParent(currentAnchor.transform, worldPositionStays: false);
        contentRoot.localPosition = new Vector3(0f, 0f, yOffset);

        isAligned = true;
    }
}
