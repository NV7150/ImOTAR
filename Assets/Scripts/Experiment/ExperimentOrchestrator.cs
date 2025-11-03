using System;
using System.Collections.Generic;
using UnityEngine;
using ImOTAR.RecordSender;

[DisallowMultipleComponent]
public class ExperimentOrchestrator : MonoBehaviour {
    [SerializeField] private ExperimentPhaseManager phaseManager;
    [SerializeField] private List<ExpLogger> loggers;
    [SerializeField] private ImageUploader imageUploader;
    [SerializeField] private ExperimentStarter starter;
    [SerializeField] private UIExpManager uiExpManager;
    [SerializeField] private bool sendPerMethod;

    private int currentMethodIndex = -1;
    private ExperimentPhase previousPhase = ExperimentPhase.NOT_STARTED;
    private ExperimentMethod previousMethod = ExperimentMethod.NONE;

    private void OnEnable(){
        if (phaseManager == null) throw new NullReferenceException("ExperimentOrchestrator: phaseManager not assigned");
        if (loggers == null) throw new NullReferenceException("ExperimentOrchestrator: loggers not assigned");
        if (imageUploader == null) throw new NullReferenceException("ExperimentOrchestrator: imageUploader not assigned");
        if (starter == null) throw new NullReferenceException("ExperimentOrchestrator: starter not assigned");
        if (uiExpManager == null) throw new NullReferenceException("ExperimentOrchestrator: uiExpManager not assigned");

        phaseManager.OnPhaseChanged += OnPhaseChanged;
        phaseManager.OnMethodChanged += OnMethodChanged;
        uiExpManager.OnAllTasksCompleted += OnAllTasksCompleted;
    }

    private void OnDisable(){
        if (phaseManager != null){
            phaseManager.OnPhaseChanged -= OnPhaseChanged;
            phaseManager.OnMethodChanged -= OnMethodChanged;
        }
        if (uiExpManager != null){
            uiExpManager.OnAllTasksCompleted -= OnAllTasksCompleted;
        }
    }

    private void OnPhaseChanged(ExperimentPhase newPhase){
        if (newPhase == ExperimentPhase.END){
            if (sendPerMethod){
                if (previousMethod != ExperimentMethod.NONE){
                    foreach (var logger in loggers){
                        if (logger == null) throw new NullReferenceException("ExperimentOrchestrator: loggers contains null");
                        logger.SendMethod(previousMethod);
                    }
                }
            } else {
                foreach (var logger in loggers){
                    if (logger == null) throw new NullReferenceException("ExperimentOrchestrator: loggers contains null");
                    logger.SendAllMethods();
                }
            }
        }

        previousPhase = newPhase;
    }

    private void OnMethodChanged(ExperimentMethod newMethod){
        if (sendPerMethod && previousMethod != ExperimentMethod.NONE){
            foreach (var logger in loggers){
                if (logger == null) throw new NullReferenceException("ExperimentOrchestrator: loggers contains null");
                logger.SendMethod(previousMethod);
            }
        }

        imageUploader.ExpId = $"{phaseManager.ExperimentId}-{newMethod}";

        previousMethod = newMethod;
    }

    private void OnAllTasksCompleted(){
        NextPhase();
    }

    public void NextPhase(){
        if (starter.RandomizedMethods == null) throw new InvalidOperationException("ExperimentOrchestrator: RandomizedMethods not initialized");

        ExperimentPhase currentPhase = phaseManager.CurrPhase;

        if (currentPhase == ExperimentPhase.NOT_STARTED){
            throw new InvalidOperationException("ExperimentOrchestrator: Cannot call NextPhase before experiment starts");
        }

        if (currentPhase == ExperimentPhase.END){
            return;
        }

        if (currentPhase == ExperimentPhase.TUTORIAL){
            currentMethodIndex = 0;
            phaseManager.CurrPhase = ExperimentPhase.EXPERIMENT;
            phaseManager.CurrMethod = starter.RandomizedMethods[currentMethodIndex];
            return;
        }

        currentMethodIndex++;

        if (currentMethodIndex >= starter.RandomizedMethods.Count){
            phaseManager.CurrPhase = ExperimentPhase.END;
        } else {
            phaseManager.CurrMethod = starter.RandomizedMethods[currentMethodIndex];
        }
    }
}



