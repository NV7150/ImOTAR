using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Experiment
{
    [DisallowMultipleComponent]
    public class SwitchButton : MonoBehaviour
    {
        [SerializeField] private TMP_Text _label;
        private SwitchController _controller;
        private int _index = -1;
        private Button _button;
        private bool _initialized;

        public void Setup(SwitchController controller, int index, string displayName)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            _controller = controller;
            _index = index;
            EnsureComponents();
            if (_label == null)
            {
                throw new InvalidOperationException("Label TMP_Text component not assigned.");
            }
            _label.text = displayName ?? string.Empty;
            _button.onClick.AddListener(HandleClick);
            _initialized = true;
        }

        private void EnsureComponents()
        {
            if (_button == null)
            {
                _button = GetComponent<Button>();
                if (_button == null)
                {
                    throw new InvalidOperationException("Button component missing on prefab instance.");
                }
            }
            if (_label == null)
            {
                _label = GetComponentInChildren<TMP_Text>();
            }
        }

        private void HandleClick()
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("SwitchButton clicked before initialization.");
            }
            if (_controller == null)
            {
                throw new InvalidOperationException("Controller reference lost.");
            }
            _controller.OnButtonClicked(_index);
        }

        private void OnDestroy()
        {
            if (_button != null)
            {
                _button.onClick.RemoveListener(HandleClick);
            }
        }
    }
}
