using UnityEngine;

public abstract class MotionObtainBase : MonoBehaviour {
    // Is this motion obtain can provide rotation
    public abstract bool RotationEnabled { get; }

    // Absolute quaternion, normaly from the initial head position that application started
    public abstract Quaternion AbsoluteQuat { get; }
    // Head rotation from last frame
    public abstract Quaternion LastQuatDif { get; }
    
    // Is this motion obtain can provide position
    public abstract bool PositionEnabled { get; }

    // Absolute position, normally from the initial head position that application started
    public abstract Vector3 AbsolutePosition { get; }
    // Head position delta from last frame
    public abstract Vector3 LastPositionDif { get; }

}