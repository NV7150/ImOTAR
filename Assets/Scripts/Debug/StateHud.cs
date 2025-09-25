using System;
using System.Text;
using UnityEngine;
using TMPro;


[DisallowMultipleComponent]
public class StateHud : MonoBehaviour {
    [Header("UI")]
    [SerializeField] private TMP_Text  text;
    [SerializeField, Min(10f)] private float refreshIntervalMs = 100f;

    [Header("Sources")]
    [SerializeField] private StateManager state;
    [SerializeField] private DieByRule die;
    [SerializeField] private BirthByMotion birth;
    [SerializeField] private PoseDiffManager pose;

    [Header("Format")]
    [SerializeField, Min(0)] private int precisionAngle = 2;
    [SerializeField, Min(0)] private int precisionDist = 3;
    [SerializeField, Min(0)] private int precisionVel = 3;

    private float _nextAt;
    private readonly StringBuilder _sb = new StringBuilder(256);

    private void OnEnable(){
        if (text == null) throw new NullReferenceException("StateHud: text not assigned");
        if (state == null) throw new NullReferenceException("StateHud: state not assigned");
        if (die == null) throw new NullReferenceException("StateHud: die not assigned");
        if (birth == null) throw new NullReferenceException("StateHud: birth not assigned");
        if (pose == null) throw new NullReferenceException("StateHud: pose not assigned");
        _nextAt = 0f;
    }

    private void LateUpdate(){
        if (Time.unscaledTime * 1000f < _nextAt) return;
        _nextAt = Time.unscaledTime * 1000f + Mathf.Max(10f, refreshIntervalMs);

        _sb.Length = 0;
        // State
        _sb.Append("State: ").Append(state.CurrState.ToString()).Append('\n');

        // Birth
        _sb.Append("Birth: ");
        _sb.Append("rotVel=").Append(birth.EmaRotVel.ToString("F" + precisionAngle));
        _sb.Append("/thr=").Append(birth.RotVelStableDegPerSec.ToString("F" + precisionAngle));
        _sb.Append(", posVel=").Append(birth.EmaPosVel.ToString("F" + precisionVel));
        _sb.Append("/thr=").Append(birth.PosVelStableMps.ToString("F" + precisionVel));
        _sb.Append(", stable=").Append(birth.StableAccumMs.ToString("F0")).Append("/")
           .Append(birth.StableTimeMs.ToString("F0")).Append(" ms");
        _sb.Append(", fit=").Append(birth.IsStableNow ? "true" : "false").Append('\n');

        // Die (motion + coverage)
        float ang = 0f, dist = 0f;
        if (pose.Generation != Guid.Empty){
            ang = Quaternion.Angle(Quaternion.identity, pose.Rotation);
            dist = pose.Translation.magnitude;
        }
        _sb.Append("Die: motion ang=").Append(ang.ToString("F" + precisionAngle))
           .Append("/thr=").Append(die.RotDieDeg.ToString("F" + precisionAngle))
           .Append(", dist=").Append(dist.ToString("F" + precisionDist))
           .Append("/thr=").Append(die.PosDieMeters.ToString("F" + precisionDist)).Append('\n');

        _sb.Append("     coverage ema=").Append(die.EmaUnknownRatio.ToString("F3"))
           .Append("/thr=").Append(die.UnknownRatioThresh.ToString("F3"))
           .Append(", inFlight=").Append(die.RequestInFlight ? "true" : "false")
           .Append(", latestReady=").Append(die.HasLatestUnknown ? "true" : "false").Append('\n');

        // Pose
        _sb.Append("Pose: gen=");
        if (pose.Generation == Guid.Empty) _sb.Append("—"); else _sb.Append(pose.Generation.ToString().Substring(0, 8));
        _sb.Append(", baseTs=");
        if (pose.Generation == Guid.Empty) _sb.Append("—"); else _sb.Append(pose.BaselineTimestamp.ToString("HH:mm:ss.fff"));

        text.text = _sb.ToString();
    }
}


