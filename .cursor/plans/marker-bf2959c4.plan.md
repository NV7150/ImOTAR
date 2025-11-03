<!-- bf2959c4-46d5-4be4-8ffd-d5cd630c7fba b68c2918-2c30-4e4e-a9e5-1cf9dc15ee42 -->
# マーカーXY平面配置対応

## 変更内容

`ImageAnchorContentAligner.cs` の `TryAlignToImage` メソッドを修正：

1. **yOffsetパラメータ追加**

   - `[SerializeField] private float yOffset;` をフィールドに追加
   - マーカーローカル+Z方向へのオフセット量

2. **アンカーの回転調整**

   - マーカーがXZ平面（縦）に存在する前提
   - マーカーローカル-Y方向 → アンカーの+Z方向（forward）にマッピング
   - `Quaternion.Euler(90f, 0f, 0f)` をマーカー回転に乗算

3. **contentRootの位置調整**

   - `worldPositionStays: false` でアンカーの子に設定
   - `contentRoot.localPosition = new Vector3(0f, 0f, yOffset);` でオフセット適用
   - マーカー座標系基準で、マーカーローカル+Z方向に配置

## 変更箇所

```csharp
// 66-74行目を修正
var anchorObject = new GameObject("ContentAnchor");
Quaternion adjustedRotation = image.transform.rotation * Quaternion.Euler(90f, 0f, 0f);
anchorObject.transform.SetPositionAndRotation(image.transform.position, adjustedRotation);

currentAnchor = anchorObject.AddComponent<ARAnchor>();
contentRoot.SetParent(currentAnchor.transform, worldPositionStays: false);
contentRoot.localPosition = new Vector3(0f, 0f, yOffset);
```