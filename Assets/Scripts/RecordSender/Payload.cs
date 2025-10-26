using System;
using UnityEngine;

namespace dang0.ServerLog{

    [Serializable]
    public class Payload {
        public readonly string DATE_FORMAT = "yyyy-MM-dd_HH:mm:ss";
        
        [SerializeField] private string timestamp;
        [SerializeField] private string subjectId;
        [SerializeField] private string experimentId;
        [SerializeField] private string data;

        public Payload(DateTime timestamp, string subjectId, string experimentId, string payload){
            if (string.IsNullOrEmpty(subjectId) || string.IsNullOrEmpty(experimentId)) 
                throw new ArgumentException("id is null or empty.", nameof(experimentId));
            if (payload == null) 
                throw new ArgumentNullException(nameof(payload));
            this.timestamp = timestamp.ToString(DATE_FORMAT);
            this.subjectId = subjectId;
            this.experimentId = experimentId;
            this.data = payload;
        }
    }
}
