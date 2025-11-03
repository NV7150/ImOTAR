using System;
using UnityEngine;

[DisallowMultipleComponent]
public class TutorialDepthInitializer : MonoBehaviour {
    [SerializeField] private ExperimentPhaseManager phaseManager;
    [SerializeField] private RenderTexture targetRT;
    [SerializeField] private Material clearMaterial;

    private void OnEnable(){
        if (phaseManager == null) throw new NullReferenceException("TutorialDepthInitializer: phaseManager not assigned");
        if (targetRT == null) throw new NullReferenceException("TutorialDepthInitializer: targetRT not assigned");
        if (clearMaterial == null) throw new NullReferenceException("TutorialDepthInitializer: clearMaterial not assigned");

        phaseManager.OnPhaseChanged += OnPhaseChanged;
    }

    private void OnDisable(){
        if (phaseManager != null){
            phaseManager.OnPhaseChanged -= OnPhaseChanged;
        }
    }

    private void OnPhaseChanged(ExperimentPhase newPhase){
        if (newPhase == ExperimentPhase.TUTORIAL){
            Graphics.Blit(null, targetRT, clearMaterial);
        }
    }
}

