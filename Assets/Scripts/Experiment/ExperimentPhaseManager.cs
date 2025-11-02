using System;
using UnityEngine;

[DisallowMultipleComponent]
public class ExperimentPhaseManager : MonoBehaviour {
    public event Action<ExperimentPhase> OnPhaseChanged;

    private ExperimentPhase _currPhase;
    public ExperimentPhase CurrPhase {
        get => _currPhase;
        set {
            _currPhase = value;
            OnPhaseChanged?.Invoke(_currPhase);
        }
    }

    public string ExperimentId { get; set; }
}

