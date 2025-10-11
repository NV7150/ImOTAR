using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public abstract class StructureManager : MonoBehaviour {

    public abstract Guid Generation {get;}

    public event Action<PointCloud> OnReady;

    protected void InvokeReady(PointCloud splat){
        OnReady?.Invoke(splat);
    }
}


