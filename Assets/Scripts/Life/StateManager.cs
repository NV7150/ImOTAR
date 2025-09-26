using UnityEngine;
using System;

public enum State {
    INACTIVE,
    GENERATING,
    ACTIVE
}

public  class StateManager : MonoBehaviour {
    public State CurrState { get; private set; }

    public void Generate(){
        if(CurrState != State.INACTIVE)
            return;
        if(!AllTrue(TryGenerate))
            return;
        CurrState = State.GENERATING;
        OnGenerate?.Invoke();
    }

    public void GenerateEnd(){
        if(CurrState != State.GENERATING)
            return;
        if(!AllTrue(TryGenerateEnd))
            return;
        CurrState = State.ACTIVE;
        OnGenerateEnd?.Invoke();
    }

    public void GenerateFailed(){
        if(CurrState != State.GENERATING)
            return;
        if(!AllTrue(TryDiscard))
            throw new InvalidOperationException("Birth Failed, but cannot die");
        CurrState = State.INACTIVE;
        OnDiscard?.Invoke();
    }

    public void Discard(){
        if(CurrState != State.ACTIVE)
            return;
        if(!AllTrue(TryDiscard))
            return;
        CurrState = State.INACTIVE;
        OnDiscard?.Invoke();
    }

    public event Action OnGenerate;

    public event Func<bool> TryGenerate;

    public event Action OnGenerateEnd;
    public event Func<bool> TryGenerateEnd;

    public event Action OnDiscard;
    public event Func<bool> TryDiscard;

    private static bool AllTrue(Func<bool> handlers){
        if(handlers == null)
            return true;
        var list = handlers.GetInvocationList();
        for(int i = 0; i < list.Length; i++){
            var f = (Func<bool>)list[i];
            if(!f())
                return false;
        }
        return true;
    }
}