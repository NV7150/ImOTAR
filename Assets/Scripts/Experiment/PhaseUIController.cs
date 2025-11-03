using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class PhaseUIController : MonoBehaviour {
    [Serializable]
    public class PhaseUIEntry {
        public ExperimentPhase phase;
        public List<GameObject> visibleUI;
    }

    [SerializeField] private ExperimentPhaseManager phaseManager;
    [SerializeField] private List<PhaseUIEntry> phaseUIEntries;

    private HashSet<GameObject> allManagedUI;

    private void OnEnable(){
        if (phaseManager == null) throw new NullReferenceException("PhaseUIController: phaseManager not assigned");
        if (phaseUIEntries == null) throw new NullReferenceException("PhaseUIController: phaseUIEntries not assigned");

        CollectManagedUI();
        phaseManager.OnPhaseChanged += OnPhaseChanged;
        UpdateUIVisibility(phaseManager.CurrPhase);
    }

    private void OnDisable(){
        if (phaseManager != null){
            phaseManager.OnPhaseChanged -= OnPhaseChanged;
        }
    }

    private void CollectManagedUI(){
        allManagedUI = new HashSet<GameObject>();
        foreach (var entry in phaseUIEntries){
            if (entry.visibleUI == null) continue;
            foreach (var ui in entry.visibleUI){
                if (ui != null){
                    allManagedUI.Add(ui);
                }
            }
        }
    }

    private void OnPhaseChanged(ExperimentPhase newPhase){
        UpdateUIVisibility(newPhase);
    }

    private void UpdateUIVisibility(ExperimentPhase currentPhase){
        HashSet<GameObject> currentVisibleUI = new HashSet<GameObject>();

        foreach (var entry in phaseUIEntries){
            if (entry.phase == currentPhase && entry.visibleUI != null){
                foreach (var ui in entry.visibleUI){
                    if (ui != null){
                        currentVisibleUI.Add(ui);
                    }
                }
            }
        }

        foreach (var ui in allManagedUI){
            if (ui != null){
                ui.SetActive(currentVisibleUI.Contains(ui));
            }
        }
    }
}

