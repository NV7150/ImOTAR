using UnityEngine;

// Always fires the schedule by reporting LOW_SPEED every frame.
// Drop this on the same GameObject where you'd normally use a scheduler
// and disable/remove other schedulers to force continuous operation.
public class DebugScheduler : SchedulerBase
{
    [Header("Debug")]
    [SerializeField] private bool verboseLogs = false;

    public override ScheduleStatus CurrentState => ScheduleStatus.LOW_SPEED;

    private void Update()
    {
        if (verboseLogs)
        {
            // Keep logs lightweight to avoid spamming the console.
            // Toggle on only when needed.
            Debug.Log("[DebugScheduler] Forcing LOW_SPEED");
        }
    }
}
