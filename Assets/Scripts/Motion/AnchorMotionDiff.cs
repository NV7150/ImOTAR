using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

[DisallowMultipleComponent]
public sealed class AnchorMotionDiff : PoseDiffManager {
    [Header("Inputs")]
    [SerializeField] private AsyncFrameProvider provider;   // Async job lifecycle
    [SerializeField] private MotionObtain motion;           // AR camera world pose source
    [SerializeField] private ARAnchorManager anchorManager; // AR Session Origin child

    [Header("Params")]
    [SerializeField] private int maxAnchors = 32;           // Same-time anchors upper bound
    [SerializeField] private bool requireTracking = false;  // Switch immediately per policy 2->b

    [Header("Debug")]
    [SerializeField] private bool logVerbose = false;
    [SerializeField] private string logPrefix = "[AnchorMotionDiff]";

    private readonly Dictionary<Guid, ARAnchor> _anchorsByJob = new Dictionary<Guid, ARAnchor>();
    private readonly Queue<Guid> _fifoOrder = new Queue<Guid>();
    private struct Snapshot {
        public readonly Quaternion Rot;
        public readonly Vector3 Pos;
        public Snapshot(Quaternion rot, Vector3 pos){ Rot = rot; Pos = pos; }
    }
    private readonly Dictionary<Guid, Snapshot> _snaps = new Dictionary<Guid, Snapshot>();

    private void OnEnable(){
        if (provider == null) throw new NullReferenceException("AnchorMotionDiff: provider not assigned");
        if (motion == null) throw new NullReferenceException("AnchorMotionDiff: motion not assigned");
        if (anchorManager == null) throw new NullReferenceException("AnchorMotionDiff: anchorManager not assigned");

        provider.OnAsyncFrameStarted += OnJobStarted;
        provider.OnAsyncFrameCanceled += OnJobCanceled;
    }

    private void OnDisable(){
        if (provider != null){
            provider.OnAsyncFrameStarted -= OnJobStarted;
            provider.OnAsyncFrameCanceled -= OnJobCanceled;
        }
    }

    private void OnJobStarted(Guid jobId){
        if (!motion.TryGetLatestData<AbsoluteRotationData>(out var r)){
            if (logVerbose) Debug.LogWarning($"{logPrefix} Rotation unavailable at job start: {jobId}");
            return;
        }
        if (!motion.TryGetLatestData<AbsolutePositionData>(out var p)){
            if (logVerbose) Debug.LogWarning($"{logPrefix} Position unavailable at job start: {jobId}");
            return;
        }

        // Record snapshot for fallback until anchor becomes available
        _snaps[jobId] = new Snapshot(r.Rotation, p.Position);

        var pose = new Pose(p.Position, r.Rotation);
        CreateAnchorAsync(jobId, pose);
    }

    private void OnJobCanceled(Guid jobId){
        if (logVerbose) Debug.Log($"{logPrefix} Job canceled gen={jobId} (anchor kept)");
        // Keeping anchors by default; cleanup policy is managed by maxAnchors
    }

    public override bool TryGetDiffFrom(Guid generation, out Vector3 pos, out Quaternion rot){
        pos = Vector3.zero;
        rot = Quaternion.identity;

        if (generation == Guid.Empty) return false;
        var hasCurrR = motion.TryGetLatestData<AbsoluteRotationData>(out var currR);
        var hasCurrP = motion.TryGetLatestData<AbsolutePositionData>(out var currP);
        if (!hasCurrR || !hasCurrP){
            if (logVerbose) Debug.LogWarning($"{logPrefix} Pose unavailable in TryGetDiffFrom for gen={generation}");
            return false;
        }

        // Prefer anchor-based diff if anchor exists
        if (_anchorsByJob.TryGetValue(generation, out var anchor)){
            if (anchor == null){
                _anchorsByJob.Remove(generation);
            } else {
                if (requireTracking && anchor.trackingState != TrackingState.Tracking) return false;
                var anchorRot = anchor.transform.rotation;
                var anchorPos = anchor.transform.position;
                rot = Quaternion.Inverse(currR.Rotation) * anchorRot;
                pos = Quaternion.Inverse(currR.Rotation) * (anchorPos - currP.Position);
                return true;
            }
        }

        // Fallback to snapshot-based diff until anchor is available
        if (_snaps.TryGetValue(generation, out var snap)){
            rot = Quaternion.Inverse(currR.Rotation) * snap.Rot;
            pos = Quaternion.Inverse(currR.Rotation) * (snap.Pos - currP.Position);
            return true;
        }

        return false;
    }

    private void EnforceAnchorLimit(){
        if (maxAnchors < 0) throw new InvalidOperationException("AnchorMotionDiff: maxAnchors must be >= 0");
        while (_anchorsByJob.Count > maxAnchors && _fifoOrder.Count > 0){
            var oldId = _fifoOrder.Dequeue();
            if (_anchorsByJob.TryGetValue(oldId, out var oldAnchor)){
                if (oldAnchor != null){
                    if (logVerbose) Debug.Log($"{logPrefix} Prune anchor gen={oldId}");
                    Destroy(oldAnchor.gameObject);
                }
                _anchorsByJob.Remove(oldId);
                // Also drop any snapshot baseline for this generation
                _snaps.Remove(oldId);
            }
        }
    }

    private async void CreateAnchorAsync(Guid jobId, Pose pose){
        try {
            var result = await anchorManager.TryAddAnchorAsync(pose);
            if (!result.status.IsSuccess()){
                if (logVerbose) Debug.LogWarning($"{logPrefix} TryAddAnchorAsync failed for gen={jobId} status={result.status}");
                return; // keep snapshot fallback
            }

            var anchor = result.value;
            if (anchor == null){
                if (logVerbose) Debug.LogWarning($"{logPrefix} TryAddAnchorAsync returned null anchor gen={jobId}");
                return;
            }

            _anchorsByJob[jobId] = anchor;
            _fifoOrder.Enqueue(jobId);
            EnforceAnchorLimit();

            if (logVerbose) Debug.Log($"{logPrefix} Anchor created for gen={jobId}");
        } catch (Exception ex) {
            Debug.LogError($"{logPrefix} TryAddAnchorAsync exception gen={jobId}: {ex}");
        }
    }
}


