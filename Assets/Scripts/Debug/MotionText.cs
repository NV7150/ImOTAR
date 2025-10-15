using TMPro;
using UnityEngine;

public class MotionText : MonoBehaviour
{
    [SerializeField] private PoseDiffManager diff;
    [SerializeField] private StructureManager stu;

    [SerializeField] private TextMeshProUGUI text;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start() {
        
    }

    // Update is called once per frame
    void Update() {
        if(diff.TryGetDiffFrom(stu.Generation, out var pos, out var rot))
            text.text = $"{pos}, {rot}";
    }
}
