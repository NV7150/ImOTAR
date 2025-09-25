using System;
using UnityEngine;

public abstract class PoseDiffManager : MonoBehaviour {
    // Interface-only: no fields/logic here
    public abstract Guid Generation { get; }
    public abstract DateTime BaselineTimestamp { get; }
    public abstract Vector3 Translation { get; }
    public abstract Quaternion Rotation { get; }
}


