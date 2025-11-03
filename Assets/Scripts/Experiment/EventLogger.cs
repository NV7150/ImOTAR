using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace dang0.ServerLog {
    [DisallowMultipleComponent]
    public class EventLogger : ExpLogger {
        private const string DATE_FORMAT = "yyyy-MM-dd_HH:mm:ss";

        [Serializable]
        public class EventEntry {
            public string timestamp;
            public string eventName;

            public EventEntry(string timestamp, string eventName){
                this.timestamp = timestamp;
                this.eventName = eventName;
            }
        }

        [Serializable]
        private class EventsWrapper {
            public List<EventEntry> events;

            public EventsWrapper(List<EventEntry> events){
                this.events = events;
            }
        }

        [SerializeField] private ExperimentPhaseManager phaseManager;
        [SerializeField] private Sender sender;

        private Dictionary<ExperimentMethod, List<EventEntry>> eventsByMethod = new Dictionary<ExperimentMethod, List<EventEntry>>();
        private string subjectId;
        private string experimentId;
        private bool isLogging = false;

        public override void StartLogging(string subjectId, string experimentId){
            if (string.IsNullOrEmpty(subjectId)) throw new ArgumentException("subjectId is null or empty", nameof(subjectId));
            if (string.IsNullOrEmpty(experimentId)) throw new ArgumentException("experimentId is null or empty", nameof(experimentId));
            if (phaseManager == null) throw new NullReferenceException("EventLogger: phaseManager not assigned");
            if (sender == null) throw new NullReferenceException("EventLogger: sender not assigned");

            this.subjectId = subjectId;
            this.experimentId = experimentId;
            this.isLogging = true;
            eventsByMethod.Clear();
        }

        public void Caused(string eventName){
            if (!isLogging) throw new InvalidOperationException("EventLogger: StartLogging has not been called");
            if (string.IsNullOrEmpty(eventName)) throw new ArgumentException("eventName is null or empty", nameof(eventName));

            var currentMethod = phaseManager.CurrMethod;
            if (!eventsByMethod.ContainsKey(currentMethod)){
                eventsByMethod[currentMethod] = new List<EventEntry>();
            }

            var timestamp = DateTime.Now.ToString(DATE_FORMAT);
            eventsByMethod[currentMethod].Add(new EventEntry(timestamp, eventName));
        }

        public override void SendMethod(ExperimentMethod method){
            if (!isLogging) throw new InvalidOperationException("EventLogger: StartLogging has not been called");
            if (sender == null) throw new NullReferenceException("EventLogger: sender not assigned");

            if (!eventsByMethod.ContainsKey(method) || eventsByMethod[method].Count == 0){
                return;
            }

            var eventsWrapper = new EventsWrapper(eventsByMethod[method]);
            var eventsJson = JsonUtility.ToJson(eventsWrapper);
            var methodPayload = new MethodPayload(method, eventsJson);
            var payload = new Payload(DateTime.Now, subjectId, experimentId, methodPayload.ToJson());
            sender.Send(payload);
        }

        public override void SendAllMethods(){
            if (!isLogging) throw new InvalidOperationException("EventLogger: StartLogging has not been called");

            foreach (var method in eventsByMethod.Keys){
                SendMethod(method);
            }
        }
    }
}

