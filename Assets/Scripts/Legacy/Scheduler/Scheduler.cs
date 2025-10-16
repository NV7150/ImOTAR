using System;
using UnityEngine;

public enum ScheduleStatus{
    HIGH_SPEED,
    LOW_SPEED,
    STOP
}

public abstract class Scheduler : MonoBehaviour {
    public abstract ScheduleStatus CurrentState { get; }

    public abstract Guid UpdateReqId { get; }

    public abstract void RequestUpdate(Guid id);
}