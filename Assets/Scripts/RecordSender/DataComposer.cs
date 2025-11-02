using System;
using UnityEngine;
using System.Globalization;
namespace dang0.ServerLog{
    [DisallowMultipleComponent]
    public sealed class DataComposer : MonoBehaviour {
        [SerializeField] private Sender sender;
        [SerializeField] private string subjectid;
        [SerializeField] private CalibManager manager;

        public string ExperimentId { get; set; }

        private const float DefaultRadius = -1f;
        private const string RadiusKey = "radius";
        private const string OrderKey = "order";

        public float Radius { get; set; } = DefaultRadius;

        public void OnCalibFinished(ICalibSuite suite){
            if (suite == null) throw new ArgumentNullException(nameof(suite));
            if (string.IsNullOrEmpty(ExperimentId)) throw new ArgumentException("ExperimentId is null or empty", nameof(ExperimentId));
            if (sender == null) throw new NullReferenceException("Sender is not assigned");

            if (manager == null) throw new NullReferenceException("CalibManager is not assigned");
            var json = suite.ToJson();
            if (string.IsNullOrEmpty(json) || json[0] != '{') throw new InvalidOperationException("Suite.ToJson returned invalid JSON");

            if (float.IsNaN(Radius) || float.IsInfinity(Radius)) throw new ArgumentOutOfRangeException(nameof(Radius), "Radius is NaN or Infinity");
            if (Radius == DefaultRadius) throw new InvalidOperationException("Radius is default (-1).");
            if (Radius <= 0f) throw new ArgumentOutOfRangeException(nameof(Radius), "Radius must be > 0.");

            var openIndex = json.IndexOf('{');
            var closeIndex = json.LastIndexOf('}');
            if (closeIndex <= openIndex) throw new InvalidOperationException("Suite.ToJson returned invalid JSON");

            var inner = json.Substring(openIndex + 1, closeIndex - openIndex - 1).Trim();
            if (inner.IndexOf("\"" + OrderKey + "\"", StringComparison.Ordinal) >= 0) throw new InvalidOperationException("order already exists in suite json");
            if (inner.IndexOf("\"" + RadiusKey + "\"", StringComparison.Ordinal) >= 0) throw new InvalidOperationException("radius already exists in suite json");

            var isEmpty = inner.Length == 0;
            var order = manager.StepOrder;
            if (order == null || order.Count == 0) throw new InvalidOperationException("StepOrder is null or empty");
            var orderStr = BuildOrderJson(order);
            json = json.Substring(0, closeIndex) + (isEmpty ? "" : ",") + " \"" + OrderKey + "\": " + orderStr + json.Substring(closeIndex);

            // Update indices after inserting order, then append radius
            closeIndex = json.LastIndexOf('}');
            inner = json.Substring(openIndex + 1, closeIndex - openIndex - 1).Trim();
            isEmpty = inner.Length == 0;
            var radiusStr = Radius.ToString("R", CultureInfo.InvariantCulture);
            json = json.Substring(0, closeIndex) + (isEmpty ? "" : ",") + " \"" + RadiusKey + "\": " + radiusStr + json.Substring(closeIndex);

            var payload = new Payload(DateTime.UtcNow, subjectid, ExperimentId, json);
            sender.Send(payload);
        }

        private static string BuildOrderJson(System.Collections.Generic.IReadOnlyList<string> order){
            var sb = new System.Text.StringBuilder();
            sb.Append('[');
            for (int i = 0; i < order.Count; i++){
                if (i > 0) sb.Append(',');
                var id = order[i];
                if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("order contains null or empty id");
                sb.Append('"').Append(Escape(id)).Append('"');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string Escape(string s){
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}


