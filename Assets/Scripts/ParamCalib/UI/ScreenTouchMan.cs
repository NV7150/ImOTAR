using UnityEngine;

public class ScreenTouchMan : MonoBehaviour {
    [SerializeField] private CalibManager calibMan;

    private void Update(){
        if (calibMan.Phase == CalibPhase.CALIBRATING && Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began) {
            calibMan.OnPressScreen();
        }
    }
}