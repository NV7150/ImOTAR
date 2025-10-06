using UnityEngine;

public class CalibrationSphere : MonoBehaviour {
    [SerializeField] private GameObject sphere;
    [SerializeField] private GameObject player;
    [SerializeField] private float maxSize = 3.0f;

    [SerializeField] private float minSize = 0.3f;

    void Start(){
        sphere.transform.localScale = ((maxSize - minSize) * 0.5f + minSize)* new Vector3(1, 10, 1);
        sphere.transform.position = player.transform.position;
    }

    public void Adjust(float rate){
        sphere.transform.localScale = ((maxSize - minSize) * rate + minSize) * new Vector3(1, 10, 1);
        Debug.Log(sphere.transform.localScale);
    }
}