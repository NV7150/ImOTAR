# Repository Guidelines

## Project Structure & Module Organization
- The Unity project’s runtime code lives in `Assets/Scripts`, split by feature (`DepthEstimation`, `ParamCalib`, `Motion`, `UI`, `Filters`, etc.), so keep new systems beside their closest sibling.
- Scenes, prefabs, and runtime-loaded assets sit in `Assets/Scenes`, `Assets/Prefabs`, and `Assets/Resources`; calibration and sample captures belong in `Assets/TestData`.
- Engine and rendering configuration is under `ProjectSettings`, while package references are in `Packages/manifest.json`. Generated folders (`Library`, `Logs`, `Temp`, `Build`) must stay out of source control.
- Platform hooks and build automation live in `Assets/Editor` (currently `PostprocessBuild.cs` for iOS), so add editor tooling there.

## Build, Test, and Development Commands
- `open -na "Unity" --args -projectPath "$(pwd)"` launches the project in Unity 6000.0.59f2, matching `ProjectSettings/ProjectVersion.txt`.
- `Unity -quit -batchmode -projectPath "$(pwd)" -runTests -testPlatform PlayMode -logFile Logs/PlayMode.log` executes the Play Mode suite headlessly; check the log for pass/fail.
- For scripted builds, invoke `Unity -quit -batchmode -projectPath "$(pwd)" -executeMethod BuildAutomation.PerformIOSBuild -logFile Logs/Build-iOS.log` after wiring an entry point inside `Assets/Editor`; adjust the method name to the concrete wrapper you add.

## Coding Style & Naming Conventions
- Use 4-space indentation and `PascalCase` for public APIs. Private serialized fields follow the existing `_camelCase` pattern with `[SerializeField]`.
- Keep MonoBehaviours lean: cache components in `Awake`/`Start`, prefer dependency injection helpers or serialized references over `FindObjectOfType`.
- Group related attributes (e.g., `[Header]`, `[Tooltip]`) above fields, and mirror folder names in script namespaces to aid eventual assembly definition splits.

## Testing Guidelines
- Place deterministic harnesses in `Assets/Scripts/Legacy` and migrate stable checks into Unity Test Framework fixtures under `Assets/Tests` (create if absent). Name fixtures `{Feature}Tests` and methods `Should_<Action>`.
- Use `Assets/TestData` for reproducible inputs; document new datasets in `Assets/Docs` with source and resolution.
- Collect coverage via the Code Coverage package when touching inference or motion pipelines, targeting the critical execution paths rather than UI glue.

## Commit & Pull Request Guidelines
- Follow the short prefix convention in git history (`fix:`, `update:`, `feat:`) and write imperative, scoped subjects under 65 characters.
- PRs must describe the scenario validated, include screenshots or short clips when changing scenes or UI, and attach relevant logs from `Logs/`.
- Link tracking issues where possible, enumerate follow-up tasks openly, and call out asset size or licensing impacts from new Cesium or OpenCV dependencies.
