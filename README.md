## Toxicity Reporter (Windows App)

WinUI 3 desktop app that records system audio + microphone + screen, keeps a rolling buffer, and uploads a flagged clip to the API.

### Run

- Open `WinUI App/WinUI App.sln` in Visual Studio 2022
- Build/Run (x64)

### Config

Create `WinUI App/WinUI App/appsettings.json`:

```json
{
  "SupabaseUrl": "https://YOUR_PROJECT.supabase.co",
  "SupabaseAnonKey": "YOUR_ANON_KEY",
  "WorkerUrl": "http://localhost:8787"
}
```

Do not commit real keys.


