<!-- 8a6ef81a-b945-4514-a30c-d717a40c1a70 c2ed72f0-02aa-4848-a9ed-10a95ff546dd -->
# Button Snap Caller for ImageUploader

## What we'll add

- File: `Assets/Scripts/RecordSender/ImageUploadButton.cs` (MonoBehaviour)

## Behavior

- Fields
  - `[SerializeField] ImageUploader uploader` (assign existing component)
  - `public bool IsUploadEnd { get; private set; } = true;`
  - `private int inflight = 0;`
  - `private const string TimeFmt = "yyyyMMdd_HHmmssfff";` (UTC, no spaces)
- Methods
  - `public void Snap()`
    - Build `id = DateTime.UtcNow.ToString(TimeFmt)`
    - Fire-and-forget `RunSnapAsync(id)` (do not block UI thread)
  - `private async Task RunSnapAsync(string id)`
    - Validate `uploader != null`; throw if null (no placeholders)
    - `Interlocked.Increment(ref inflight); IsUploadEnd = false;`
    - `await uploader.Take(id);`
    - `if (Interlocked.Decrement(ref inflight) == 0) IsUploadEnd = true;`

## Essential snippets (concise)

- Timestamp ID
```csharp
var id = DateTime.UtcNow.ToString(TimeFmt, System.Globalization.CultureInfo.InvariantCulture);
```

- Fire-and-forget safe call (without swallowing exceptions)
```csharp
_ = RunSnapAsync(id).ContinueWith(t => Debug.LogException(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
```


## Inspector usage

1. Add `ImageUploadButton` to a UI controller GameObject.
2. Drag `ImageUploader` into its `uploader` field.
3. Bind `ImageUploadButton.Snap` to your `Button.onClick`.
4. Optionally bind UI to `IsUploadEnd` for status indication.

## Notes

- Supports multiple rapid clicks: each click creates a distinct timestamp ID and runs concurrently; `IsUploadEnd` becomes true only when all snaps complete.
- No disk writes or extra features beyond invoking existing uploader.
- Exceptions are not silently swallowed; they are surfaced via `Debug.LogException`.

### To-dos

- [ ] Create MonoBehaviour ImageUploader in Assets/Scripts/RecordSender/ImageUploader.cs
- [ ] Add SerializeField fields, property ExpId, and validation guards
- [ ] Implement camera and RenderTexture capture to Texture2D (in-memory)
- [ ] Implement async PNG encode and multipart POST per image sequentially
- [ ] Add XML doc/comments and example usage, ensure no disk writes