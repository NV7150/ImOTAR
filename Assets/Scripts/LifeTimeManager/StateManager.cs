using UnityEngine;
using System;
using System.Collections.Generic;

public enum State {
    INACTIVE,
    GENERATING,
    ACTIVE
}

public  class StateManager : MonoBehaviour {
    public State CurrState { get => _currState; private set{ _currState = value; } }
    private State _currState = State.INACTIVE;

    // -------------------------------------------------------
    // Events backed by Lists to avoid GC Alloc on invocation
    // -------------------------------------------------------

    private readonly List<Func<bool>> _tryGenerateHandlers = new List<Func<bool>>();
    private readonly List<Func<bool>> _tryGenerateEndHandlers = new List<Func<bool>>();
    private readonly List<Func<bool>> _tryDiscardHandlers = new List<Func<bool>>();

    public event Action OnGenerate;
    public event Func<bool> TryGenerate {
        add { if (value != null && !_tryGenerateHandlers.Contains(value)) _tryGenerateHandlers.Add(value); }
        remove { _tryGenerateHandlers.Remove(value); }
    }

    public event Action OnGenerateEnd;
    public event Func<bool> TryGenerateEnd {
        add { if (value != null && !_tryGenerateEndHandlers.Contains(value)) _tryGenerateEndHandlers.Add(value); }
        remove { _tryGenerateEndHandlers.Remove(value); }
    }

    public event Action OnDiscard;
    public event Func<bool> TryDiscard {
        add { if (value != null && !_tryDiscardHandlers.Contains(value)) _tryDiscardHandlers.Add(value); }
        remove { _tryDiscardHandlers.Remove(value); }
    }

    // -------------------------------------------------------
    // Public Methods
    // -------------------------------------------------------

    public void Generate(){
        if(CurrState != State.INACTIVE)
            return;
        if(!CheckAllTrue(_tryGenerateHandlers))
            return;
        CurrState = State.GENERATING;
        OnGenerate?.Invoke();
    }

    public void GenerateEnd(){
        if(CurrState != State.GENERATING)
            return;
        if(!CheckAllTrue(_tryGenerateEndHandlers))
            return;
        CurrState = State.ACTIVE;
        OnGenerateEnd?.Invoke();
    }

    public void GenerateFailed(){
        if(CurrState != State.GENERATING)
            return;
        if(!CheckAllTrue(_tryDiscardHandlers))
            throw new InvalidOperationException("Birth Failed, but cannot die");
        CurrState = State.INACTIVE;
        OnDiscard?.Invoke();
    }

    public void Discard(){
        if(CurrState != State.ACTIVE)
            return;
        if(!CheckAllTrue(_tryDiscardHandlers))
            return;
        CurrState = State.INACTIVE;
        OnDiscard?.Invoke();
    }

    // -------------------------------------------------------
    // Private Helpers
    // -------------------------------------------------------

    private bool CheckAllTrue(List<Func<bool>> handlers){
        for(int i = 0; i < handlers.Count; i++){
            if(!handlers[i].Invoke())
                return false;
        }
        return true;
    }
}
