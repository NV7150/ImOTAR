using System;
using UnityEngine;

[Serializable]
public class PhasedPayload {
    public string phase;
    public string data;

    public PhasedPayload(ExperimentPhase phase, string data){
        if (data == null) throw new ArgumentNullException(nameof(data));
        this.phase = phase.ToString();
        this.data = data;
    }

    public string ToJson(){
        return JsonUtility.ToJson(this);
    }
}

