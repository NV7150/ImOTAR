using System;
using System.Text;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace dang0.ServerLog{
    [DisallowMultipleComponent]
    public sealed class Sender : MonoBehaviour {
        private const string Endpoint = "/ingest";

        [SerializeField] private string domain;

        public void Send(Payload payload){
            if (string.IsNullOrEmpty(domain)) throw new ArgumentException("domain is null or empty", nameof(domain));
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var url = domain.EndsWith("/") ? (domain.TrimEnd('/') + Endpoint) : (domain + Endpoint);
            var json = JsonUtility.ToJson(payload);
            StartCoroutine(Post(url, json));
        }

        private IEnumerator Post(string url, string json){
            Debug.Log($"json: {json}");
            var bodyRaw = Encoding.UTF8.GetBytes(json);
            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)){
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success){
                    throw new InvalidOperationException($"POST failed: {req.responseCode} {req.error} \n json: {json}");
                } else {
                    Debug.Log("Successfully sent");
                }
            }
        }
    }
}


