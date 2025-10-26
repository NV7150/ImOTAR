using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class BaselineFixStep : CalibStep {
    [Header("Guide / Camera")]
    [SerializeField] private PoseDiffManager pose;
    [SerializeField] private Transform cameraTr;
    [SerializeField] private GameObject guide;

    [Header("Message")]
    [SerializeField] private string stepMessage = "Stabilize target and press to set baseline";

    public override string StepMessage => stepMessage;

    private bool _started;

    public override void StartCalib(){
        if (pose == null) throw new NullReferenceException("BaselineFixStep: pose not assigned");
        if (guide == null) throw new NullReferenceException("BaselineFixStep: guide not assigned");
        if (cameraTr == null){
            var main = Camera.main;
            if (main == null) throw new InvalidOperationException("BaselineFixStep: camera not assigned and Camera.main not found");
            cameraTr = main.transform;
        }

        guide.SetActive(false);

        _started = true;
    }

    public override void RecordAndEnd(ICalibSuite recorder){
        if (!_started) throw new InvalidOperationException("BaselineFixStep: StartCalib must be called before RecordAndEnd");

        if (guide.gameObject.activeSelf) guide.gameObject.SetActive(false);
        _started = false;
    }
}


