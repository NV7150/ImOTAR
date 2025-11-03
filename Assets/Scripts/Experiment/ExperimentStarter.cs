using System;
using System.Collections.Generic;
using UnityEngine;
using ImOTAR.RecordSender;

[DisallowMultipleComponent]
public class ExperimentStarter : MonoBehaviour {
    [SerializeField] private string subjectId;
    [SerializeField] private List<ExpLogger> loggers;
    [SerializeField] private ImageUploader imageUploader;
    [SerializeField] private ExperimentPhaseManager phaseManager;

    public string ExperimentId { get; set; }

    private List<ExperimentMethod> randomizedMethods;

    public List<ExperimentMethod> RandomizedMethods => randomizedMethods;

    public void StartExperiment(){
        if (string.IsNullOrWhiteSpace(ExperimentId)) throw new InvalidOperationException("ExperimentStarter: ExperimentId is not set");
        if (string.IsNullOrWhiteSpace(subjectId)) throw new InvalidOperationException("ExperimentStarter: subjectId is not set");
        if (phaseManager == null) throw new NullReferenceException("ExperimentStarter: phaseManager not assigned");
        if (imageUploader == null) throw new NullReferenceException("ExperimentStarter: imageUploader not assigned");
        if (loggers == null) throw new NullReferenceException("ExperimentStarter: loggers not assigned");

        RandomizeMethods();

        phaseManager.ExperimentId = ExperimentId;

        foreach (var logger in loggers){
            if (logger == null) throw new NullReferenceException("ExperimentStarter: loggers contains null");
            logger.StartLogging(subjectId, ExperimentId);
        }

        phaseManager.CurrPhase = ExperimentPhase.TUTORIAL;
        imageUploader.ExpId = $"{ExperimentId}-{phaseManager.CurrPhase}";
    }

    private void RandomizeMethods(){
        randomizedMethods = new List<ExperimentMethod> {
            ExperimentMethod.BASELINE,
            ExperimentMethod.NAIVE,
            ExperimentMethod.PROPOSED
        };

        for (int i = randomizedMethods.Count - 1; i > 0; i--){
            int randomIndex = UnityEngine.Random.Range(0, i + 1);
            ExperimentMethod temp = randomizedMethods[i];
            randomizedMethods[i] = randomizedMethods[randomIndex];
            randomizedMethods[randomIndex] = temp;
        }
    }
}



