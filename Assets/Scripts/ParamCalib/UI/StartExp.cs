using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class StartExp : MonoBehaviour {
    [SerializeField] private CalibManager calibMan;
    [SerializeField] private List<GameObject> disableObjs;
    [SerializeField] private List<GameObject> enableObjs;

    public void StartProcess(){
        disableObjs.ForEach(o => o.SetActive(false));
        enableObjs.ForEach(o => o.SetActive(true));
        calibMan.StartCalibration();
    }
}