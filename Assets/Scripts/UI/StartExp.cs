using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Events;

public class StartExp : MonoBehaviour {
    [SerializeField] private UnityEvent startProcess;
    [SerializeField] private List<GameObject> disableObjs;
    [SerializeField] private List<GameObject> enableObjs;

    public void StartProcess(){
        disableObjs.ForEach(o => o.SetActive(false));
        enableObjs.ForEach(o => o.SetActive(true));
        
    }
}