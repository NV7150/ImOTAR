using System;
using UnityEngine;

[DisallowMultipleComponent]
public class ExperimentDepthResetter : MonoBehaviour {
    [SerializeField] private ExperimentPhaseManager phaseManager;
    [SerializeField] private RenderTexture targetRT;
    [SerializeField] private Material clearMaterial;

    private void OnEnable(){
        if (phaseManager == null) throw new NullReferenceException("ExperimentDepthResetter: phaseManager not assigned");
        if (targetRT == null) throw new NullReferenceException("ExperimentDepthResetter: targetRT not assigned");
        if (clearMaterial == null) throw new NullReferenceException("ExperimentDepthResetter: clearMaterial not assigned");

        phaseManager.OnPhaseChanged += OnPhaseChanged;
    }

    private void OnDisable(){
        if (phaseManager != null){
            phaseManager.OnPhaseChanged -= OnPhaseChanged;
        }
    }

    private void OnPhaseChanged(ExperimentPhase newPhase){
        if (newPhase == ExperimentPhase.TUTORIAL || newPhase == ExperimentPhase.INTERMIDIATE){
            Graphics.Blit(null, targetRT, clearMaterial);
        }
    }
}






