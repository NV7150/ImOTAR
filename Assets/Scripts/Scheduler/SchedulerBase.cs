using System;

public abstract class SchedulerBase: Scheduler {
    private Guid _updateReqId = Guid.Empty;
    public override Guid UpdateReqId => _updateReqId;

    public override void RequestUpdate(Guid id) {
        _updateReqId = id;
    }
}