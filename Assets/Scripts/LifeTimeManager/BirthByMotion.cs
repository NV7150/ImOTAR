using System;
using UnityEngine;

[DisallowMultipleComponent]
public class BirthByMotion : MonoBehaviour {
    [Header("Inputs")]
    [SerializeField] private MotionObtain motion;
    [SerializeField] private StateManager state;

    [Header("Stability")]

    [Header("Debug")]
    [SerializeField] private bool logVerbose = false;
    [SerializeField] private string logPrefix = "[BirthByMotion]";

    private bool _armed;

    private void OnEnable(){
        if (motion == null) throw new NullReferenceException("BirthByMotion: motion not assigned");
        if (state  == null) throw new NullReferenceException("BirthByMotion: state not assigned");
        _armed = true;
    }

    private void Update(){
        if (state.CurrState != State.INACTIVE) {
            // Only monitors for BIRTH when INACTIVE
            _armed = true;
            return;
        }

        if (!motion.TryGetLatestData<ReferencePoseData>(out var refData)) return;

        if (!refData.IsStable){
            // Re-arm on unstable
            _armed = true;
            return;
        }

        if (!_armed) return;

        if (logVerbose) Debug.Log($"{logPrefix} Birth: rotVel={refData.EmaRotVelDeg:F3} deg/s, posVel={refData.EmaPosVelMps:F4} m/s");
        state.Generate();
        _armed = false; // wait for unstable before next trigger
    }
}


