using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public abstract class SplatManager : MonoBehaviour {

    public event Action<Splat> OnSplatReady;

    protected void InvokeReady(Splat splat){
        OnSplatReady?.Invoke(splat);
    }
}


