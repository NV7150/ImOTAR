using TMPro;
using UnityEngine;

public class CalibMessage : MonoBehaviour {
    [SerializeField] private CalibManager calibMan;
    [SerializeField] private TextMeshProUGUI text;

    void Update(){
        if(calibMan.CurrentIndex < 0)
            return;
        if(!calibMan.IsDone){
            var msg = calibMan.CurrentStep.StepMessage;
            text.text = msg;
        }else {
            var msg = calibMan.CalibValues.ToString();
            text.text = msg;
        }

    }
}