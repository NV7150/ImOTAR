using System;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using TMPro;

namespace Experiment {
    public class CameraFPSDisplay : MonoBehaviour {
        [SerializeField] private ARCameraManager cameraManager;
        [SerializeField] private TextMeshProUGUI fpsText;

        private float _lastFrameTime;
        private int _frameCount;
        private float _timer;

        private void OnEnable() {
            if (cameraManager != null) {
                cameraManager.frameReceived += OnCameraFrameReceived;
            }
        }

        private void OnDisable() {
            if (cameraManager != null) {
                cameraManager.frameReceived -= OnCameraFrameReceived;
            }
        }

        private void OnCameraFrameReceived(ARCameraFrameEventArgs args) {
            float currentTime = Time.realtimeSinceStartup;
            
            // 初回実行時の初期化
            if (_lastFrameTime <= 0) {
                _lastFrameTime = currentTime;
                return;
            }

            _frameCount++;
            _timer += (currentTime - _lastFrameTime);
            _lastFrameTime = currentTime;

            if (_timer >= 1.0f) {
                float fps = _frameCount / _timer;
                if (fpsText != null) {
                    fpsText.text = $"CamFPS: {fps:F1}";
                }
                
                _frameCount = 0;
                _timer = 0f;
            }
        }
    }
}

