using System;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class AquireDepthImageProvider : CpuFrameProvider
{
    [SerializeField] private float updatePeriod = 0.5f;
    [SerializeField] private AROcclusionManager occlusionManager;

    Texture2D _currentImg;

    private bool _isEnd = false;
    private DateTime _timestamp;
    public override DateTime TimeStamp{
        get => _timestamp;
    }

    public override Texture2D DepthTex => _currentImg;


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start(){
        occlusionManager.frameReceived += UpdateFrame;
    }

    // Update is called once per frame
    void Update(){
        
    }

    void UpdateFrame(AROcclusionFrameEventArgs eventArgs){
        if(!occlusionManager.TryAcquireEnvironmentDepthCpuImage(out var depthImg)){
            Debug.Log("Cannot Obtain DepthImg");
            return;
        }

        if(_currentImg == null || _currentImg.width != depthImg.width || _currentImg.height != depthImg.height){
            _currentImg = new Texture2D(depthImg.width, depthImg.height, depthImg.format.AsTextureFormat(), false);
        }

        if(!IsInitTexture){
            OnDepthTexInitialized();
            IsInitTexture = true;
        }

        //TODO: Transformationを入れる
#if UNITY_IOS
        var textureParameter = new XRCpuImage.ConversionParams(depthImg, depthImg.format.AsTextureFormat(), XRCpuImage.Transformation.None);
#elif UNITY_ANDROID
        var textureParameter = new XRCpuImage.ConversionParams(depthImg, depthImg.format.AsTextureFormat(), XRCpuImage.Transformation.MirrorY);
#else  
        var textureParameter = new XRCpuImage.ConversionParams(depthImg, depthImg.format.AsTextureFormat(), XRCpuImage.Transformation.None);
#endif
        var rawTextureData = _currentImg.GetRawTextureData<byte>();
        Debug.Assert(rawTextureData.Length == depthImg.GetConvertedDataSize(textureParameter.outputDimensions, textureParameter.outputFormat),
                "The Texture2D is not the same size as the converted data.");
        depthImg.Convert(textureParameter, rawTextureData);
        _currentImg.Apply();

        depthImg.Dispose();
        _timestamp = DateTime.Now;
        TickUp();
    }
}
