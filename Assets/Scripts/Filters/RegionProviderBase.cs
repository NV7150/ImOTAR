using UnityEngine;

public abstract class RegionProviderBase : RegionProvider {

    public override RenderTexture CurrentRegion => _tex;
    public override int Tick => _tick;

    private int _tick = 0;
    public const int TICK_MAX = 4098;

    private RenderTexture _tex;

    protected void TickUp(RenderTexture tex){
        _tick = (_tick + 1) % TICK_MAX;
        _tex = tex;
        InvokeTexUp(tex);
    }

}