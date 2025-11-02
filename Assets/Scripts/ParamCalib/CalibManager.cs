using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Events;

public enum CalibPhase{
    NOT_STARTED,
    CALIBRATING,
    END
}

[DisallowMultipleComponent]
public class CalibManager : MonoBehaviour {
    [Header("Steps")]
    [SerializeField] private List<CalibStep> steps = new List<CalibStep>();
    [SerializeField] private bool randomize = false;

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

    [SerializeField] private UnityEvent<ICalibSuite> calibFinished;

    private int _idx = -1;           // current measurement step index (-1: before first)
    private bool _isResetActive;     // true when resetStep is active
    private CalibPhase _phase;       // external-visible state
    private InMemorySuite _suite;
    private const int MinShuffleCount = 2;
    private List<string> stepOrder;

    public int CurrentIndex => _idx;
    public CalibStep CurrentStep => _isResetActive ? resetStep : ((_idx >= 0 && _idx < steps.Count) ? steps[_idx] : null);

    public ICalibSuite CalibValues => _suite;
    public CalibPhase Phase => _phase;

    // Total number of measurement steps.
    public int StepCount => steps?.Count ?? 0;

    public IReadOnlyList<string> StepOrder => stepOrder;

    // 1-based current step number (reset shows upcoming measure number).
    public int StepNo {
        get {
            if (StepCount == 0) return 0;
            int n = _idx + 1;
            if (n < 1) n = 1;
            if (n > StepCount) n = StepCount;
            return n;
        }
    }

    private void OnEnable(){
        _suite = new InMemorySuite();
        if (resetStep == null) 
            throw new NullReferenceException("CalibManager: resetStep not assigned");
        if (estimator == null) 
            throw new NullReferenceException("CalibManager: estimator not assigned");
        if (steps == null || steps.Count == 0) 
            throw new InvalidOperationException("CalibManager: steps is empty");

        _idx = -1;
        _isResetActive = true;
        _phase = CalibPhase.NOT_STARTED;

        if (startOnEnable) 
            StartCalibration();
    }

    /// <summary>
    /// Public method to start calibration manually.
    /// </summary>
    public void StartCalibration(){
        if (_phase == CalibPhase.END) 
            return;
        if (_phase == CalibPhase.NOT_STARTED && randomize)
            ShuffleSteps();
        if (_phase == CalibPhase.NOT_STARTED)
            BuildStepOrder();
        _phase = CalibPhase.CALIBRATING;
        StartActive();
    }

    public void OnPressScreen(){
        if (_phase == CalibPhase.END) 
            return;
        if (CurrentStep == null) 
            return;

        EndActive();
        Advance();
        if (_phase != CalibPhase.END) 
            StartActive();
    }

    private void StartActive(){
        var step = CurrentStep;
        if (step == null) 
            return;
        try {
            step.StartCalib();
            if (_isResetActive) { 
                if (autoRunBaseline) {
                    estimator.Resume();
                 } else {
                    estimator.Pause(); 
                }
            } else { 
                estimator.Pause(); 
            }

            if (logVerbose) {
                string kind = _isResetActive ? "reset" : "measure";
                Debug.Log($"{logPrefix} Start {kind} step ({step.GetType().Name}) idx={_idx}");
            }
        } catch (Exception ex) {
            Debug.LogError($"{logPrefix} StartCalib failed: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }

    private void ShuffleSteps(){
        if (steps == null)
            throw new InvalidOperationException("CalibManager: steps is null");
        if (steps.Count < MinShuffleCount)
            return;
        var rng = new System.Random();
        for (int i = steps.Count - 1; i > 0; i--){
            int j = rng.Next(i + 1);
            var tmp = steps[i];
            steps[i] = steps[j];
            steps[j] = tmp;
        }
    }

    private void BuildStepOrder(){
        if (steps == null) throw new InvalidOperationException("CalibManager: steps is null");
        if (steps.Count == 0) throw new InvalidOperationException("CalibManager: steps is empty");
        var set = new HashSet<string>();
        if (stepOrder == null) stepOrder = new List<string>(steps.Count);
        else stepOrder.Clear();
        for (int i = 0; i < steps.Count; i++){
            var s = steps[i];
            if (s == null) throw new InvalidOperationException("CalibManager: steps contains null");
            var id = s.Id;
            if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("CalibManager: step Id is null or empty");
            if (!set.Add(id)) throw new InvalidOperationException("CalibManager: duplicate step Id detected: " + id);
            stepOrder.Add(id);
        }
    }

    private void EndActive(){
        var step = CurrentStep;
        if (step == null) 
            return;
        try {
            step.RecordAndEnd(_suite);
            if (_isResetActive){
                estimator.DiscardCurrEstimation();
                if (stopAfterReset) 
                    estimator.Pause();
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
        if (_phase == CalibPhase.END) 
            return;
        if (_isResetActive){
            if (_idx < 0) 
                _idx = 0; // move to first measurement
            _isResetActive = false;
        } else {
            _idx++;
            if (_idx < steps.Count) {
                _isResetActive = true; // insert reset before next measurement
            } else {
                _phase = CalibPhase.END;
                calibFinished.Invoke(_suite);
                if (logVerbose) 
                    Debug.Log($"{logPrefix} All steps completed");
            }
        }
    }

    private sealed class InMemorySuite : ICalibSuite {
        private readonly Dictionary<string, ICalibParameter> _map = new Dictionary<string, ICalibParameter>();

        public bool TryGetParameter<T>(string id, out T parameter) where T : ICalibParameter {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("id is null or empty", nameof(id));
            if (_map.TryGetValue(id, out var v)){
                if (v is T t){ 
                    parameter = t; 
                    return true; 
                }
                throw new InvalidCastException("CalibSuite: stored parameter type mismatch");
            }
            parameter = default;
            return false;
        }

        public void RegisterParameter<T>(string id, T parameter) where T : ICalibParameter {
            if (parameter == null) throw new ArgumentNullException(nameof(parameter));
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("id is null or empty", nameof(id));
            if (parameter.Id != id) throw new InvalidOperationException("parameter.Id does not match id");
            _map[id] = parameter;
        }

        public string ToJson(){
            var sb = new System.Text.StringBuilder();
            sb.Append('{');
            bool wroteAny = false;
            foreach (var kv in _map){
                if (wroteAny) sb.Append(',');
                sb.Append('"').Append(kv.Key).Append("\":{");
                if (kv.Value is AngleParam ap){
                    sb.Append("\"value\":").Append(ap.Value.ToString(CultureInfo.InvariantCulture));
                    sb.Append(',');
                    sb.Append("\"safety\":").Append(ap.Safety.ToString(CultureInfo.InvariantCulture));
                } else if (kv.Value is DistanceParam dp){
                    sb.Append("\"value\":").Append(dp.Value.ToString(CultureInfo.InvariantCulture));
                    sb.Append(',');
                    sb.Append("\"safety\":").Append(dp.Safety.ToString(CultureInfo.InvariantCulture));
                } else {
                    throw new InvalidOperationException("Unknown parameter type");
                }
                sb.Append('}');
                wroteAny = true;
            }
            sb.Append('}');
            return sb.ToString();
        }

        public override string ToString(){
            if (_map.Count == 0) return "InMemorySuite: (empty)";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("InMemorySuite:");
            foreach (var kvp in _map){
                sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
            }
            return sb.ToString();
        }
    }
}


