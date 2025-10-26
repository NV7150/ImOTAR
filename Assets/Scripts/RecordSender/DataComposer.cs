using System;
using UnityEngine;

namespace dang0.ServerLog{
    [DisallowMultipleComponent]
    public sealed class DataComposer : MonoBehaviour {
        [SerializeField] private Sender sender;
        [SerializeField] private string subjectid;

        public string ExperimentId { get; set; }

        public void OnCalibFinished(ICalibSuite suite){
            if (suite == null) throw new ArgumentNullException(nameof(suite));
            if (string.IsNullOrEmpty(ExperimentId)) throw new ArgumentException("ExperimentId is null or empty", nameof(ExperimentId));
            if (sender == null) throw new NullReferenceException("Sender is not assigned");

            var json = suite.ToJson();
            if (string.IsNullOrEmpty(json) || json[0] != '{') throw new InvalidOperationException("Suite.ToJson returned invalid JSON");

            var payload = new Payload(DateTime.UtcNow, subjectid, ExperimentId, json);
            sender.Send(payload);
        }

        private static string Escape(string s){
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}


