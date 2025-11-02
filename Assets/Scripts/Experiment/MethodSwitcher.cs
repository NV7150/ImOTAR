using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class MethodSwitcher : MonoBehaviour {
    [Serializable]
    public class PipelineEntry {
        public ExperimentPhase phase;
        public GameObject pipeline;
    }

    [SerializeField] private List<PipelineEntry> pipelines;
    [SerializeField] private ExperimentPhaseManager phaseManager;

    private void OnEnable(){
        if (phaseManager == null) throw new NullReferenceException("MethodSwitcher: phaseManager not assigned");
        if (pipelines == null) throw new NullReferenceException("MethodSwitcher: pipelines not assigned");

        ValidatePipelines();
        phaseManager.OnPhaseChanged += OnPhaseChanged;
    }

    private void OnDisable(){
        if (phaseManager != null){
            phaseManager.OnPhaseChanged -= OnPhaseChanged;
        }
    }

    private void ValidatePipelines(){
        foreach (var entry in pipelines){
            if (entry.phase == ExperimentPhase.NOT_STARTED || entry.phase == ExperimentPhase.END){
                throw new ArgumentException($"MethodSwitcher: pipelines contains invalid phase {entry.phase}");
            }
        }
    }

    private void OnPhaseChanged(ExperimentPhase newPhase){
        foreach (var entry in pipelines){
            if (entry.pipeline == null) throw new NullReferenceException("MethodSwitcher: pipeline GameObject is null");
            entry.pipeline.SetActive(entry.phase == newPhase);
        }
    }
}

