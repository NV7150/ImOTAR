using System;
using UnityEngine;

[DisallowMultipleComponent]
public class BirthByMotion : MonoBehaviour {
    [Header("Inputs")]
    [SerializeField] private MotionObtain motion;
    [SerializeField] private StateManager state;

    private void OnEnable(){
        if (motion == null) throw new NullReferenceException("BirthByMotion: motion not assigned");
        if (state  == null) throw new NullReferenceException("BirthByMotion: state not assigned");
    }

    private void Update(){
        if (state.CurrState != State.INACTIVE) return;

        if (!motion.TryGetLatestData<ReferencePoseData>(out var refData)) return;

        if (!refData.IsStable) return;

        state.Generate();
    }
}


