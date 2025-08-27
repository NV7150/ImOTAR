using UnityEngine;

public enum ScheduleStatus{
    HIGH_SPEED,
    LOW_SPEED,
    STOP
}

public abstract class SchedulerBase : MonoBehaviour {
    public abstract ScheduleStatus CurrentState { get; }
}