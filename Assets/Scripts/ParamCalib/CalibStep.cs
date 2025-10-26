using UnityEngine;

public abstract class CalibStep : MonoBehaviour {
    public abstract string StepMessage{ get; }
    public abstract void StartCalib();
    public abstract void RecordAndEnd(ICalibSuite recorder);
}