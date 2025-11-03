using UnityEngine;

public abstract class ExpLogger : MonoBehaviour {
    public abstract void StartLogging(string subjectId, string experimentId);
    public abstract void SendMethod(ExperimentMethod method);
    public abstract void SendAllMethods();
}

