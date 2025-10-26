using UnityEngine;

public class PushPlayer : MonoBehaviour {
    [SerializeField] private AudioSource audioSource;
    public void Push(){
        audioSource.Play();
    }
}
