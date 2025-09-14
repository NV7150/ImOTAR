using System;
using UnityEngine;

/// <summary>
/// Debug scheduler that always reports the inspector-selected state.
/// Stores the last requested update Guid for visibility.
/// </summary>
[AddComponentMenu("Debug/Pseudo Scheduler")]
public class PseudoScheduler : Scheduler {
    [Header("Debug State")]
    [SerializeField] private ScheduleStatus forcedState = ScheduleStatus.HIGH_SPEED;

    [Header("Diagnostics")]
    [SerializeField, Tooltip("Last update request id (read-only in runtime)")]
    private string lastUpdateRequestId = string.Empty;

    private Guid _lastRequestedId = Guid.Empty;

    public override ScheduleStatus CurrentState { get { return forcedState; } }

    public override Guid UpdateReqId { get { return _lastRequestedId; } }

    public override void RequestUpdate(Guid id) {
        _lastRequestedId = id;
        lastUpdateRequestId = id.ToString();
    }
}


