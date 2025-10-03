using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class CalibManager : MonoBehaviour {
    [Header("Steps")]
    [SerializeField] private List<CalibStep> steps = new List<CalibStep>();

    [Header("Debug")]
    [SerializeField] private bool logVerbose = false;
    [SerializeField] private string logPrefix = "[CalibManager]";

    private int _currentIndex = -1;
    private InMemorySuite _suite;

    public int CurrentIndex => _currentIndex;
    public CalibStep CurrentStep => (_currentIndex >= 0 && _currentIndex < steps.Count) ? steps[_currentIndex] : null;

    private void OnEnable(){
        _suite = new InMemorySuite();
        MoveNextOrThrowIfEmpty();
    }

    public void OnPressScreen(){
        var step = CurrentStep;
        if (step == null) return;
        try {
            step.RecordAndEnd(_suite);
        } catch (Exception ex) {
            Debug.LogError($"{logPrefix} Step RecordAndEnd failed: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
        MoveNext();
    }

    private void MoveNextOrThrowIfEmpty(){
        if (steps == null || steps.Count == 0) throw new InvalidOperationException("CalibManager: steps is empty");
        _currentIndex = 0;
        StartCurrent();
    }

    private void MoveNext(){
        _currentIndex++;
        if (_currentIndex >= steps.Count){
            if (logVerbose) Debug.Log($"{logPrefix} All steps completed");
            return;
        }
        StartCurrent();
    }

    private void StartCurrent(){
        var step = CurrentStep;
        if (step == null) return;
        try {
            step.StartCalib();
            if (logVerbose) Debug.Log($"{logPrefix} Start step[{_currentIndex}] {step.GetType().Name}");
        } catch (Exception ex) {
            Debug.LogError($"{logPrefix} Step StartCalib failed: {ex.Message}\n{ex.StackTrace}");
            throw;
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
    }
}


