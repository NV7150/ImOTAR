// using System;
// using UnityEngine;

// [DisallowMultipleComponent]
// public class SimpleMotionDiff : PoseDiffManager {
//     [Header("Inputs")]
//     [SerializeField] private AsyncFrameProvider provider;  // Async job lifecycle (ProcessStart/Cancel)
//     [SerializeField] private MotionObtain motion;          // Absolute pose source

//     [Header("Debug")]
//     [SerializeField] private bool logVerbose = false;
//     [SerializeField] private string logPrefix = "[SimpleMotionDiff]";

//     private Guid _generation = Guid.Empty;
//     private DateTime _baselineTs = DateTime.MinValue;
//     private Quaternion _baseRot = Quaternion.identity;
//     private Vector3 _basePos = Vector3.zero;

//     public override Guid Generation => _generation;
//     public override DateTime BaselineTimestamp => _baselineTs;

//     public override Quaternion Rotation {
//         get {
//             if (_generation == Guid.Empty) return Quaternion.identity;
//             if (!motion.TryGetLatestData<AbsoluteRotationData>(out var curr))
//                 throw new InvalidOperationException("SimpleMotionDiff: rotation unavailable");
//             // current->baseline in current frame: R_rel = inv(R_curr) * R_base
//             return Quaternion.Inverse(curr.Rotation) * _baseRot;
//         }
//     }

//     public override Vector3 Translation {
//         get {
//             if (_generation == Guid.Empty) return Vector3.zero;
//             if (!motion.TryGetLatestData<AbsoluteRotationData>(out var currR))
//                 throw new InvalidOperationException("SimpleMotionDiff: rotation unavailable for translation");
//             if (!motion.TryGetLatestData<AbsolutePositionData>(out var currP))
//                 throw new InvalidOperationException("SimpleMotionDiff: position unavailable");
//             // current->baseline in current frame: t_rel = inv(R_curr) * (p_base - p_curr)
//             return Quaternion.Inverse(currR.Rotation) * (_basePos - currP.Position);
//         }
//     }

//     private void OnEnable(){
//         if (provider == null) throw new NullReferenceException("SimpleMotionDiff: provider not assigned");
//         if (motion == null) throw new NullReferenceException("SimpleMotionDiff: motion not assigned");
//         provider.OnAsyncFrameStarted += OnJobStarted;
//         provider.OnAsyncFrameCanceled += OnJobCanceled;
//         _generation = Guid.Empty;
//         _baselineTs = DateTime.MinValue;
//     }

//     private void OnDisable(){
//         if (provider != null){
//             provider.OnAsyncFrameStarted -= OnJobStarted;
//             provider.OnAsyncFrameCanceled -= OnJobCanceled;
//         }
//     }

//     private void OnJobStarted(Guid jobId){
//         Reset();
//         _generation = jobId; // match provider's job id exactly
//         if (logVerbose) Debug.Log($"{logPrefix} Capture baseline gen={_generation}");
//     }

//     private void OnJobCanceled(Guid jobId){
//         if (_generation == jobId){
//             _generation = Guid.Empty; // mark as not established
//             _baselineTs = DateTime.MinValue;
//             if (logVerbose) Debug.Log($"{logPrefix} Clear baseline gen={jobId}");
//         }
//     }

//     public void Reset() {
//         if (!motion.TryGetLatestData<AbsoluteRotationData>(out var r))
//             throw new InvalidOperationException("SimpleMotionDiff: rotation unavailable at job start");
//         if (!motion.TryGetLatestData<AbsolutePositionData>(out var p))
//             throw new InvalidOperationException("SimpleMotionDiff: position unavailable at job start");
//         _baseRot = r.Rotation;
//         _basePos = p.Position;
//         _baselineTs = DateTime.UtcNow;
//     }
// }


