using System;
using UnityEngine;

[Serializable]
public class MethodPayload {
    public string method;
    public string data;

    public MethodPayload(ExperimentMethod method, string data){
        if (data == null) throw new ArgumentNullException(nameof(data));
        this.method = method.ToString();
        this.data = data;
    }

    public string ToJson(){
        return JsonUtility.ToJson(this);
    }
}

