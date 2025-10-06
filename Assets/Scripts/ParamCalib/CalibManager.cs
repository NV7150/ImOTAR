using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class CalibManager : MonoBehaviour {
    [Header("Steps")]
    [SerializeField] private List<CalibStep> steps = new List<CalibStep>();

    [Header("Reset (Baseline)")]
    [SerializeField] private CalibStep resetStep; // baseline step instance (not included in steps)
    [SerializeField] private CalibEstimateManager estimator; // state-less estimator
    [SerializeField] private bool autoRunBaseline = true;
    [SerializeField] private bool stopAfterReset = true;

    [Header("Auto Start")]
    [SerializeField] private bool startOnEnable = true; // Automatically start calibration on OnEnable

    [Header("Debug")]
    [SerializeField] private bool logVerbose = false;
    [SerializeField] private string logPrefix = "[CalibManager]";

    private int _idx = -1;           // current measurement step index (-1: before first)
    private bool _isResetActive;     // true when resetStep is active
    private bool _isDone;            // all measurement steps completed
    private InMemorySuite _suite;

    public int CurrentIndex => _idx;
    public bool IsDone => _isDone;
    public CalibStep CurrentStep => _isResetActive ? resetStep : ((_idx >= 0 && _idx < steps.Count) ? steps[_idx] : null);

    public ICalibSuite CalibValues => _suite;

    private void OnEnable(){
        _suite = new InMemorySuite();
        if (resetStep == null) throw new NullReferenceException("CalibManager: resetStep not assigned");
        if (estimator == null) throw new NullReferenceException("CalibManager: estimator not assigned");
        if (steps == null || steps.Count == 0) throw new InvalidOperationException("CalibManager: steps is empty");

        _idx = -1;
        _isResetActive = true;
        _isDone = false;

        if (startOnEnable) 
            StartCalibration();
    }

    /// <summary>
    /// Public method to start calibration manually.
    /// </summary>
    public void StartCalibration(){
        if (_isDone) return;
        StartActive();
    }

    public void OnPressScreen(){
        if (_isDone) return;
        if (CurrentStep == null) return;

        EndActive();
        Advance();
        if (!_isDone) StartActive();
    }

    private void StartActive(){
        var step = CurrentStep;
        if (step == null) return;
        try {
            step.StartCalib();
            if (_isResetActive) { if (autoRunBaseline) estimator.Resume(); else estimator.Pause(); }
            else { estimator.Pause(); }
            if (logVerbose) {
                string kind = _isResetActive ? "reset" : "measure";
                Debug.Log($"{logPrefix} Start {kind} step ({step.GetType().Name}) idx={_idx}");
            }
        } catch (Exception ex) {
            Debug.LogError($"{logPrefix} StartCalib failed: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }

    private void EndActive(){
        var step = CurrentStep;
        if (step == null) return;
        try {
            step.RecordAndEnd(_suite);
            if (_isResetActive){
                estimator.DiscardCurrEstimation();
                if (stopAfterReset) estimator.Pause();
            }
            if (logVerbose) {
                string kind = _isResetActive ? "reset" : "measure";
                Debug.Log($"{logPrefix} End {kind} step ({step.GetType().Name}) idx={_idx}");
            }
        } catch (Exception ex) {
            Debug.LogError($"{logPrefix} RecordAndEnd failed: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }

    private void Advance(){
        if (_isDone) return;
        if (_isResetActive){
            if (_idx < 0) _idx = 0; // move to first measurement
            _isResetActive = false;
        } else {
            _idx++;
            if (_idx < steps.Count) {
                _isResetActive = true; // insert reset before next measurement
            } else {
                _isDone = true;
                if (logVerbose) Debug.Log($"{logPrefix} All steps completed");
            }
        }
    }

    private sealed class InMemorySuite : ICalibSuite {
        private readonly Dictionary<Type, ICalibParameter> _map = new Dictionary<Type, ICalibParameter>();

        public bool TryGetParameter<T>(out T parameter) where T : ICalibParameter {
            if (_map.TryGetValue(typeof(T), out var v)){
                if (v is T t){ parameter = t; return true; }
                throw new InvalidCastException("CalibSuite: stored parameter type mismatch");
            }
            parameter = default;
            return false;
        }

        public void RegisterParameter<T>(T parameter) where T : ICalibParameter {
            if (parameter == null) throw new ArgumentNullException(nameof(parameter));
            _map[typeof(T)] = parameter;
        }

        public override string ToString(){
            if (_map.Count == 0) return "InMemorySuite: (empty)";
            
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("InMemorySuite:");
            foreach (var kvp in _map){
                sb.AppendLine($"  {kvp.Key.Name}: {kvp.Value}");
            }
            return sb.ToString();
        }
    }
}


