using System;
using UnityEngine;
using dang0.ServerLog;

[DisallowMultipleComponent]
public sealed class MethodOrderSender : MonoBehaviour {
    [SerializeField] private ExperimentPhaseManager phaseManager;
    [SerializeField] private ExperimentStarter starter;
    [SerializeField] private Sender sender;

    private bool hasSent;

    private void OnEnable(){
        if (phaseManager == null) throw new NullReferenceException("MethodOrderSender: phaseManager not assigned");
        if (starter == null) throw new NullReferenceException("MethodOrderSender: starter not assigned");
        if (sender == null) throw new NullReferenceException("MethodOrderSender: sender not assigned");

        phaseManager.OnPhaseChanged += OnPhaseChanged;
    }

    private void OnDisable(){
        if (phaseManager != null){
            phaseManager.OnPhaseChanged -= OnPhaseChanged;
        }
    }

    private void OnPhaseChanged(ExperimentPhase newPhase){
        if (hasSent) return;
        if (newPhase != ExperimentPhase.END) return;

        var methods = starter.RandomizedMethods;
        if (methods == null) throw new InvalidOperationException("MethodOrderSender: RandomizedMethods not initialized");
        if (methods.Count == 0) throw new InvalidOperationException("MethodOrderSender: RandomizedMethods empty");

        var subjectId = starter.SubjectId;
        if (string.IsNullOrWhiteSpace(subjectId)) throw new InvalidOperationException("MethodOrderSender: SubjectId not set");

        var experimentId = starter.ExperimentId;
        if (string.IsNullOrWhiteSpace(experimentId)) throw new InvalidOperationException("MethodOrderSender: ExperimentId not set");

        var payload = new MethodOrderPayload(methods);
        var payloadJson = payload.ToJson();
        var wrapper = new Payload(DateTime.Now, subjectId, experimentId, payloadJson);
        sender.Send(wrapper);
        hasSent = true;
    }
}






