<!-- c9dd7870-0b22-4041-9db3-611900502bdb e3b9206f-80f6-418e-8825-4fc553a7c108 -->
# Experiment Phase Control System Implementation

## 新規ファイル作成（Assets/Scripts/Experiment/）

### 1. ExperimentPhase.cs

```csharp
public enum ExperimentPhase {
    NOT_STARTED,
    BASELINE,
    NAIVE,
    PROPOSED,
    END
}
```

### 2. ExperimentPhaseManager.cs

- `public ExperimentPhase CurrPhase { get; set; }` でPhase保持
- `public string ExperimentId { get; set; }` で被験者番号保持
- `public event Action<ExperimentPhase> OnPhaseChanged` イベント（CurrPhaseのsetterで発火）

### 3. ExpLogger.cs

抽象基底クラス：

```csharp
public abstract class ExpLogger : MonoBehaviour {
    public abstract void StartLogging(string subjectId, string experimentId);
    public abstract void SendPhase(ExperimentPhase phase);
    public abstract void SendAllPhases();
}
```

### 4. MethodSwitcher.cs

```csharp
[Serializable]
public class PipelineEntry {
    public ExperimentPhase phase;
    public GameObject pipeline;
}
```

- `[SerializeField] List<PipelineEntry> pipelines`
- `[SerializeField] ExperimentPhaseManager phaseManager`
- OnEnableでphaseManager.OnPhaseChangedを購読
- OnDisableで購読解除
- イベントハンドラで：pipelinesリスト内にNOT_STARTED/ENDが含まれていたら例外、指定PhaseのGameObjectだけEnable

### 5. PhasedPayload.cs

- `public ExperimentPhase phase`
- `public string data`
- コンストラクタ `PhasedPayload(ExperimentPhase phase, string data)`
- `string ToJson()` で `{"phase": "BASELINE", "data": {...}}` 形式を返す（JsonUtility使用）

### 6. EventLogger.cs (ExpLogger継承)

```csharp
[Serializable]
public class EventEntry {
    public string timestamp;
    public string eventName;
}
```

- `[SerializeField] ExperimentPhaseManager phaseManager`
- `[SerializeField] Sender sender`
- `Dictionary<ExperimentPhase, List<EventEntry>>` でPhaseごとに記録
- subjectId, experimentId（StartLoggingで受け取る）
- `StartLogging(string subjectId, string experimentId)` で初期化
- `Caused(string eventName)` で現在Phaseにイベント追加（StartLogging前ならInvalidOperationException）
- `SendPhase(ExperimentPhase phase)` でJSON化→PhasedPayload→Payload→sender.Send、ログ記録停止
- `SendAllPhases()` で全PhaseにSendPhase()呼び出し
- JSON形式: `{"events": [{"timestamp": "...", "eventName": "..."}, ...]}`

### 7. StateEventer.cs

- `[SerializeField] ExperimentPhaseManager phaseManager`
- `[SerializeField] StateManager stateManager`
- `[SerializeField] EventLogger eventLogger`
- OnEnableでstateManagerのイベント購読（OnGenerate, OnGenerateEnd, OnDiscard）
- OnDisableで購読解除
- イベントハンドラでphaseManager.CurrPhase==PROPOSEDならeventLogger.Caused()呼び出し

## 既存ファイル変更

### 8. PerformanceMonitor.cs (ExpLogger継承に変更)

変更点：

- ExpLoggerを継承
- `[SerializeField] ExperimentPhaseManager phaseManager` 追加
- subjectId, experimentIdフィールド削除（StartLoggingで受け取る）
- `Dictionary<ExperimentPhase, List<MetricData>>` でPhaseごとに記録
- `StartLogging(string subjectId, string experimentId)` にリネーム（既存StartMonitor）、1回だけ呼ぶ想定、内部でPhase変化を監視
- EndMonitor()削除
- `SendPhase(ExperimentPhase phase)` で指定PhaseのデータをJSON化→PhasedPayload→Payload→sender.Send、ログ記録停止
- `SendAllPhases()` で全PhaseにSendPhase()呼び出し

## 実装の注意点

- すべてのコンポーネントはMonoBehaviour継承、[DisallowMultipleComponent]属性
- 設定ミスは例外（NullReferenceException, ArgumentException, InvalidOperationException等）
- DATE_FORMATは既存に合わせる（"yyyy-MM-dd_HH:mm:ss"）
- 各コンポーネントが必要な参照を自分のSerializeFieldで持つ