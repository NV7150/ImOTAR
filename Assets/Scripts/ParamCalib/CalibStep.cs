using UnityEngine;

public abstract class CalibStep : MonoBehaviour {
    public string StepMessage{get;}
    public void StartCalib();
    public void RecordAndEnd(ICalibSuite recorder);
}