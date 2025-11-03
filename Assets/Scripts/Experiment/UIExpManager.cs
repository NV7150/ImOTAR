using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using ImOTAR.RecordSender;

[DisallowMultipleComponent]
public class UIExpManager : MonoBehaviour {
    [Serializable]
    public class TaskEntry {
        public Texture2D image;
        public string instructionText;
    }

    [SerializeField] private List<TaskEntry> tasks;
    [SerializeField] private ExperimentPhaseManager phaseManager;
    [SerializeField] private ImageUploadButton uploadButton;
    [SerializeField] private RawImage imageDisplay;
    [SerializeField] private TextMeshProUGUI instructionText;
    [SerializeField] private TextMeshProUGUI progressText;

    public event Action OnAllTasksCompleted;

    private int currentIndex = 0;

    private void OnEnable(){
        if (phaseManager == null) throw new NullReferenceException("UIExpManager: phaseManager not assigned");
        if (tasks == null) throw new NullReferenceException("UIExpManager: tasks not assigned");
        if (uploadButton == null) throw new NullReferenceException("UIExpManager: uploadButton not assigned");

        phaseManager.OnPhaseChanged += OnPhaseChanged;
        phaseManager.OnMethodChanged += OnMethodChanged;
    }

    private void OnDisable(){
        if (phaseManager != null){
            phaseManager.OnPhaseChanged -= OnPhaseChanged;
            phaseManager.OnMethodChanged -= OnMethodChanged;
        }
    }

    private void Update(){
        if (uploadButton != null && uploadButton.Status == ImageUploadButton.UploadStatus.Error){
            if (instructionText != null){
                instructionText.text = uploadButton.LastError;
            }
        }
    }

    private void OnPhaseChanged(ExperimentPhase newPhase){
        if (newPhase != ExperimentPhase.EXPERIMENT){
            currentIndex = 0;
        }
    }

    private void OnMethodChanged(ExperimentMethod newMethod){
        currentIndex = 0;
        if (newMethod != ExperimentMethod.NONE){
            ShowFirstTask();
        }
    }

    private void ShowFirstTask(){
        if (tasks.Count == 0) throw new InvalidOperationException("UIExpManager: tasks list is empty");
        UpdateUI();
    }

    public void NextTask(){
        ExperimentPhase currPhase = phaseManager.CurrPhase;
        if (currPhase != ExperimentPhase.EXPERIMENT) return;

        ExperimentMethod currMethod = phaseManager.CurrMethod;
        if (currMethod == ExperimentMethod.NONE) return;

        if (uploadButton != null && uploadButton.Status == ImageUploadButton.UploadStatus.Error) return;

        currentIndex++;

        if (currentIndex >= tasks.Count){
            OnAllTasksCompleted?.Invoke();
            return;
        }

        UpdateUI();
    }

    private void UpdateUI(){
        if (currentIndex < 0 || currentIndex >= tasks.Count) throw new IndexOutOfRangeException($"UIExpManager: currentIndex {currentIndex} out of range");

        TaskEntry task = tasks[currentIndex];

        if (imageDisplay != null){
            imageDisplay.texture = task.image;
        }

        if (instructionText != null){
            instructionText.text = task.instructionText;
        }

        if (progressText != null){
            progressText.text = $"画像 {currentIndex + 1}/{tasks.Count}";
        }
    }
}



