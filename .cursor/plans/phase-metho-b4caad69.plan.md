<!-- b4caad69-ca20-4034-90b4-0899a60526c7 add83ae8-ed38-48ef-a05c-91f663a13d88 -->
# ExperimentPhase/Method分離リファクタリング

## 目的

現在のExperimentPhaseからBASELINE/NAIVE/PROPOSEDを分離し、NOT_STARTED/TUTORIAL/EXPERIMENT/ENDというPhaseと、BASELINE/NAIVE/PROPOSEDというMethodの二層構造に変更する。

## 新規ファイル作成

### 1. ExperimentMethod.cs

`Assets/Scripts/Experiment/ExperimentMethod.cs`を作成

```csharp
public enum ExperimentMethod {
    NONE,
    BASELINE,
    NAIVE,
    PROPOSED
}
```

### 2. MethodPayload.cs

`Assets/Scripts/Experiment/MethodPayload.cs`を作成（PhasedPayloadから名称変更）

```csharp
[Serializable]
public class MethodPayload {
    public string method;
    public string data;

    public MethodPayload(ExperimentMethod method, string data){
        if (data == null) throw new ArgumentNullException(nameof(data));
        this.method = method.ToString();
        this.data = data;
    }

    public string ToJson(){
        return JsonUtility.ToJson(this);
    }
}
```

## 既存ファイル変更

### 3. ExperimentPhase.cs

BASELINE/NAIVE/PROPOSEDを削除し、EXPERIMENTを追加

```csharp
public enum ExperimentPhase {
    NOT_STARTED,
    TUTORIAL,
    EXPERIMENT,
    END
}
```

### 4. ExperimentPhaseManager.cs

CurrMethodプロパティとOnMethodChangedイベントを追加

- `private ExperimentMethod _currMethod`を追加
- `public ExperimentMethod CurrMethod { get; set; }`プロパティを追加（setterでOnMethodChangedを発火）
- `public event Action<ExperimentMethod> OnMethodChanged`を追加

### 5. ExperimentStarter.cs

- `List<ExperimentPhase> randomizedPhases` → `List<ExperimentMethod> randomizedMethods`
- `RandomizedPhases`プロパティ → `RandomizedMethods`プロパティ
- `RandomizePhases()`メソッド → `RandomizeMethods()`メソッド
- リスト内容をBASELINE/NAIVE/PROPOSEDの3つのMethodに変更
- `phaseManager.CurrPhase = ExperimentPhase.TUTORIAL`は維持

### 6. ExperimentOrchestrator.cs

- `sendPerPhase` → `sendPerMethod`にリネーム
- `OnPhaseChanged`内で`sendPerMethod`の場合に`logger.SendMethod(previousMethod)`を呼ぶ
- `previousPhase`に加えて`previousMethod`を追跡
- `phaseManager.OnMethodChanged += OnMethodChanged`をサブスクライブ
- `OnMethodChanged(ExperimentMethod newMethod)`ハンドラを追加し、`imageUploader.ExpId`を`{ExperimentId}-{newMethod}`に設定
- `NextPhase()`内でTUTORIAL後は`CurrPhase = EXPERIMENT`に変更し、`CurrMethod = RandomizedMethods[0]`を設定
- 以降のmethod切り替えでは`CurrMethod`のみを更新

### 7. MethodSwitcher.cs

- `PipelineEntry.phase` → `PipelineEntry.method`（型を`ExperimentMethod`に変更）
- `ValidatePipelines()`で`ExperimentMethod.NONE`をチェック
- `OnPhaseChanged`に加えて`OnMethodChanged`をサブスクライブ
- EXPERIMENT中はMethodに応じてパイプラインを切り替え、TUTORIAL中は全てfalse

### 8. UIExpManager.cs

- `OnPhaseChanged`で`EXPERIMENT`フェーズの判定
- `OnMethodChanged`をサブスクライブし、BASELINE/NAIVE/PROPOSEDで`ShowFirstTask()`
- `NextTask()`内のフェーズチェックをMethod判定に変更

### 9. ExpLogger.cs

- `SendPhase(ExperimentPhase phase)` → `SendMethod(ExperimentMethod method)`に変更

### 10. EventLogger.cs

- `Dictionary<ExperimentPhase, List<EventEntry>> eventsByPhase` → `Dictionary<ExperimentMethod, List<EventEntry>> eventsByMethod`
- `Caused()`内で`phaseManager.CurrMethod`を使用
- `SendPhase()` → `SendMethod(ExperimentMethod method)`に変更
- `PhasedPayload` → `MethodPayload`に変更
- `SendAllPhases()` → `SendAllMethods()`に変更

### 11. PerformanceMonitor.cs

- `Dictionary<ExperimentPhase, List<MetricData>> recordedDataByPhase` → `Dictionary<ExperimentMethod, List<MetricData>> recordedDataByMethod`
- `CollectMetrics()`内で`phaseManager.CurrMethod`を使用
- `SendPhase()` → `SendMethod(ExperimentMethod method)`に変更
- `PhasedPayload` → `MethodPayload`に変更
- ファイル名フォーマットを`{timestamp}_{method}`に変更
- `SendAllPhases()` → `SendAllMethods()`に変更

### 12. StateEventer.cs

- 各イベントハンドラ内で`phaseManager.CurrPhase == ExperimentPhase.PROPOSED` → `phaseManager.CurrMethod == ExperimentMethod.PROPOSED`に変更

## ファイル削除

### 13. PhasedPayload.cs

`Assets/Scripts/Experiment/PhasedPayload.cs`を削除（MethodPayload.csに置き換え）

## 実装の注意点

- ExperimentPhaseManager: OnPhaseChangedとOnMethodChangedは独立して発火
- TUTORIAL中はCurrMethod = ExperimentMethod.NONE
- EXPERIMENT中のMethod切り替えではCurrPhaseは変更しない
- ログ送信粒度はMethodごと
- ImageUploaderのExpIdはMethodベースで`{ExperimentId}-{Method}`形式

### To-dos

- [ ] ExperimentMethod.csを作成（NONE/BASELINE/NAIVE/PROPOSED）
- [ ] MethodPayload.csを作成（PhasedPayloadから名称変更）
- [ ] ExperimentPhase.csからBASELINE/NAIVE/PROPOSEDを削除しEXPERIMENTを追加
- [ ] ExperimentPhaseManager.csにCurrMethodとOnMethodChangedを追加
- [ ] ExperimentStarter.csでPhaseリストをMethodリストに変更
- [ ] ExperimentOrchestrator.csでPhase/Method分離ロジックとExpId設定を実装
- [ ] MethodSwitcher.csでphaseをmethodに変更しMethod切り替えロジック実装
- [ ] UIExpManager.csでPhase/Method判定を分離
- [ ] ExpLogger.csでSendPhaseをSendMethodに変更
- [ ] EventLogger.csでPhase→Method変更とMethodPayload使用
- [ ] PerformanceMonitor.csでPhase→Method変更とMethodPayload使用
- [ ] StateEventer.csでPhase判定をMethod判定に変更
- [ ] PhasedPayload.csとそのmetaファイルを削除