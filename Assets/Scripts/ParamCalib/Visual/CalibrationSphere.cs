using UnityEngine;

public class CalibrationSphere : MonoBehaviour {
    [SerializeField] private GameObject sphere;
    [SerializeField] private GameObject player;
    [SerializeField] private float maxSize = 3.0f;

    [SerializeField] private float minSize = 0.3f;

    [SerializeField] private Vector3 scaleAxis = new Vector3(1f, 10f, 1f);

    void Start(){
        sphere.transform.localScale = ((maxSize - minSize) * 0.5f + minSize) * scaleAxis;
        sphere.transform.position = player.transform.position;
    }

    public void Adjust(float rate){
        // Scale is diameter, so multiply 2 to make it radius
        sphere.transform.localScale = ((maxSize - minSize) * rate + minSize) * 2 * scaleAxis;
    }

    public void SetMeter(float meter){
        // Force set by meter; ignores min/max range.
        // Scale is diameter, so multiply 2 to make it radius
        sphere.transform.localScale = meter * 2 * scaleAxis;
    }
}