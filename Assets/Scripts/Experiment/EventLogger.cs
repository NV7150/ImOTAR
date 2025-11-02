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

        private Dictionary<ExperimentPhase, List<EventEntry>> eventsByPhase = new Dictionary<ExperimentPhase, List<EventEntry>>();
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
            eventsByPhase.Clear();
        }

        public void Caused(string eventName){
            if (!isLogging) throw new InvalidOperationException("EventLogger: StartLogging has not been called");
            if (string.IsNullOrEmpty(eventName)) throw new ArgumentException("eventName is null or empty", nameof(eventName));

            var currentPhase = phaseManager.CurrPhase;
            if (!eventsByPhase.ContainsKey(currentPhase)){
                eventsByPhase[currentPhase] = new List<EventEntry>();
            }

            var timestamp = DateTime.Now.ToString(DATE_FORMAT);
            eventsByPhase[currentPhase].Add(new EventEntry(timestamp, eventName));
        }

        public override void SendPhase(ExperimentPhase phase){
            if (!isLogging) throw new InvalidOperationException("EventLogger: StartLogging has not been called");
            if (sender == null) throw new NullReferenceException("EventLogger: sender not assigned");

            if (!eventsByPhase.ContainsKey(phase) || eventsByPhase[phase].Count == 0){
                return;
            }

            var eventsWrapper = new EventsWrapper(eventsByPhase[phase]);
            var eventsJson = JsonUtility.ToJson(eventsWrapper);
            var phasedPayload = new PhasedPayload(phase, eventsJson);
            var payload = new Payload(DateTime.Now, subjectId, experimentId, phasedPayload.ToJson());
            sender.Send(payload);
        }

        public override void SendAllPhases(){
            if (!isLogging) throw new InvalidOperationException("EventLogger: StartLogging has not been called");

            foreach (var phase in eventsByPhase.Keys){
                SendPhase(phase);
            }
        }
    }
}

