using UnityEngine;
using System;

public enum State {
    DEAD,
    BIRTHING,
    ALIVE
}

public  class StateManager : MonoBehaviour {
    public State CurrState { get; private set; }

    public void Birth(){
        if(CurrState != State.DEAD)
            return;
        if(!AllTrue(TryBirth))
            return;
        CurrState = State.BIRTHING;
        OnBirth?.Invoke();
    }

    public void BirthEnd(){
        if(CurrState != State.BIRTHING)
            return;
        if(!AllTrue(TryBirthEnd))
            return;
        CurrState = State.ALIVE;
        OnBirthEnd?.Invoke();
    }

    public void BirthFailed(){
        if(CurrState != State.BIRTHING)
            return;
        if(!AllTrue(TryDead))
            throw new InvalidOperationException("Birth Failed, but cannot die");
        CurrState = State.DEAD;
        OnDead?.Invoke();
    }

    public void Die(){
        if(CurrState != State.ALIVE)
            return;
        if(!AllTrue(TryDead))
            return;
        CurrState = State.DEAD;
        OnDead?.Invoke();
    }

    public event Action OnBirth;

    public event Func<bool> TryBirth;

    public event Action OnBirthEnd;
    public event Func<bool> TryBirthEnd;

    public event Action OnDead;
    public event Func<bool> TryDead;

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