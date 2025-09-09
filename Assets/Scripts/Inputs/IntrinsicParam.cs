using UnityEngine;

/// <summary>
/// カメラ内部パラメータの構造体
/// RGBスケール（カメラの物理解像度）での値を保持
/// </summary>
[System.Serializable]
public struct IntrinsicParam {
    public float fxPx;  // 焦点距離 X (pixels)
    public float fyPx;  // 焦点距離 Y (pixels)
    public float cxPx;  // 主点 X (pixels)
    public float cyPx;  // 主点 Y (pixels)
    public int width;   // 解像度幅 (pixels)
    public int height;  // 解像度高さ (pixels)
    public bool isValid;

    public IntrinsicParam(float fx, float fy, float cx, float cy, int w, int h, bool valid) {
        fxPx = fx;
        fyPx = fy;
        cxPx = cx;
        cyPx = cy;
        width = w;
        height = h;
        isValid = valid;
    }

    /// <summary>
    /// 正規化焦点距離 X (fx / width)
    /// </summary>
    public float FxN => fxPx / width;

    /// <summary>
    /// 正規化焦点距離 Y (fy / height)
    /// </summary>
    public float FyN => fyPx / height;

    /// <summary>
    /// 正規化主点 X (cx / width)
    /// </summary>
    public float CxN => cxPx / width;

    /// <summary>
    /// 正規化主点 Y (cy / height)
    /// </summary>
    public float CyN => cyPx / height;

    /// <summary>
    /// 解像度をVector2で取得
    /// </summary>
    public Vector2 Resolution => new Vector2(width, height);
}
