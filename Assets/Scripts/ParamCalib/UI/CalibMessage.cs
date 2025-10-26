using TMPro;
using UnityEngine;

public class CalibMessage : MonoBehaviour {
    [SerializeField] private CalibManager calibMan;
    [SerializeField] private TextMeshProUGUI text;
    [SerializeField] private TextMeshProUGUI stepNum;

    void Update(){
        string msg = "";
        if(calibMan.Phase == CalibPhase.CALIBRATING){
            msg = calibMan.CurrentStep.StepMessage;
            stepNum.text = $"{calibMan.StepNo}/{calibMan.StepCount}";
        } else if(calibMan.Phase == CalibPhase.END) {
            msg = "完了です。ありがとうございました。";
            stepNum.text = $"{calibMan.StepCount}/{calibMan.StepCount}";
        } else if(calibMan.Phase == CalibPhase.NOT_STARTED) {
            msg = "Not Started";
            stepNum.text = "N/A";
        }
        text.text = msg;
    }
}