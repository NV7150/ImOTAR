using System;
using UnityEngine;

public abstract class PoseDiffManager : MonoBehaviour {
    public abstract bool TryGetDiffFrom(Guid generation, out Vector3 pos, out Quaternion rot);
}


