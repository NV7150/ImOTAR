using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using TMPro;
using dang0.ServerLog;

public class CalibMetersUI : MonoBehaviour {
    [SerializeField] private List<float> meters;
    [SerializeField] private CalibrationSphere target;
    [SerializeField] private Transform buttonsRoot;
    [SerializeField] private GameObject buttonPrefab;
    [SerializeField] private UnityEvent onStart;
    [SerializeField] private DataComposer composer;

    private readonly List<Button> createdButtons = new List<Button>();

    void Start(){
        ValidateConfig();
        BuildButtons();
    }

    private void ValidateConfig(){
        if (target == null) throw new InvalidOperationException("CalibMetersUI: target is not assigned");
        if (buttonsRoot == null) throw new InvalidOperationException("CalibMetersUI: buttonsRoot is not assigned");
        if (buttonPrefab == null) throw new InvalidOperationException("CalibMetersUI: buttonPrefab is not assigned");
        if (meters == null || meters.Count == 0) throw new InvalidOperationException("CalibMetersUI: meters is null or empty");
        if (composer == null) throw new InvalidOperationException("CalibMetersUI: composer is not assigned");

        // Prefab validation
        var prefabButton = buttonPrefab.GetComponentInChildren<Button>(true);
        if (prefabButton == null) throw new InvalidOperationException("CalibMetersUI: buttonPrefab must contain a Button component");
        var tmpTexts = buttonPrefab.GetComponentsInChildren<TMP_Text>(true);
        if (tmpTexts == null || tmpTexts.Length != 1) throw new InvalidOperationException("CalibMetersUI: buttonPrefab must contain exactly one TMP_Text");

        // meters values must be finite numbers
        for (int i = 0; i < meters.Count; i++){
            float v = meters[i];
            if (float.IsNaN(v) || float.IsInfinity(v)) throw new InvalidOperationException("CalibMetersUI: meters contains NaN or Infinity");
        }
    }

    private void BuildButtons(){
        createdButtons.Clear();
        foreach (var meter in meters){
            var go = Instantiate(buttonPrefab, buttonsRoot);
            var button = go.GetComponentInChildren<Button>(true);
            if (button == null) throw new InvalidOperationException("CalibMetersUI: instantiated buttonPrefab missing Button");
            var tmpText = go.GetComponentInChildren<TMP_Text>(true);
            if (tmpText == null) throw new InvalidOperationException("CalibMetersUI: instantiated buttonPrefab missing TMP_Text");

            tmpText.text = meter.ToString("F2") + "m";

            float captured = meter;
            button.onClick.AddListener(() => OnClick(captured));
            createdButtons.Add(button);
        }
    }

    private void OnClick(float meter){
        composer.Radius = meter;
        target.SetMeter(meter);
        if (onStart != null) onStart.Invoke();
        DisableAllButtons();
    }

    private void DisableAllButtons(){
        for (int i = 0; i < createdButtons.Count; i++){
            var b = createdButtons[i];
            if (b != null) b.interactable = false;
        }
    }
}


