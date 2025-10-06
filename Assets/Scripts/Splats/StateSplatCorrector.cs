using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
public class StateSplatCorrector : SplatCorrector {
    [Header("State")]
    [SerializeField] private StateManager state;

    protected override void OnEnable(){
        if (state == null) throw new NullReferenceException("StateSplatCorrector: state not assigned");
        base.OnEnable();
    }

    protected override bool ShouldRun(){
        if (state.CurrState != State.ACTIVE) return false;
        return true;
    }
}


