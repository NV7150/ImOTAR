using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class MethodSwitcher : MonoBehaviour {
    [Serializable]
    public class PipelineEntry {
        public ExperimentMethod method;
        public GameObject pipeline;
    }

    [SerializeField] private List<PipelineEntry> pipelines;
    [SerializeField] private ExperimentPhaseManager phaseManager;

    private void OnEnable(){
        if (phaseManager == null) throw new NullReferenceException("MethodSwitcher: phaseManager not assigned");
        if (pipelines == null) throw new NullReferenceException("MethodSwitcher: pipelines not assigned");

        ValidatePipelines();
        phaseManager.OnPhaseChanged += OnPhaseChanged;
        phaseManager.OnMethodChanged += OnMethodChanged;
    }

    private void OnDisable(){
        if (phaseManager != null){
            phaseManager.OnPhaseChanged -= OnPhaseChanged;
            phaseManager.OnMethodChanged -= OnMethodChanged;
        }
    }

    private void ValidatePipelines(){
        foreach (var entry in pipelines){
            if (entry.method == ExperimentMethod.NONE){
                throw new ArgumentException($"MethodSwitcher: pipelines contains invalid method {entry.method}");
            }
        }
    }

    private void OnPhaseChanged(ExperimentPhase newPhase){
        if (newPhase != ExperimentPhase.EXPERIMENT){
            foreach (var entry in pipelines){
                if (entry.pipeline == null) throw new NullReferenceException("MethodSwitcher: pipeline GameObject is null");
                entry.pipeline.SetActive(false);
            }
        }
    }

    private void OnMethodChanged(ExperimentMethod newMethod){
        foreach (var entry in pipelines){
            if (entry.pipeline == null) throw new NullReferenceException("MethodSwitcher: pipeline GameObject is null");
            entry.pipeline.SetActive(entry.method == newMethod);
        }
    }
}

