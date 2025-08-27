using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using TMPro;
using UnityEngine;

public class WeightedFeatureDebugText : MonoBehaviour {
    [Header("Targets")]
    [SerializeField] private WeightedFeatureScheduler scheduler;
    [SerializeField] private MotionObtain motionSource;
    [SerializeField] private List<MotionFeatureExtractorBase> extractors = new List<MotionFeatureExtractorBase>();
    [SerializeField] private TMP_Text targetText;

    [Header("Score Display")]
    [SerializeField] private bool showScore = true;

    private void OnEnable(){
        ValidateOrThrow();
    }

    private void OnValidate(){
        // Basic sanity
        if (extractors == null) extractors = new List<MotionFeatureExtractorBase>();
    }

    private void ValidateOrThrow(){
        if (scheduler == null) throw new NullReferenceException("WeightedFeatureDebugText: scheduler is not assigned");
        if (motionSource == null) throw new NullReferenceException("WeightedFeatureDebugText: motionSource is not assigned");
        if (targetText == null) throw new NullReferenceException("WeightedFeatureDebugText: TMP_Text targetText is not assigned");
        for (int i = 0; i < extractors.Count; i++) if (extractors[i] == null) throw new NullReferenceException($"WeightedFeatureDebugText: extractors[{i}] is null");
    }

    private void Update(){
        // Collect features
        var sb = new StringBuilder(512);
        sb.Append("State: ").Append(scheduler.CurrentState.ToString());

        int totalLen = 0;
        for (int i = 0; i < extractors.Count; i++){
            var ext = extractors[i];
            if (ext == null) continue;
            NativeArray<float> feats = ext.ExtractFeature(motionSource);
            if (!feats.IsCreated || feats.Length == 0) continue;
            if (totalLen == 0) sb.Append("\nFeatures: [");
            else sb.Append(", ");
            for (int j = 0; j < feats.Length; j++){
                if (j > 0) sb.Append(',');
                sb.Append(feats[j].ToString("0.###"));
            }
            totalLen += feats.Length;
        }
        if (totalLen > 0) sb.Append(']');

        if (showScore && totalLen > 0 && scheduler.FeatureCount == totalLen){
            float score = 0f;
            var featsArr = scheduler.CurrentFeatures;
            var wArr = scheduler.CurrentWeights;
            int n = Mathf.Min(featsArr.IsCreated ? featsArr.Length : 0, wArr.IsCreated ? wArr.Length : 0);
            for (int i = 0; i < n; i++) score += featsArr[i] * wArr[i];
            sb.Append("\nScore: ").Append(score.ToString("0.######"));
        }

        targetText.text = sb.ToString();
    }
}


