using System;
using UnityEngine;

namespace dang0.ServerLog {
    [DisallowMultipleComponent]
    public class StateEventer : MonoBehaviour {
        [SerializeField] private ExperimentPhaseManager phaseManager;
        [SerializeField] private StateManager stateManager;
        [SerializeField] private EventLogger eventLogger;

        private void OnEnable(){
            if (phaseManager == null) throw new NullReferenceException("StateEventer: phaseManager not assigned");
            if (stateManager == null) throw new NullReferenceException("StateEventer: stateManager not assigned");
            if (eventLogger == null) throw new NullReferenceException("StateEventer: eventLogger not assigned");

            stateManager.OnGenerate += OnGenerate;
            stateManager.OnGenerateEnd += OnGenerateEnd;
            stateManager.OnDiscard += OnDiscard;
        }

        private void OnDisable(){
            if (stateManager != null){
                stateManager.OnGenerate -= OnGenerate;
                stateManager.OnGenerateEnd -= OnGenerateEnd;
                stateManager.OnDiscard -= OnDiscard;
            }
        }

        private void OnGenerate(){
            if (phaseManager.CurrMethod == ExperimentMethod.PROPOSED){
                eventLogger.Caused("OnGenerate");
            }
        }

        private void OnGenerateEnd(){
            if (phaseManager.CurrMethod == ExperimentMethod.PROPOSED){
                eventLogger.Caused("OnGenerateEnd");
            }
        }

        private void OnDiscard(){
            if (phaseManager.CurrMethod == ExperimentMethod.PROPOSED){
                eventLogger.Caused("OnDiscard");
            }
        }
    }
}

