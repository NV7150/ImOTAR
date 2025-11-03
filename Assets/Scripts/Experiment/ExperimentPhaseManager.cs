using System;
using UnityEngine;

[DisallowMultipleComponent]
public class ExperimentPhaseManager : MonoBehaviour {
    public event Action<ExperimentPhase> OnPhaseChanged;
    public event Action<ExperimentMethod> OnMethodChanged;

    private ExperimentPhase _currPhase;
    public ExperimentPhase CurrPhase {
        get => _currPhase;
        set {
            _currPhase = value;
            OnPhaseChanged?.Invoke(_currPhase);
        }
    }

    private ExperimentMethod _currMethod;
    public ExperimentMethod CurrMethod {
        get => _currMethod;
        set {
            _currMethod = value;
            OnMethodChanged?.Invoke(_currMethod);
        }
    }

    public string ExperimentId { get; set; }
}

