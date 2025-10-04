using UnityEngine;

public abstract class CalibStep : MonoBehaviour {
    public string StepMessage{get;}
    public abstract void StartCalib();
    public abstract void RecordAndEnd(ICalibSuite recorder);
}