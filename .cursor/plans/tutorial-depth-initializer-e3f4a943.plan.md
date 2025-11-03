<!-- e3f4a943-b438-4c3c-a07b-f3ae2c53be6e 9a8d77d8-80a5-4397-a0ce-f6be7e66ef95 -->
# Tutorial Depth Initializer Implementation

## 実装内容

### 1. クリアシェーダーの作成

**ファイル**: `Assets/Shaders/RenderPipe/DepthClearShader.shader`

- R32_FloatのRenderTextureを-1.0fで塗りつぶす単純なシェーダー
- フルスクリーン描画用（頂点3つで三角形描画）
- fragmentシェーダーで固定値-1.0fを返す

### 2. 初期化コンポーネントの作成

**ファイル**: `Assets/Scripts/Experiment/TutorialDepthInitializer.cs`

**SerializedFields**:

- `ExperimentPhaseManager phaseManager` - フェーズ監視用
- `RenderTexture targetRT` - 初期化対象のRenderTexture
- `Shader clearShader` - 上記で作成したクリアシェーダー

**動作**:

- `OnEnable`でnullチェック、`OnPhaseChanged`イベント購読
- `OnPhaseChanged`で`ExperimentPhase.TUTORIAL`の時のみ動作
- `Graphics.Blit`でクリアシェーダーを使い、targetRTを-1.0fで塗りつぶす
- TUTORIAL以外のフェーズでは何もしない（競合回避）
- `OnDisable`でイベント解除

**エラーハンドリング**:

- 各SerializedFieldがnullの場合は例外を投げる
- clearShaderからMaterialを作成できない場合は例外を投げる

## ファイル配置の理由

- シェーダー: 既存の`FullScreenDepthWriter.shader`と同じディレクトリに配置（関連機能）
- スクリプト: 既存の`ExperimentOrchestrator.cs`と同じディレクトリに配置（実験フロー関連）