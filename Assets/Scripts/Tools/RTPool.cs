using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// RenderTextureプール管理クラス
/// メモリ効率化のためRenderTextureを再利用する
/// </summary>
public class RTPool
{
    private Dictionary<string, Queue<RenderTexture>> _pools = new Dictionary<string, Queue<RenderTexture>>();
    private bool _isEnabled = true;
    private int _maxPoolSize = 4;

    /// <summary>
    /// プール機能の有効/無効を設定
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    /// <summary>
    /// プールの最大サイズを設定
    /// </summary>
    public int MaxPoolSize
    {
        get => _maxPoolSize;
        set => _maxPoolSize = Mathf.Max(1, value);
    }

    /// <summary>
    /// プールからRenderTextureを取得
    /// プールが無効または空の場合は新規作成
    /// </summary>
    public RenderTexture Get(int width, int height, RenderTextureFormat format)
    {
        if (!_isEnabled)
            return CreateNew(width, height, format);

        string key = GetPoolKey(width, height, format);
        
        if (!_pools.ContainsKey(key))
            _pools[key] = new Queue<RenderTexture>();

        var pool = _pools[key];
        if (pool.Count > 0)
        {
            var rt = pool.Dequeue();
            if (rt != null && rt.IsCreated())
            {
                // テクスチャをクリア
                ClearTexture(rt);
                return rt;
            }
        }

        return CreateNew(width, height, format);
    }

    /// <summary>
    /// RenderTextureをプールに返却
    /// プールが無効または満杯の場合は破棄
    /// </summary>
    public void Return(RenderTexture rt, int width, int height, RenderTextureFormat format)
    {
        if (!_isEnabled || rt == null)
        {
            DestroyTexture(rt);
            return;
        }

        string key = GetPoolKey(width, height, format);
        
        if (!_pools.ContainsKey(key))
            _pools[key] = new Queue<RenderTexture>();

        var pool = _pools[key];
        if (pool.Count < _maxPoolSize)
        {
            pool.Enqueue(rt);
        }
        else
        {
            DestroyTexture(rt);
        }
    }

    /// <summary>
    /// 全プールをクリア
    /// </summary>
    public void ClearAll()
    {
        foreach (var pool in _pools.Values)
        {
            while (pool.Count > 0)
            {
                var rt = pool.Dequeue();
                DestroyTexture(rt);
            }
        }
        _pools.Clear();
    }

    /// <summary>
    /// プール統計情報を取得
    /// </summary>
    public Dictionary<string, int> GetPoolStats()
    {
        var stats = new Dictionary<string, int>();
        foreach (var kvp in _pools)
        {
            stats[kvp.Key] = kvp.Value.Count;
        }
        return stats;
    }

    /// <summary>
    /// 新しいRenderTextureを作成
    /// </summary>
    private static RenderTexture CreateNew(int width, int height, RenderTextureFormat format)
    {
        var rt = new RenderTexture(width, height, 0, format);
        rt.Create();
        ClearTexture(rt);
        return rt;
    }

    /// <summary>
    /// RenderTextureをクリア
    /// </summary>
    private static void ClearTexture(RenderTexture rt)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = prev;
    }

    /// <summary>
    /// RenderTextureを破棄
    /// </summary>
    private static void DestroyTexture(RenderTexture rt)
    {
        if (rt != null)
        {
            rt.Release();
            Object.Destroy(rt);
        }
    }

    /// <summary>
    /// プールキーを生成
    /// </summary>
    private static string GetPoolKey(int width, int height, RenderTextureFormat format)
    {
        return $"{width}x{height}_{format}";
    }
}
