using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class BaselineFixStep : CalibStep {
    [Header("Guide / Camera")]
    [SerializeField] private PoseDiffManager pose;
    [SerializeField] private Transform cameraTr;
    [SerializeField] private List<GameObject> guides;

    [Header("Message")]
    [SerializeField] private string stepMessage = "Stabilize target and press to set baseline";

    public override string StepMessage => stepMessage;

    public override string Id => "Reset";

    private bool _started;

    public override void StartCalib(){
        if (pose == null) throw new NullReferenceException("BaselineFixStep: pose not assigned");
        if (cameraTr == null){
            var main = Camera.main;
            if (main == null) throw new InvalidOperationException("BaselineFixStep: camera not assigned and Camera.main not found");
            cameraTr = main.transform;
        }

        guides.ForEach(o => o.SetActive(false));

        _started = true;
    }

    public override void RecordAndEnd(ICalibSuite recorder){
        if (!_started) 
        throw new InvalidOperationException("BaselineFixStep: StartCalib must be called before RecordAndEnd");

        guides.ForEach(o => o.SetActive(false));

        _started = false;
    }
}


