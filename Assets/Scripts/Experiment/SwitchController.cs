using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Experiment
{
    [DisallowMultipleComponent]
    public class SwitchController : MonoBehaviour
    {
        [Serializable]
        public struct Entry
        {
            public string Name;
            public List<GameObject> Targets;
        }

        [Header("Input")]
        [SerializeField] private List<Entry> _entries = new List<Entry>();
        [SerializeField] private Button _buttonPrefab;
        [SerializeField] private Transform _root;

        private void Start()
        {
            ValidateInputs();
            DisableAllTargets(); // default: all disabled
            BuildButtons();
        }

        private void ValidateInputs()
        {
            if (_entries == null || _entries.Count == 0)
            {
                throw new InvalidOperationException("Entries must have at least one item.");
            }
            if (_buttonPrefab == null)
            {
                throw new InvalidOperationException("Button prefab is not assigned.");
            }
            if (_root == null)
            {
                throw new InvalidOperationException("Root transform is not assigned.");
            }
            for (int i = 0; i < _entries.Count; i++)
            {
                var list = _entries[i].Targets;
                if (list == null)
                {
                    throw new InvalidOperationException($"Entry[{i}] targets list is null.");
                }
                for (int j = 0; j < list.Count; j++)
                {
                    if (list[j] == null)
                    {
                        throw new InvalidOperationException($"Entry[{i}].Targets[{j}] is null.");
                    }
                }
            }
        }

        private void BuildButtons()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var button = Instantiate(_buttonPrefab, _root);
                var switchButton = button.GetComponent<SwitchButton>();
                if (switchButton == null)
                {
                    switchButton = button.gameObject.AddComponent<SwitchButton>();
                }
                switchButton.Setup(this, i, _entries[i].Name);
            }
        }

        internal void OnButtonClicked(int index)
        {
            if (index < 0 || index >= _entries.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            // Disable all, then enable only the selected entry's targets
            DisableAllTargets();

            var selected = _entries[index].Targets;
            if (selected == null)
            {
                throw new InvalidOperationException($"Entry[{index}] targets list is null.");
            }
            for (int i = 0; i < selected.Count; i++)
            {
                var go = selected[i];
                if (go == null)
                {
                    throw new InvalidOperationException($"Entry[{index}].Targets[{i}] is null or destroyed.");
                }
                go.SetActive(true);
            }
        }

        private void DisableAllTargets()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var list = _entries[i].Targets;
                if (list == null)
                {
                    throw new InvalidOperationException($"Entry[{i}] targets list is null.");
                }
                for (int j = 0; j < list.Count; j++)
                {
                    var go = list[j];
                    if (go == null)
                    {
                        throw new InvalidOperationException($"Entry[{i}].Targets[{j}] is null or destroyed.");
                    }
                    go.SetActive(false);
                }
            }
        }
        
    }
}
