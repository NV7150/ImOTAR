using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// 非同期深度配信のモック。
/// SerializedFieldで指定したTexture2D(R16, mm)を一定の遅延後に
/// RenderTexture(RFloat, meters)へ変換し、Asyncイベントとして配信する。
/// </summary>
public class PseudoAsyncDepthRec : AsyncFrameProvider {
    [Header("Test Input")]
    [SerializeField] private Texture2D testDepthTexture; // R16, mm単位

    [Header("Output")]
    [SerializeField] private RenderTexture targetRT; // RFloat, meters単位

    [Header("Conversion")]
    [SerializeField] private Material depthConversionMaterial; // R16mm → meters変換用

    [Header("Timing Settings")]
    [SerializeField] private float delayMs = 200f; // 非同期遅延
    [SerializeField] private bool autoStart = true;
    [SerializeField] private bool loop = true;

    private DateTime lastUpdateTime;
    private Coroutine sendCoroutine;

    public override RenderTexture FrameTex => targetRT;
    public override DateTime TimeStamp => lastUpdateTime;

    private void Start(){
        ValidateConfiguration();
        if (targetRT != null){
            IsInitTexture = true;
            OnFrameTexInitialized();
        }
        if (autoStart){
            StartSending();
        }
    }

    private void OnEnable(){
        if (autoStart && sendCoroutine == null){
            StartSending();
        }
    }

    private void OnDisable(){
        StopSending();
    }

    public void StartSending(){
        if (!ValidateConfiguration()) return;
        StopSending();
        sendCoroutine = StartCoroutine(SendAsyncCoroutine());
    }

    public void StopSending(){
        if (sendCoroutine != null){
            StopCoroutine(sendCoroutine);
            sendCoroutine = null;
        }
    }

    public void SendSingleAsync(){
        if (!ValidateConfiguration()) return;
        StopSending();
        sendCoroutine = StartCoroutine(SendOnceAsyncCoroutine());
    }

    private IEnumerator SendAsyncCoroutine(){
        do{
            yield return SendOnceAsyncCoroutine();
        } while (loop);
    }

    private IEnumerator SendOnceAsyncCoroutine(){
        if (!ValidateConfiguration()) yield break;

        Guid id = ProcessStart();
        yield return new WaitForSeconds(delayMs / 1000f);

        // 変換: R16 mm → RFloat meters
        Graphics.Blit(testDepthTexture, targetRT, depthConversionMaterial);

        lastUpdateTime = DateTime.Now;
        ProcessEnd(id);
    }

    private bool ValidateConfiguration(){
        if (testDepthTexture == null){
            Debug.LogError("PseudoAsyncDepthRec: testDepthTexture is not assigned");
            return false;
        }
        if (targetRT == null){
            Debug.LogError("PseudoAsyncDepthRec: targetRT is not assigned");
            return false;
        }
        if (depthConversionMaterial == null){
            Debug.LogError("PseudoAsyncDepthRec: depthConversionMaterial is not assigned");
            return false;
        }
        return true;
    }

    [ContextMenu("Send Single Async")]
    private void CtxSendOnce(){ SendSingleAsync(); }

    [ContextMenu("Start Async Sending")]
    private void CtxStart(){ StartSending(); }

    [ContextMenu("Stop Async Sending")]
    private void CtxStop(){ StopSending(); }
}


