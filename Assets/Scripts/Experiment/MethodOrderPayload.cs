using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public sealed class MethodOrderPayload {
    [SerializeField] private string[] order;

    public MethodOrderPayload(IReadOnlyList<ExperimentMethod> methodOrder){
        if (methodOrder == null) throw new ArgumentNullException(nameof(methodOrder));
        if (methodOrder.Count == 0) throw new ArgumentException("methodOrder is empty", nameof(methodOrder));

        order = new string[methodOrder.Count];
        for (int i = 0; i < methodOrder.Count; i++){
            order[i] = methodOrder[i].ToString();
        }
    }

    public string ToJson(){
        return JsonUtility.ToJson(this);
    }
}






