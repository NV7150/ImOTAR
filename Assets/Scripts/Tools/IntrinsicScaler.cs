using UnityEngine;

/// <summary>
/// カメラ内部パラメータのスケーリングツール
/// RGBスケールのIntrinsicParamを任意の出力解像度に正規化
/// </summary>
public static class IntrinsicScaler {
    /// <summary>
    /// IntrinsicParamを指定された出力解像度にスケーリング
    /// </summary>
    /// <param name="intrinsics">元の内部パラメータ（RGBスケール）</param>
    /// <param name="outputWidth">出力解像度の幅</param>
    /// <param name="outputHeight">出力解像度の高さ</param>
    /// <returns>スケーリングされた内部パラメータ</returns>
    public static IntrinsicParam ScaleToOutput(IntrinsicParam intrinsics, int outputWidth, int outputHeight) {
        if (!intrinsics.isValid) {
            return new IntrinsicParam(0, 0, 0, 0, outputWidth, outputHeight, false);
        }

        // スケールファクターを計算
        float scaleX = (float)outputWidth / intrinsics.width;
        float scaleY = (float)outputHeight / intrinsics.height;

        // 内部パラメータをスケーリング
        float fxScaled = intrinsics.fxPx * scaleX;
        float fyScaled = intrinsics.fyPx * scaleY;
        float cxScaled = intrinsics.cxPx * scaleX;
        float cyScaled = intrinsics.cyPx * scaleY;

        return new IntrinsicParam(fxScaled, fyScaled, cxScaled, cyScaled, outputWidth, outputHeight, true);
    }
    public static Matrix4x4 BuildProjectionMatrix(
        IntrinsicParam intrinsics, int outputWidth, int outputHeight,
        float nearMeters, float farMeters
    ) {
        if (!intrinsics.isValid) {
            throw new System.InvalidOperationException("IntrinsicScaler: intrinsics not valid");
        }
        if (outputWidth <= 0 || outputHeight <= 0) {
            throw new System.InvalidOperationException("IntrinsicScaler: invalid output size");
        }
        if (nearMeters <= 0f || farMeters <= nearMeters) {
            throw new System.InvalidOperationException("IntrinsicScaler: invalid near/far");
        }

        var scaledIntrinsics = ScaleToOutput(intrinsics, outputWidth, outputHeight);

        float W = (float)outputWidth;
        float H = (float)outputHeight;
        float n = nearMeters;
        float f = farMeters;

        Matrix4x4 P = Matrix4x4.zero;

        // X
        P[0,0] =  2f * scaledIntrinsics.fxPx / W;
        P[0,1] =  0f;
        P[0,2] =  1f - (2f * scaledIntrinsics.cxPx / W);
        P[0,3] =  0f;

        // Y
        P[1,0] =  0f;
        P[1,1] =  2f * scaledIntrinsics.fyPx / H;
        P[1,2] =  (2f * scaledIntrinsics.cyPx / H) - 1f;
        P[1,3] =  0f;

        // Z （非reversed-Z基準）
        P[2,0] =  0f;
        P[2,1] =  0f;
        P[2,2] =  f / (f - n);
        P[2,3] = (-f * n) / (f - n);

        // W
        P[3,0] =  0f;
        P[3,1] =  0f;
        P[3,2] =  1f;   // ← ここを +1 に修正（D3D/Metalスタイル）
        P[3,3] =  0f;

        // 最後にGPU空間に合わせる
        return GL.GetGPUProjectionMatrix(P, true);
    }

}
