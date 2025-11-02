using UnityEngine;

public abstract class ExpLogger : MonoBehaviour {
    public abstract void StartLogging(string subjectId, string experimentId);
    public abstract void SendPhase(ExperimentPhase phase);
    public abstract void SendAllPhases();
}

