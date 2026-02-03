# Toxicity Reporter - Complete Technical Documentation

This document provides comprehensive technical details of the Toxicity Reporter system implementation, covering architecture, workflows, implementation decisions, and every component of the codebase.

## Table of Contents
1. [System Architecture Overview](#system-architecture-overview)
2. [Technology Stack](#technology-stack)
3. [Authentication System](#authentication-system)
4. [Audio Capture Implementation](#audio-capture-implementation)
5. [Video Capture Implementation](#video-capture-implementation)
6. [Upload Workflow](#upload-workflow)
7. [Server Implementation](#server-implementation)
8. [Database Schema](#database-schema)
9. [Security Model](#security-model)
10. [Data Flow](#data-flow)
11. [API Endpoints](#api-endpoints)
12. [Configuration Management](#configuration-management)
13. [Error Handling](#error-handling)
14. [File Management](#file-management)

---

## System Architecture Overview

The system consists of three main components:

### Component 1: WinUI 3 Desktop Application (App)
- **Language**: C# (.NET 8.0)
- **Framework**: WinUI 3 (Windows App SDK 1.8)
- **Platform**: Windows 10 (Build 17763+) and Windows 11
- **Purpose**: Capture audio/video, manage user authentication, submit reports

### Component 2: Cloudflare Worker (Server)
- **Language**: TypeScript
- **Runtime**: Cloudflare Workers (V8 isolates, serverless)
- **Framework**: Hono (lightweight HTTP framework)
- **Purpose**: API gateway, JWT verification, orchestrate uploads to Supabase

### Component 3: Supabase (Backend)
- **Authentication**: Supabase Auth (PostgreSQL-backed, JWT tokens)
- **Database**: PostgreSQL (managed by Supabase)
- **Storage**: Supabase Storage (S3-compatible object storage)
- **Purpose**: User management, data persistence, file storage

### Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                    WinUI 3 Desktop App                      │
│  ┌────────────┐  ┌──────────────┐  ┌───────────────────┐  │
│  │   Views    │  │   Services   │  │     Models        │  │
│  │ LoginPage  │  │ AuthService  │  │  AuthModels       │  │
│  │ MainPage   │  │ ReportsAPI   │  │  ReportModels     │  │
│  │            │  │ CaptureServ  │  │                   │  │
│  └────────────┘  └──────────────┘  └───────────────────┘  │
└─────────────────────────────────────────────────────────────┘
         │                    │                    │
         │ Auth               │ Report Init       │ Upload Files
         ▼                    ▼                    ▼
┌──────────────────┐  ┌──────────────────┐  ┌─────────────────┐
│  Supabase Auth   │  │ Cloudflare       │  │ Supabase        │
│  REST API        │  │ Worker           │  │ Storage         │
│                  │  │ (Hono Router)    │  │ (S3-like)       │
│  /auth/signup    │  │                  │  │                 │
│  /auth/token     │  │ POST /reports/   │  │ reports-audio/  │
│                  │  │      init        │  │ reports-video/  │
│ Returns JWT      │  │ POST /reports/   │  │                 │
│                  │  │      complete    │  │ Signed URLs     │
└──────────────────┘  └──────────────────┘  └─────────────────┘
                               │
                               ▼
                      ┌──────────────────┐
                      │ Supabase         │
                      │ PostgreSQL       │
                      │                  │
                      │ Table: reports   │
                      │   - id (uuid)    │
                      │   - user_id      │
                      │   - audio_path   │
                      │   - video_path   │
                      │   - metadata     │
                      └──────────────────┘
```

---

## Technology Stack

### WinUI 3 App Dependencies

**Core Framework:**
- `Microsoft.WindowsAppSDK` 1.8.260101001 - Windows App SDK runtime
- `Microsoft.Windows.SDK.BuildTools` 10.0.26100.7463 - Build tooling

**Audio Capture:**
- `NAudio` 2.2.1 - Main audio library
  - Provides `WasapiLoopbackCapture` for system audio
  - Provides `WaveInEvent` for microphone
  - Handles WAV file writing
- `NAudio.WinMM` 2.2.1 - Windows Multimedia extensions

**Video Capture:**
- `SharpAvi` 3.0.0 - AVI file creation and encoding
  - Motion JPEG compression support
  - Video stream management
- `System.Drawing.Common` 8.0.0 - Bitmap manipulation
- `System.Windows.Forms` 4.0.0 - Screen bounds detection

**Built-in .NET:**
- `System.Net.Http` - HTTP client for API calls
- `System.Text.Json` - JSON serialization
- `System.IO` - File operations

### Server Dependencies

**Framework:**
- `hono` 4.11.7 - Lightweight web framework
  - Routes: `/health`, `/reports/init`, `/reports/complete`
  - Middleware support
  - CORS handling

**Authentication:**
- `jose` 5.10.0 - JWT verification library
  - JWKS (JSON Web Key Set) fetching
  - Token signature verification
  - Claims extraction

**Database & Storage:**
- `@supabase/supabase-js` 2.93.3 - Supabase client
  - PostgreSQL interactions
  - Storage signed URL generation
  - Service role authentication

**Runtime:**
- Cloudflare Workers - V8 isolates
  - Global edge network
  - Serverless execution
  - No cold starts (pre-warmed)

### Infrastructure

**Supabase Components:**
- **Auth Service**: User management, JWT token generation
- **PostgreSQL**: Relational database for reports table
- **Storage**: S3-compatible object storage with signed URLs
- **API**: RESTful endpoints for auth and data operations

**Cloudflare:**
- **Workers**: Serverless compute at the edge
- **Secrets**: Environment variable management (encrypted)
- **Global Network**: Low-latency API access worldwide

---

## Authentication System

### Authentication Flow (Step-by-Step)

#### 1. User Signup
**Client (WinUI App):**
```
User enters email + password
  ↓
SupabaseAuthService.SignUpAsync(email, password)
  ↓
HTTP POST to: {SUPABASE_URL}/auth/v1/signup
Headers:
  - apikey: {SUPABASE_ANON_KEY}
  - Content-Type: application/json
Body:
  {
    "email": "user@example.com",
    "password": "password123"
  }
```

**Supabase Response:**
```json
{
  "access_token": "eyJhbGciOiJIUzI1NiIs...",
  "token_type": "bearer",
  "expires_in": 3600,
  "refresh_token": "...",
  "user": {
    "id": "uuid-of-user",
    "email": "user@example.com",
    "created_at": "2026-02-01T..."
  }
}
```

**What Happens:**
- Supabase creates user record in `auth.users` table
- Returns JWT access token (expires in 1 hour)
- App stores token in memory (`SupabaseAuthService._accessToken`)
- App stores user info (`SupabaseAuthService._currentUser`)
- Navigates to MainPage

#### 2. User Login
**Client (WinUI App):**
```
User enters email + password
  ↓
SupabaseAuthService.LoginAsync(email, password)
  ↓
HTTP POST to: {SUPABASE_URL}/auth/v1/token?grant_type=password
Headers:
  - apikey: {SUPABASE_ANON_KEY}
  - Content-Type: application/json
Body:
  {
    "email": "user@example.com",
    "password": "password123"
  }
```

**Same response structure as signup**, stored in memory.

#### 3. Token Usage
Every API call to the Cloudflare Worker includes:
```
Authorization: Bearer {access_token}
```

The Worker verifies this token before processing requests.

### JWT Token Structure

The access token is a JWT with these claims:
```json
{
  "sub": "uuid-of-user",          // User ID (subject)
  "email": "user@example.com",
  "role": "authenticated",
  "iss": "https://project-id.supabase.co/auth/v1",  // Issuer
  "aud": "authenticated",          // Audience
  "exp": 1234567890,              // Expiration timestamp
  "iat": 1234567800               // Issued at timestamp
}
```

**Key Point**: The `sub` claim contains the user's UUID, which the server extracts and uses as `user_id` when inserting reports.

### Token Storage Strategy

**Current Implementation (In-Memory):**
- Token stored in `SupabaseAuthService` instance field
- Lost when app closes
- User must re-login after restart

**Why This Approach:**
- Simple for current scope
- No persistent credential security concerns
- Suitable for development

**Production Alternatives (Not Implemented):**
- Windows Credential Locker (`PasswordVault` API)
- DPAPI (Data Protection API) with file storage
- Token refresh logic (auto-refresh before expiry)

---

## Audio Capture Implementation

### Overview

The app captures two audio streams simultaneously:
1. **System Audio** (loopback): Everything the computer outputs (games, Discord, music, etc.)
2. **Microphone Audio**: User's voice input

Both use Windows WASAPI (Windows Audio Session API) through NAudio library.

### System Audio Capture (Loopback)

**Technology: NAudio WasapiLoopbackCapture**

```csharp
// Initialize capture
_systemAudioCapture = new WasapiLoopbackCapture();

// Get the audio format (provided by Windows)
var systemFormat = _systemAudioCapture.WaveFormat;
// Typical: 44.1kHz or 48kHz, stereo, 16-bit or 32-bit float

// Create WAV writer
_systemAudioWriter = new WaveFileWriter(_tempSystemAudioPath, systemFormat);

// Subscribe to data events
_systemAudioCapture.DataAvailable += SystemAudio_DataAvailable;

// Start recording
_systemAudioCapture.StartRecording();
```

**How It Works:**
- WASAPI Loopback captures from the default audio rendering device
- Windows continuously provides audio buffers (typically 10-100ms chunks)
- Each buffer is written to the WAV file
- File grows continuously during recording

**Data Handling:**
```csharp
private void SystemAudio_DataAvailable(object? sender, WaveInEventArgs e)
{
    // e.Buffer contains audio samples
    // e.BytesRecorded indicates how many bytes are valid
    
    // Write to file
    _systemAudioWriter.Write(e.Buffer, 0, e.BytesRecorded);
    _systemAudioWriter.Flush();
    
    // Also add to rolling buffer (for 10-second window tracking)
    lock (_bufferLock)
    {
        _audioBuffer.Enqueue(new AudioSegment
        {
            Data = e.Buffer.Take(e.BytesRecorded).ToArray(),
            Timestamp = DateTime.UtcNow
        });
        
        // Remove segments older than 10 seconds
        while (_audioBuffer.Count > 0)
        {
            var oldest = _audioBuffer.Peek();
            if ((DateTime.UtcNow - oldest.Timestamp).TotalSeconds > 10)
            {
                _audioBuffer.Dequeue();
            }
            else break;
        }
    }
}
```

### Microphone Capture

**Technology: NAudio WaveInEvent**

```csharp
// Initialize capture
_microphoneCapture = new WaveInEvent
{
    WaveFormat = new WaveFormat(44100, 2),  // 44.1kHz, stereo
    BufferMilliseconds = 100                 // 100ms buffers
};

// Create WAV writer
_microphoneWriter = new WaveFileWriter(_tempMicrophonePath, _microphoneCapture.WaveFormat);

// Subscribe to data events
_microphoneCapture.DataAvailable += Microphone_DataAvailable;

// Start recording
_microphoneCapture.StartRecording();
```

**How It Works:**
- WaveInEvent uses Windows waveIn APIs to capture from default microphone
- Samples at 44.1kHz (CD quality), stereo
- Buffers delivered every 100ms
- Written to separate WAV file

**Why Separate Files:**
- System audio and microphone have different sample rates/formats
- Easier to analyze separately (user voice vs game audio)
- Server can process/analyze them independently

### Audio File Format (WAV)

**Structure:**
```
WAV File = RIFF Header + Format Chunk + Data Chunk

RIFF Header:
- ChunkID: "RIFF"
- ChunkSize: file size - 8
- Format: "WAVE"

Format Chunk:
- AudioFormat: 1 (PCM) or 3 (IEEE float)
- NumChannels: 2 (stereo)
- SampleRate: 44100 or 48000
- BitsPerSample: 16 or 32
- ByteRate: SampleRate * NumChannels * BitsPerSample/8

Data Chunk:
- Raw PCM samples (uncompressed)
```

**Why WAV:**
- Uncompressed = maximum quality
- Simple format = no encoding overhead
- Universal playback support
- Fast to write during capture

**File Sizes:**
- 10 seconds @ 44.1kHz stereo 16-bit: ~1.7 MB
- System + microphone: ~3.4 MB total

### Rolling Buffer Concept

**Purpose**: Keep only the last 10 seconds of audio in memory for potential flagging.

**Implementation:**
```csharp
// Queue data structure
private readonly Queue<AudioSegment> _audioBuffer = new();

class AudioSegment
{
    public byte[] Data { get; set; }      // Audio samples
    public DateTime Timestamp { get; set; } // When captured
}

// On each audio buffer received:
1. Add new segment to queue with current timestamp
2. Check oldest segment in queue
3. If oldest is > 10 seconds old, remove it
4. Repeat until all segments are within 10-second window
```

**Why Queue:**
- FIFO (First In, First Out) matches rolling buffer semantics
- Efficient enqueue/dequeue operations
- Easy to check age of oldest segment

**Thread Safety:**
- Audio callbacks happen on background threads
- Queue access protected by `lock (_bufferLock)`
- Prevents race conditions between audio threads

---

## Video Capture Implementation

### Overview

Screen recording captures the entire desktop at 1920x1080 resolution, 30 frames per second, encoded as Motion JPEG AVI.

### Technology Stack

**Capture Method: GDI BitBlt**
- Windows Graphics Device Interface (GDI)
- `BitBlt` = Bit Block Transfer (fast pixel copying)
- Copies desktop surface to bitmap in memory

**Encoding: SharpAvi with Motion JPEG**
- Creates AVI container format
- MJPEG compression (each frame is a JPEG)
- Balance between file size and quality

### Screen Capture Process

**Step 1: Initialize Video Stream**
```csharp
// Create AVI writer
_aviWriter = new AviWriter(filePath)
{
    FramesPerSecond = 30,
    EmitIndex1 = true  // Create index for seeking
};

// Create MJPEG video stream
_videoStream = _aviWriter.AddMJpegWpfVideoStream(
    width: 1920,
    height: 1080,
    quality: 70  // JPEG quality (0-100)
);
```

**Step 2: Capture Loop (Background Thread)**
```csharp
while (!_stopScreenRecording)
{
    var now = DateTime.Now;
    if (now >= nextFrameTime)
    {
        CaptureScreenFrame();
        nextFrameTime = now + frameInterval; // 1/30 second
    }
    else
    {
        Thread.Sleep(10);  // Wait to reduce CPU
    }
}
```

**Step 3: Capture Single Frame**
```csharp
// Get screen dimensions
var screenBounds = Screen.PrimaryScreen.Bounds;

// Create bitmap for full screen
using var screenBitmap = new Bitmap(screenBounds.Width, screenBounds.Height);
using (var graphics = Graphics.FromImage(screenBitmap))
{
    // Get device contexts
    IntPtr desktopDC = GetDC(GetDesktopWindow());
    IntPtr memoryDC = graphics.GetHdc();
    
    // Copy desktop pixels to bitmap
    BitBlt(memoryDC, 0, 0, screenBounds.Width, screenBounds.Height, 
           desktopDC, 0, 0, SRCCOPY);
    
    // Release DCs
    graphics.ReleaseHdc(memoryDC);
    ReleaseDC(GetDesktopWindow(), desktopDC);
}

// Resize to 1920x1080 if needed
using var frameBitmap = new Bitmap(1920, 1080);
using (var g = Graphics.FromImage(frameBitmap))
{
    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
    g.DrawImage(screenBitmap, 0, 0, 1920, 1080);
}

// Convert to byte array and write
var frameData = BitmapToByteArray(frameBitmap);
_videoStream.WriteFrame(true, frameData, 0, frameData.Length);
```

### Bitmap to Byte Array Conversion

**Critical Implementation Detail:**

Bitmaps in memory have **stride** (row padding) that may not equal `width * bytes_per_pixel`.

```csharp
private byte[] BitmapToByteArray(Bitmap bitmap)
{
    var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
    
    // Lock bitmap in 32bpp format (4 bytes per pixel)
    var bitmapData = bitmap.LockBits(
        rect, 
        ImageLockMode.ReadOnly, 
        PixelFormat.Format32bppRgb
    );
    
    var stride = bitmapData.Stride;
    var width = bitmap.Width;
    var height = bitmap.Height;
    
    // Check if stride equals width * 4 (tight packing)
    if (stride == width * 4)
    {
        // No padding, can copy directly
        var length = stride * height;
        var bytes = new byte[length];
        Marshal.Copy(bitmapData.Scan0, bytes, 0, length);
        bitmap.UnlockBits(bitmapData);
        return bytes;
    }
    else
    {
        // Has padding, must copy row-by-row
        var tightBuffer = new byte[width * height * 4];
        var srcPtr = bitmapData.Scan0;
        
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(
                srcPtr + (y * stride),           // Source: row with padding
                tightBuffer,                      // Destination
                y * width * 4,                    // Dest offset (no padding)
                width * 4                         // Copy width bytes only
            );
        }
        
        bitmap.UnlockBits(bitmapData);
        return tightBuffer;
    }
}
```

**Why 32bpp (not 24bpp):**
- 24bpp often has stride padding issues
- 32bpp is naturally aligned (4-byte boundaries)
- SharpAvi MJPEG encoder accepts 32bpp directly
- Encoder handles BGR32 → JPEG conversion internally

### Video File Format (AVI)

**Container: Audio Video Interleave (AVI)**
- Microsoft multimedia container
- Supports multiple video/audio streams
- Index for seeking
- Widely compatible

**Codec: Motion JPEG (MJPEG)**
- Each frame is independently JPEG-compressed
- No inter-frame compression (unlike H.264)
- Easy to encode (no complex GOP structures)
- Good quality at moderate file sizes
- Seeking is fast (no keyframe dependencies)

**File Size Estimate:**
- 1920x1080 @ 30fps, MJPEG quality 70
- ~3-5 MB per second
- 10 seconds = ~30-50 MB

### Thread Management

**Capture Thread:**
```csharp
_screenRecordThread = new Thread(ScreenCaptureLoop)
{
    IsBackground = true,           // Dies with main thread
    Priority = ThreadPriority.BelowNormal  // Don't block UI
};
_screenRecordThread.Start();
```

**Stopping:**
```csharp
_stopScreenRecording = true;           // Signal stop
_screenRecordThread.Join(15000);       // Wait up to 15 seconds
_videoStream = null;
_aviWriter?.Close();                   // Close file
```

**Why Background Thread:**
- Screen capture is CPU-intensive
- Can't block UI thread
- Needs to run continuously at 30 FPS
- Background priority prevents UI lag

---

## Upload Workflow

### Three-Phase Upload Process

The system uses a **multi-phase upload strategy** to handle large files efficiently with Cloudflare Workers.

#### Phase 1: Initialize Report

**Client Calls:**
```
POST /reports/init
Authorization: Bearer {user_access_token}
Body: {
  "has_microphone": true,
  "has_video": true
}
```

**Server Actions:**
1. Verify JWT token
2. Extract user_id from token
3. Generate new report UUID
4. Create signed upload URLs for each file:
   - System audio: `{report_id}/system_audio.wav`
   - Microphone: `{report_id}/microphone.wav`
   - Video: `{report_id}/screen_recording.avi`
5. Return all URLs + tokens to client

**Server Response:**
```json
{
  "report_id": "uuid-here",
  "audio_upload_url": "https://...supabase.co/storage/v1/...",
  "audio_upload_token": "temp-token-1",
  "audio_path": "uuid-here/system_audio.wav",
  "microphone_upload_url": "https://...supabase.co/storage/v1/...",
  "microphone_upload_token": "temp-token-2",
  "microphone_path": "uuid-here/microphone.wav",
  "video_upload_url": "https://...supabase.co/storage/v1/...",
  "video_upload_token": "temp-token-3",
  "video_path": "uuid-here/screen_recording.avi"
}
```

**What Are Signed URLs:**
- Temporary URLs that allow direct upload to Supabase Storage
- Include authentication in the URL itself
- Expire after short period (default: 1 hour)
- Bypass Cloudflare Worker for actual file transfer
- Generated by Supabase using service role key

#### Phase 2: Upload Files Directly to Storage

**Client Actions:**
```csharp
// For each file (system audio, microphone, video):
using var fileStream = File.OpenRead(filePath);
using var content = new StreamContent(fileStream);
content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

// Upload to signed URL
var httpRequest = new HttpRequestMessage(HttpMethod.Put, signedUrl);
httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uploadToken);
httpRequest.Content = content;

var response = await httpClient.SendAsync(httpRequest);
```

**Upload Flow:**
```
Client → Supabase Storage (direct)
  |
  No Worker involvement during file transfer
  |
  Uses signed URL + temporary token
```

**Progress Tracking:**
```csharp
class ProgressableStreamContent : StreamContent
{
    // Wraps file stream
    // Reports bytes transferred
    // Calculates percentage
    // Invokes progress callback
}

// Updates UI progress bar in real-time
```

**Why Direct Upload:**
- Bypasses Cloudflare Worker 100MB request size limits
- Faster (no proxy overhead)
- Worker doesn't need to handle massive file buffers
- Reduces Worker execution time and cost

#### Phase 3: Complete Report Creation

**Client Calls:**
```
POST /reports/complete
Authorization: Bearer {user_access_token}
Body: {
  "report_id": "uuid-from-phase1",
  "description": "User's description",
  "targeted": true,
  "desired_action": "Ban player",
  "recording_start_utc": "2026-02-01T12:00:00Z",
  "flag_utc": "2026-02-01T12:01:30Z",
  "clip_start_offset_sec": 0,
  "clip_end_offset_sec": 10,
  "audio_path": "uuid/system_audio.wav",
  "microphone_path": "uuid/microphone.wav",
  "video_path": "uuid/screen_recording.avi"
}
```

**Server Actions:**
1. Verify JWT token
2. Extract user_id
3. Combine audio paths (system + microphone) into single field
4. Insert row into `reports` table:
   ```sql
   INSERT INTO reports (
     id, user_id, description, targeted, desired_action,
     recording_start_utc, flag_utc,
     clip_start_offset_sec, clip_end_offset_sec,
     audio_path, video_path, forwarded_to_modulate
   ) VALUES (...);
   ```
5. Return success with report_id

**Server Response:**
```json
{
  "ok": true,
  "report_id": "uuid-here"
}
```

### Why Three-Phase Upload

**Advantages:**
1. **Scalability**: Files uploaded directly to storage (not through Worker)
2. **Speed**: Parallel uploads possible
3. **Reliability**: Worker only orchestrates, doesn't handle large payloads
4. **Cost**: Minimal Worker execution time
5. **Progress**: Client can track each upload independently

**Disadvantages:**
1. **Complexity**: Three API calls instead of one
2. **Atomicity**: Files might upload but DB insert could fail
   - Mitigation: Orphaned files can be cleaned up by periodic job
3. **More Moving Parts**: More potential failure points

**Alternative Approaches Considered:**

**Approach 1: Single POST with base64-encoded files**
- Too large for JSON (100+ MB as base64)
- Hit memory limits
- Tried, failed with "JSON value too large" error

**Approach 2: Multipart/form-data upload through Worker**
- Cloudflare Workers has poor FormData parsing
- Hit "Content-Disposition header missing name" errors
- Tried, abandoned

**Approach 3 (Current): Three-phase with signed URLs**
- Most reliable for large files
- Standard pattern for cloud storage
- Proven to work

---

## Server Implementation

### Cloudflare Workers Architecture

**What Is Cloudflare Workers:**
- Serverless JavaScript/TypeScript runtime
- Runs on V8 isolates (not containers)
- Deployed globally to 200+ data centers
- Handles requests at edge (near user)
- No cold starts
- Execution limits: 50ms CPU time (free), up to 30 seconds wall time

**Why Cloudflare Workers:**
- Fast global distribution
- No server management
- Scales automatically
- Low cost (free tier: 100k requests/day)

### Server Code Structure

**Entry Point: `src/index.ts`**
```typescript
import { Hono } from 'hono';
import { cors } from 'hono/cors';

const app = new Hono<{ Bindings: Env }>();

// Enable CORS (required for browser/desktop app requests)
app.use('/*', cors({
  origin: '*',
  allowHeaders: ['Content-Type', 'Authorization'],
  allowMethods: ['GET', 'POST', 'PUT', 'DELETE', 'OPTIONS'],
}));

// Health check
app.get('/health', (c) => c.json({ ok: true }));

// Mount reports routes
app.route('/reports', reports);

export default app;
```

**Environment Bindings:**
```typescript
interface Env {
  SUPABASE_URL: string;               // e.g., https://abc.supabase.co
  SUPABASE_SERVICE_ROLE_KEY: string;  // Secret key for DB access
}
```

These are set via Cloudflare dashboard or `wrangler secret put`.

### JWT Verification (src/auth.ts)

**Purpose**: Verify that requests come from authenticated users.

**How It Works:**

```typescript
// Step 1: Extract token from header
const authHeader = request.headers.get('Authorization');
// Expected: "Bearer eyJhbGciOiJIUzI1NiIs..."

const token = authHeader.split(' ')[1];

// Step 2: Fetch JWKS (JSON Web Key Set) from Supabase
const jwksUrl = new URL(`${SUPABASE_URL}/auth/v1/.well-known/jwks.json`);
const jwks = createRemoteJWKSet(jwksUrl);  // jose library

// Step 3: Verify token
const { payload } = await jwtVerify(token, jwks, {
  issuer: `${SUPABASE_URL}/auth/v1`,
  audience: 'authenticated'
});

// Step 4: Extract user ID
const userId = payload.sub;  // UUID of the user
```

**JWKS Caching:**
```typescript
const jwksCache = {
  jwks: null,
  timestamp: 0
};

const CACHE_TTL = 3600000;  // 1 hour

function getJWKS(supabaseUrl) {
  const now = Date.now();
  if (!jwksCache.jwks || (now - jwksCache.timestamp) > CACHE_TTL) {
    jwksCache.jwks = createRemoteJWKSet(jwksUrl);
    jwksCache.timestamp = now;
  }
  return jwksCache.jwks;
}
```

**Why Cache JWKS:**
- JWKS changes rarely (only when keys are rotated)
- Reduces external HTTP requests
- Improves latency
- Reduces Supabase Auth API load

**Token Verification Checks:**
1. **Signature**: Cryptographically signed by Supabase
2. **Expiration**: Token not expired (exp claim)
3. **Issuer**: Token issued by correct Supabase project
4. **Audience**: Token intended for 'authenticated' audience

**Middleware Pattern:**
```typescript
export async function authMiddleware(c: Context, next: Function) {
  const authHeader = c.req.header('Authorization');
  const { userId } = await verifyToken(authHeader, c.env.SUPABASE_URL);
  
  // Store in context for route handlers
  c.set('userId', userId);
  
  // Continue to route handler
  await next();
}

// Usage in routes:
reports.post('/init', authMiddleware, async (c) => {
  const userId = c.get('userId');  // Available here
  // ... handle request
});
```

### Routes: src/routes/reports.ts

**POST /reports/init**

**Purpose**: Initialize report and generate signed upload URLs.

**Implementation:**
```typescript
reports.post('/init', authMiddleware, async (c) => {
  const userId = c.get('userId');
  const body = await c.req.json();
  
  // Generate report UUID
  const reportId = crypto.randomUUID();
  
  // Create Supabase client with service role key
  const supabase = createClient(
    c.env.SUPABASE_URL, 
    c.env.SUPABASE_SERVICE_ROLE_KEY,
    {
      auth: { persistSession: false }
    }
  );
  
  // Generate signed upload URL for system audio
  const audioPath = `${reportId}/system_audio.wav`;
  const { data: audioSigned } = await supabase.storage
    .from('reports-audio')
    .createSignedUploadUrl(audioPath);
  
  // Repeat for microphone (if requested)
  // Repeat for video (if requested)
  
  return c.json({
    report_id: reportId,
    audio_upload_url: audioSigned.signedUrl,
    audio_upload_token: audioSigned.token,
    audio_path: audioPath,
    // ... other URLs
  });
});
```

**Supabase Storage Signed Upload URLs:**
- Generated by: `storage.from(bucket).createSignedUploadUrl(path)`
- Returns: `{ signedUrl, token, path }`
- Client uploads to `signedUrl` with `Authorization: Bearer {token}`
- Expires after configured time (default: 1 hour)
- Allows direct client → Storage upload without exposing service role key

**POST /reports/complete**

**Purpose**: Insert database row after files are uploaded.

**Implementation:**
```typescript
reports.post('/complete', authMiddleware, async (c) => {
  const userId = c.get('userId');
  const body = await c.req.json();
  
  // Combine audio paths (system + mic)
  const audioPaths = [body.audio_path, body.microphone_path]
    .filter(Boolean)
    .join(',');
  
  const reportData = {
    id: body.report_id,
    user_id: userId,  // From verified JWT
    description: body.description || null,
    targeted: body.targeted !== undefined ? body.targeted : null,
    desired_action: body.desired_action || null,
    recording_start_utc: body.recording_start_utc || null,
    flag_utc: body.flag_utc || null,
    clip_start_offset_sec: body.clip_start_offset_sec,
    clip_end_offset_sec: body.clip_end_offset_sec,
    audio_path: audioPaths || null,
    video_path: body.video_path || null,
    forwarded_to_modulate: false
  };
  
  const { error } = await supabase
    .from('reports')
    .insert(reportData);
  
  if (error) {
    return c.json({ error: 'Failed to create report' }, 500);
  }
  
  return c.json({ ok: true, report_id: body.report_id }, 201);
});
```

**Why Service Role Key:**
- Row-Level Security (RLS) is enabled on `reports` table
- No policies allow client access
- Service role bypasses RLS
- Ensures only server can write to database
- User cannot forge user_id (extracted from verified JWT)

### Supabase Client Initialization

```typescript
const supabase = createClient(supabaseUrl, serviceRoleKey, {
  auth: {
    autoRefreshToken: false,  // Don't auto-refresh (server doesn't need it)
    persistSession: false     // Don't persist (stateless Workers)
  }
});
```

**Service Role vs Anon Key:**

**Anon Key (used by app for auth only):**
- Public key (safe to expose)
- Respects RLS policies
- Limited permissions
- Used for: signup, login

**Service Role Key (used by server only):**
- Secret key (never exposed to client)
- Bypasses all RLS policies
- Full database access
- Used for: inserting reports, generating signed URLs

---

## Database Schema

### Reports Table

**SQL Definition:**
```sql
create table reports (
  id uuid primary key default gen_random_uuid(),
  created_at timestamptz default now(),
  user_id uuid not null references auth.users(id),
  
  -- Report metadata
  description text,
  targeted boolean,
  desired_action text,
  
  -- Timing information
  recording_start_utc timestamptz,
  flag_utc timestamptz,
  clip_start_offset_sec numeric,
  clip_end_offset_sec numeric,
  
  -- File references
  audio_path text,
  video_path text,
  
  -- Processing status
  forwarded_to_modulate boolean default false
);

-- Indexes for performance
create index idx_reports_user_id on reports(user_id);
create index idx_reports_created_at on reports(created_at);

-- Enable RLS (Row Level Security)
alter table reports enable row level security;
```

**Field Explanations:**

- `id`: Primary key, UUID v4
- `created_at`: Automatic timestamp when row is inserted
- `user_id`: Foreign key to `auth.users` (Supabase Auth table)
  - Enforces referential integrity
  - Ensures every report has a valid user
- `description`: User-provided description of incident
- `targeted`: Boolean flag - was user personally targeted
- `desired_action`: What user wants to happen (review, ban, etc.)
- `recording_start_utc`: When recording began (ISO 8601 UTC)
- `flag_utc`: When user flagged the event (ISO 8601 UTC)
- `clip_start_offset_sec`: Start of clip relative to recording start (usually 0)
- `clip_end_offset_sec`: End of clip relative to recording start (usually 10)
- `audio_path`: Comma-separated paths to audio files in Storage
  - Example: `uuid/system_audio.wav,uuid/microphone.wav`
- `video_path`: Path to video file in Storage
  - Example: `uuid/screen_recording.avi`
- `forwarded_to_modulate`: Processing flag for external system integration

**Indexes:**
- `idx_reports_user_id`: Fast lookup of all reports by specific user
- `idx_reports_created_at`: Fast sorting/filtering by date

**Row-Level Security (RLS):**
- Enabled but **no policies defined**
- Result: Clients cannot read or write directly
- Only service role can access
- All access must go through Worker

### Storage Buckets

**reports-audio:**
- Stores WAV files
- Private (not publicly accessible)
- Structure: `{report_id}/filename.wav`
- Content types: `audio/wav`

**reports-video:**
- Stores AVI files
- Private (not publicly accessible)
- Structure: `{report_id}/screen_recording.avi`
- Content types: `video/x-msvideo`

**File Organization:**
```
reports-audio/
  └── {report-uuid-1}/
      ├── system_audio.wav
      └── microphone.wav
  └── {report-uuid-2}/
      ├── system_audio.wav
      └── microphone.wav

reports-video/
  └── {report-uuid-1}/
      └── screen_recording.avi
  └── {report-uuid-2}/
      └── screen_recording.avi
```

**Access Control:**
- Buckets are private by default
- Access requires:
  - Service role key (server), or
  - Signed URL with temporary token (client during upload)
- No public URLs or anonymous access

---

## Security Model

### Defense in Depth Strategy

**Layer 1: Client Authentication**
- User must sign up/login with email + password
- Supabase Auth handles credential verification
- Password requirements enforced (min 6 characters)
- Returns JWT token on successful auth

**Layer 2: JWT Verification at Server**
- Every API call requires `Authorization: Bearer {token}`
- Worker verifies token cryptographically using JWKS
- Token signature checked against Supabase's public keys
- Expiration, issuer, and audience validated
- Invalid tokens = 401 Unauthorized response

**Layer 3: User ID from Verified Token**
- User ID extracted from JWT `sub` claim
- Server inserts this user_id into database
- Client cannot forge user_id (would require forging JWT)
- Reports are always associated with authenticated user

**Layer 4: Database RLS**
- Row-Level Security enabled on `reports` table
- No client policies (client has zero DB access)
- Only server (using service role) can write
- Prevents direct database manipulation

**Layer 5: Private Storage Buckets**
- Storage buckets are private
- Files not accessible without authentication
- Signed URLs expire after use
- No directory listing or enumeration

### Credential Storage

**Client Side:**
- Supabase anon key: Stored in `appsettings.json` (not secret, public key)
- Access token: Stored in memory only
  - Lost on app close
  - Not persisted to disk
  - Not in Windows Registry
- User password: Never stored (sent only during auth)

**Server Side:**
- Service role key: Stored as Cloudflare secret
  - Encrypted at rest
  - Injected at runtime as environment variable
  - Never logged or exposed
- No API keys in code or config files

### Attack Vectors & Mitigations

**Attack: Stolen JWT token**
- Mitigation: Tokens expire after 1 hour
- Impact: Limited window of access
- Future: Implement token refresh, revocation

**Attack: Man-in-the-middle (MITM)**
- Mitigation: All communication over HTTPS/TLS
- Supabase Auth: HTTPS only
- Cloudflare Worker: HTTPS enforced
- Storage uploads: HTTPS only

**Attack: Replay attack**
- Mitigation: JWT has expiration time (exp claim)
- Old tokens rejected automatically
- Nonce not implemented (could be added)

**Attack: Forged user_id**
- Mitigation: user_id extracted from verified JWT
- Client cannot specify user_id in request
- JWT signature prevents tampering

**Attack: Direct database access**
- Mitigation: RLS enabled, no client policies
- Service role key never exposed to client
- All writes go through Worker

**Attack: Storage enumeration**
- Mitigation: Buckets are private
- No list/browse endpoints exposed
- Signed URLs are file-specific

---

## Data Flow

### Complete Report Submission Flow

**Timeline of a Report Submission:**

```
T=0:00  User clicks "Start Recording"
          ↓
        Audio capture starts (WASAPI loopback + mic)
        Video capture starts (GDI BitBlt loop @ 30fps)
        Rolling 10-second buffer active
        Files written to:
          - C:\Users\...\TempState\system_audio_20260201_120000.wav
          - C:\Users\...\TempState\microphone_20260201_120000.wav
          - C:\Users\...\TempState\screen_recording_20260201_120000.avi

T=0:10  (10 seconds of recording accumulated)

T=0:15  User presses "Flag Event & Upload"
          ↓
        All capture stops
        Files finalized (writers closed)
        Upload dialog appears
        
T=0:20  User enters description, checks "targeted", enters desired action
        User clicks "Submit Report"
          ↓
        [PHASE 1: INITIALIZE]
        Client → Worker: POST /reports/init
          {
            has_microphone: true,
            has_video: true
          }
          ↓
        Worker verifies JWT token
        Worker extracts user_id from token
        Worker generates report UUID
        Worker → Supabase Storage: Create signed upload URLs (x3)
          ↓
        Worker → Client: Response
          {
            report_id: "abc-123",
            audio_upload_url: "https://...",
            audio_upload_token: "temp-token-1",
            microphone_upload_url: "https://...",
            microphone_upload_token: "temp-token-2",
            video_upload_url: "https://...",
            video_upload_token: "temp-token-3",
            paths: {...}
          }
        
T=0:21  [PHASE 2: UPLOAD FILES]
        Client reads local files
        
        Upload 1: System Audio
        Client → Supabase Storage:
          PUT {audio_upload_url}
          Authorization: Bearer {audio_upload_token}
          Content-Type: audio/wav
          Body: (1.7 MB WAV file stream)
          ↓
        Progress: 0% → 25% → 50% → 75% → 100%
        
T=0:23  Upload 2: Microphone
        Client → Supabase Storage:
          PUT {microphone_upload_url}
          Authorization: Bearer {microphone_upload_token}
          Body: (1.7 MB WAV file stream)
          ↓
        Progress: 0% → 100%
        
T=0:25  Upload 3: Video
        Client → Supabase Storage:
          PUT {video_upload_url}
          Authorization: Bearer {video_upload_token}
          Content-Type: video/x-msvideo
          Body: (40 MB AVI file stream)
          ↓
        Progress: 0% → 10% → 20% → ... → 100%
        
T=0:40  [PHASE 3: COMPLETE REPORT]
        All files uploaded successfully
        
        Client → Worker: POST /reports/complete
          {
            report_id: "abc-123",
            description: "Player was toxic in voice chat",
            targeted: true,
            desired_action: "Ban player",
            recording_start_utc: "2026-02-01T12:00:00Z",
            flag_utc: "2026-02-01T12:00:15Z",
            clip_start_offset_sec: 0,
            clip_end_offset_sec: 10,
            audio_path: "abc-123/system_audio.wav",
            microphone_path: "abc-123/microphone.wav",
            video_path: "abc-123/screen_recording.avi"
          }
          ↓
        Worker verifies JWT token
        Worker extracts user_id
        Worker → Supabase DB:
          INSERT INTO reports (
            id, user_id, description, targeted, desired_action,
            recording_start_utc, flag_utc, clip_start_offset_sec,
            clip_end_offset_sec, audio_path, video_path
          ) VALUES (
            'abc-123', 'user-uuid', 'Player was toxic...',
            true, 'Ban player', '2026-02-01T12:00:00Z',
            '2026-02-01T12:00:15Z', 0, 10,
            'abc-123/system_audio.wav,abc-123/microphone.wav',
            'abc-123/screen_recording.avi'
          );
          ↓
        Worker → Client: Response
          {
            ok: true,
            report_id: "abc-123"
          }
        
T=0:41  Client shows success message
        Client deletes temporary files
        Client resets UI to "Not recording" state
        
        Report is now fully submitted and available in Supabase!
```

### Data Persistence Locations

**During Recording (Temporary):**
- Local disk: `C:\Users\{user}\AppData\Local\Packages\{app-id}\TempState\`
  - `system_audio_{timestamp}.wav`
  - `microphone_{timestamp}.wav`
  - `screen_recording_{timestamp}.avi`

**After Upload (Permanent):**
- Supabase Storage:
  - `reports-audio/{report-id}/system_audio.wav`
  - `reports-audio/{report-id}/microphone.wav`
  - `reports-video/{report-id}/screen_recording.avi`
- Supabase PostgreSQL:
  - Row in `reports` table with metadata + file paths

**Cleanup:**
- Temporary files deleted after successful upload
- On app close, temp folder may retain files (Windows handles cleanup)

---

## API Endpoints

### GET /health

**Purpose**: Health check to verify Worker is running.

**Request:**
```http
GET /health HTTP/1.1
Host: localhost:8787
```

**Response:**
```http
HTTP/1.1 200 OK
Content-Type: application/json

{"ok":true}
```

**No authentication required.**

### POST /reports/init

**Purpose**: Initialize report and get signed upload URLs.

**Request:**
```http
POST /reports/init HTTP/1.1
Host: localhost:8787
Authorization: Bearer eyJhbGciOiJIUzI1NiIs...
Content-Type: application/json

{
  "has_microphone": true,
  "has_video": true
}
```

**Response:**
```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "report_id": "550e8400-e29b-41d4-a716-446655440000",
  "audio_upload_url": "https://project.supabase.co/storage/v1/upload/...",
  "audio_upload_token": "temp-signed-token-1",
  "audio_path": "550e8400.../system_audio.wav",
  "microphone_upload_url": "https://...",
  "microphone_upload_token": "temp-signed-token-2",
  "microphone_path": "550e8400.../microphone.wav",
  "video_upload_url": "https://...",
  "video_upload_token": "temp-signed-token-3",
  "video_path": "550e8400.../screen_recording.avi"
}
```

**Authentication: Required (Bearer token)**

**Processing:**
1. Middleware verifies JWT
2. Extract user_id (not used in init, but validated)
3. Generate report_id (UUID v4)
4. For each requested file type:
   - Construct storage path
   - Call `storage.from(bucket).createSignedUploadUrl(path)`
   - Collect URL + token
5. Return all signed URLs

**Error Responses:**
- 401: Invalid or missing token
- 500: Failed to create signed URLs (Supabase error)

### POST /reports/complete

**Purpose**: Insert database row after files uploaded.

**Request:**
```http
POST /reports/complete HTTP/1.1
Host: localhost:8787
Authorization: Bearer eyJhbGciOiJIUzI1NiIs...
Content-Type: application/json

{
  "report_id": "550e8400-e29b-41d4-a716-446655440000",
  "description": "Player used racial slurs in voice chat",
  "targeted": true,
  "desired_action": "Permanent ban",
  "recording_start_utc": "2026-02-01T15:30:00Z",
  "flag_utc": "2026-02-01T15:30:45Z",
  "clip_start_offset_sec": 0,
  "clip_end_offset_sec": 10,
  "audio_path": "550e8400.../system_audio.wav",
  "microphone_path": "550e8400.../microphone.wav",
  "video_path": "550e8400.../screen_recording.avi"
}
```

**Response:**
```http
HTTP/1.1 201 Created
Content-Type: application/json

{
  "ok": true,
  "report_id": "550e8400-e29b-41d4-a716-446655440000"
}
```

**Authentication: Required (Bearer token)**

**Processing:**
1. Middleware verifies JWT
2. Extract user_id from token
3. Parse request body
4. Combine audio paths (system + mic) with comma separator
5. Construct report data object with user_id
6. Insert into PostgreSQL via Supabase client
7. Return success

**Error Responses:**
- 401: Invalid or missing token
- 400: Missing required fields
- 500: Database insertion failed

**Critical Security Note:**
The `user_id` comes from the verified JWT, not from the request body. The client cannot specify or forge the user_id.

---

## Configuration Management

### Client Configuration (appsettings.json)

**Location:** `App/WinUI App/WinUI App/appsettings.json`

**Structure:**
```json
{
  "SupabaseUrl": "https://project-id.supabase.co",
  "SupabaseAnonKey": "ANON_KEY_HERE",
  "WorkerUrl": "http://localhost:8787"
}
```

**Loading Mechanism:**
```csharp
public class AppConfig
{
    // Singleton pattern
    private static AppConfig? _instance;
    private static readonly object _lock = new object();
    
    public static AppConfig Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = LoadConfiguration();
                    }
                }
            }
            return _instance;
        }
    }
    
    private static AppConfig LoadConfiguration()
    {
        var appDirectory = AppContext.BaseDirectory;
        var configPath = Path.Combine(appDirectory, "appsettings.json");
        
        if (File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<AppConfig>(json);
        }
        
        // Return defaults if file missing
        return new AppConfig { ... };
    }
}
```

**Usage:**
```csharp
var supabaseUrl = AppConfig.Instance.SupabaseUrl;
var workerUrl = AppConfig.Instance.WorkerUrl;
```

**Build Configuration:**
In `WinUI App.csproj`:
```xml
<ItemGroup>
  <None Update="appsettings.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

This ensures `appsettings.json` is copied to output directory during build.

### Server Configuration (.dev.vars)

**Local Development:**
File: `Server/.dev.vars` (gitignored)
```
SUPABASE_URL=https://project-id.supabase.co
SUPABASE_SERVICE_ROLE_KEY=eyJhbGc...
```

Wrangler automatically loads this file during `wrangler dev`.

**Production (Cloudflare Secrets):**
```bash
wrangler secret put SUPABASE_URL
# Enter value when prompted

wrangler secret put SUPABASE_SERVICE_ROLE_KEY
# Enter value when prompted
```

Secrets stored encrypted in Cloudflare's infrastructure, injected at runtime.

**Access in Code:**
```typescript
export default {
  async fetch(request: Request, env: Env) {
    const supabaseUrl = env.SUPABASE_URL;
    const serviceKey = env.SUPABASE_SERVICE_ROLE_KEY;
    // ... use these values
  }
}
```

---

## Error Handling

### Client-Side Error Handling

**Network Errors:**
```csharp
try
{
    var response = await httpClient.SendAsync(request);
    var content = await response.Content.ReadAsStringAsync();
    
    if (!response.IsSuccessStatusCode)
    {
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(content);
        return (false, "", error?.Error ?? "Unknown error");
    }
    
    // Process success response
}
catch (HttpRequestException ex)
{
    return (false, "", $"Network error: {ex.Message}");
}
catch (TaskCanceledException ex)
{
    return (false, "", "Request timeout");
}
catch (Exception ex)
{
    return (false, "", $"Error: {ex.Message}");
}
```

**User Feedback:**
All errors shown via `InfoBar` UI component:
- `InfoBarSeverity.Error` - Red, for failures
- `InfoBarSeverity.Warning` - Yellow, for validation issues
- `InfoBarSeverity.Success` - Green, for success
- `InfoBarSeverity.Informational` - Blue, for status updates

**Error Messages Shown:**
- "Not authenticated. Log in again." - Token expired/invalid
- "No audio data captured." - Capture failed
- "Failed to submit report: {details}" - Upload/API errors
- "Network error: {exception}" - Connection issues

### Server-Side Error Handling

**Global Error Handler:**
```typescript
app.onError((err, c) => {
  console.error('Server error:', err);
  return c.json({ 
    error: 'Internal server error',
    message: err.message 
  }, 500);
});
```

**Specific Error Cases:**

**Missing Authorization:**
```typescript
if (!authHeader) {
  return c.json({ error: 'Missing Authorization header' }, 401);
}
```

**Invalid JWT:**
```typescript
try {
  await jwtVerify(token, jwks, options);
} catch (error) {
  return c.json({ error: 'JWT verification failed' }, 401);
}
```

**Database Errors:**
```typescript
const { error } = await supabase.from('reports').insert(data);
if (error) {
  console.error('Database error:', error);
  return c.json({ 
    error: 'Failed to create report',
    details: error.message 
  }, 500);
}
```

**Storage Errors:**
```typescript
const { data, error } = await supabase.storage
  .from('bucket')
  .createSignedUploadUrl(path);

if (error) {
  console.error('Storage error:', error);
  return c.json({ error: 'Failed to create upload URL' }, 500);
}
```

---

## File Management

### Temporary File Strategy

**Purpose**: Store captured media temporarily before upload.

**Location:**
```csharp
var tempFolder = ApplicationData.Current.TemporaryFolder;
// Resolves to: C:\Users\{user}\AppData\Local\Packages\{app-id}\TempState\
```

**Naming Convention:**
```csharp
var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
var systemAudioPath = Path.Combine(tempFolder.Path, $"system_audio_{timestamp}.wav");
var microphonePath = Path.Combine(tempFolder.Path, $"microphone_{timestamp}.wav");
var videoPath = Path.Combine(tempFolder.Path, $"screen_recording_{timestamp}.avi");
```

Example filenames:
- `system_audio_20260201_153045.wav`
- `microphone_20260201_153045.wav`
- `screen_recording_20260201_153045.avi`

**Lifecycle:**
1. **Created**: When "Start Recording" is clicked
2. **Written**: Continuously during recording
3. **Closed**: When "Flag Event" is clicked (writers disposed)
4. **Read**: During upload phase
5. **Deleted**: After successful upload or cancel

**Cleanup Implementation:**
```csharp
public void CleanupTempFiles()
{
    try
    {
        if (_tempSystemAudioPath != null && File.Exists(_tempSystemAudioPath))
        {
            File.Delete(_tempSystemAudioPath);
            _tempSystemAudioPath = null;
        }
    }
    catch { }
    
    // Repeat for microphone and video
    // Failures ignored (file might be locked)
}
```

**When Cleanup Occurs:**
- After successful report submission
- When user cancels upload
- When user logs out
- On `Dispose()` (app shutdown)

### Storage Organization

**Supabase Storage Structure:**

```
reports-audio/
  └── {report-id}/
      ├── system_audio.wav     (system loopback)
      └── microphone.wav       (mic input)

reports-video/
  └── {report-id}/
      └── screen_recording.avi (screen capture)
```

**Why Folder per Report:**
- Isolation: Each report's files grouped together
- Easy retrieval: Get all files for a report
- Easy cleanup: Delete folder to remove all files
- Scalable: No flat namespace issues

**File Paths in Database:**
```
audio_path: "uuid/system_audio.wav,uuid/microphone.wav"
video_path: "uuid/screen_recording.avi"
```

Comma-separated allows multiple audio files in single field.

**Retrieving Files:**
```typescript
// Parse audio_path
const audioPaths = report.audio_path.split(',');

// Download each file
for (const path of audioPaths) {
  const { data } = await supabase.storage
    .from('reports-audio')
    .download(path);
}
```

---

## Client-Server Communication Protocols

### HTTP Request Details

**All Requests Use HTTPS** (enforced by Supabase and Cloudflare)

**Common Headers:**
```http
Authorization: Bearer eyJhbGciOiJIUzI1NiIs...
Content-Type: application/json
Accept: application/json
```

**Client Implementation (HttpClient):**
```csharp
public class ReportsApiClient
{
    private readonly HttpClient _httpClient;
    
    public ReportsApiClient()
    {
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan  // No timeout for large uploads
        };
    }
    
    public async Task<(bool success, InitResponse? response, string error)> 
        InitReportAsync(string token, InitRequest request)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{workerUrl}/reports/init");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        httpRequest.Content = JsonContent.Create(request);
        
        var response = await _httpClient.SendAsync(httpRequest);
        // Parse response...
    }
}
```

**Why Infinite Timeout:**
- Video uploads can take minutes for large files
- Default 100-second timeout too short
- Upload shows progress, so user knows it's working
- Cancellation via CancellationToken (not timeout)

### CORS Configuration

**Server Side (Cloudflare Worker):**
```typescript
app.use('/*', cors({
  origin: '*',                          // Allow all origins
  allowHeaders: ['Content-Type', 'Authorization'],
  allowMethods: ['GET', 'POST', 'PUT', 'DELETE', 'OPTIONS'],
  maxAge: 86400,                        // Cache preflight for 1 day
}));
```

**Why CORS:**
- Browsers enforce CORS (Cross-Origin Resource Sharing)
- Desktop apps (WinUI) don't enforce CORS, but good practice
- Allows future web client
- Preflight requests (OPTIONS) handled automatically

**Production Consideration:**
Change `origin: '*'` to specific domain for security:
```typescript
origin: 'https://yourdomain.com'
```

### Upload Progress Tracking

**Custom StreamContent with Progress:**
```csharp
private class ProgressableStreamContent : StreamContent
{
    private readonly Stream _content;
    private readonly Action<long, long> _onProgress;
    
    protected override async Task SerializeToStreamAsync(
        Stream stream, 
        TransportContext? context)
    {
        var buffer = new byte[8192];
        var totalBytes = _content.Length;
        var bytesTransferred = 0L;
        int bytesRead;
        
        while ((bytesRead = await _content.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await stream.WriteAsync(buffer, 0, bytesRead);
            bytesTransferred += bytesRead;
            
            // Report progress
            _onProgress?.Invoke(bytesTransferred, totalBytes);
        }
    }
}
```

**UI Update:**
```csharp
var content = new ProgressableStreamContent(fileStream, (transferred, total) => {
    var percentage = (int)((transferred * 100) / total);
    
    DispatcherQueue.TryEnqueue(() => {
        UploadProgressBar.Value = percentage;
        UploadProgressText.Text = $"Uploading... {percentage}%";
    });
});
```

**Dispatcher Required:**
- Progress callback executes on background thread
- UI must be updated on UI thread
- `DispatcherQueue.TryEnqueue` marshals to UI thread

---

## Supabase Integration Details

### Authentication API Endpoints

**Signup:**
- Endpoint: `POST {SUPABASE_URL}/auth/v1/signup`
- Header: `apikey: {ANON_KEY}`
- Creates user in `auth.users` table
- Returns access token + user object

**Login:**
- Endpoint: `POST {SUPABASE_URL}/auth/v1/token?grant_type=password`
- Header: `apikey: {ANON_KEY}`
- Validates credentials
- Returns access token + refresh token

**Token Structure:**
- JWT signed with HMAC-SHA256
- Secret stored in Supabase (never exposed)
- Contains user_id in `sub` claim
- Expires after 1 hour (configurable in Supabase dashboard)

### JWKS (JSON Web Key Set)

**What It Is:**
Public keys used to verify JWT signatures.

**Endpoint:**
`GET {SUPABASE_URL}/auth/v1/.well-known/jwks.json`

**Response Example:**
```json
{
  "keys": [
    {
      "kty": "RSA",
      "use": "sig",
      "kid": "key-id-1",
      "n": "modulus...",
      "e": "AQAB"
    }
  ]
}
```

**How Verification Works:**
1. Worker fetches JWKS from Supabase
2. Parses JWT header to find `kid` (key ID)
3. Looks up corresponding key in JWKS
4. Verifies signature using public key
5. If signature valid and not expired, token is trusted

**Caching:**
- JWKS fetched once, cached for 1 hour
- Reduces Supabase API calls
- Keys rarely change (only on rotation)

### Database Client

**Initialization:**
```typescript
import { createClient } from '@supabase/supabase-js';

const supabase = createClient(supabaseUrl, serviceRoleKey, {
  auth: {
    autoRefreshToken: false,
    persistSession: false
  }
});
```

**Insert Operation:**
```typescript
const { data, error } = await supabase
  .from('reports')
  .insert({
    id: reportId,
    user_id: userId,
    description: 'text',
    // ... other fields
  });

if (error) {
  // Handle error
}
```

**Supabase Client Features:**
- Automatic connection pooling
- Query builder (type-safe)
- RLS-aware (honors policies or bypasses with service role)
- Error handling with structured error objects

### Storage Client

**Create Signed Upload URL:**
```typescript
const { data, error } = await supabase.storage
  .from('bucket-name')
  .createSignedUploadUrl('path/to/file.ext');

// Returns:
// data.signedUrl = "https://...?token=..."
// data.token = "temp-bearer-token"
// data.path = "path/to/file.ext"
```

**How Client Uses It:**
```csharp
var request = new HttpRequestMessage(HttpMethod.Put, data.signedUrl);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", data.token);
request.Content = new StreamContent(fileStream);

await httpClient.SendAsync(request);
```

**Download (For Future Retrieval):**
```typescript
const { data, error } = await supabase.storage
  .from('bucket-name')
  .download('path/to/file.ext');

// data is Blob/ArrayBuffer
```

---

## Code Organization and Patterns

### WinUI App Structure

**MVVM-Inspired (Not Strict MVVM):**
- **Views**: XAML + code-behind for UI
- **Services**: Business logic, API communication, capture logic
- **Models**: Data transfer objects (DTOs), plain C# classes

**Separation of Concerns:**

**Views (UI Layer):**
- `LoginPage.xaml`: Login/signup interface
- `MainPage.xaml`: Recording controls
- `UploadReportDialog.xaml`: Report submission dialog
- Responsibility: User interaction, display state, navigate

**Services (Business Logic Layer):**
- `SupabaseAuthService`: Authentication operations
- `ReportsApiClient`: HTTP communication with Worker
- `CaptureService`: Audio/video capture and file management
- Responsibility: Logic, external communication, data processing

**Models (Data Layer):**
- `AuthModels.cs`: `SignupRequest`, `LoginRequest`, `AuthResponse`
- `ReportModels.cs`: `CreateReportRequest`, `ReportInitRequest`, etc.
- Responsibility: Data structure, serialization annotations

**Configuration:**
- `AppConfig.cs`: Singleton configuration loader

**Navigation:**
```csharp
// In LoginPage after successful login:
Frame.Navigate(typeof(MainPage), _authService);

// In MainPage, receive auth service:
protected override void OnNavigatedTo(NavigationEventArgs e)
{
    if (e.Parameter is SupabaseAuthService authService)
    {
        _authService = authService;
    }
}
```

### Server Structure

**Layered Architecture:**

**Layer 1: Entry Point (index.ts)**
- Initialize Hono app
- Configure CORS
- Register routes
- Global error handler

**Layer 2: Middleware (auth.ts)**
- JWT verification
- User context injection
- Error handling for auth failures

**Layer 3: Routes (routes/reports.ts)**
- Endpoint handlers
- Request/response processing
- Business logic
- Database/Storage operations

**Layer 4: Types (types.ts)**
- TypeScript interfaces
- Type safety across codebase
- Documentation via types

**Dependency Injection Pattern:**
```typescript
// Environment passed through Hono context
const app = new Hono<{ Bindings: Env }>();

app.post('/endpoint', async (c) => {
  const supabaseUrl = c.env.SUPABASE_URL;  // Injected
  const serviceKey = c.env.SUPABASE_SERVICE_ROLE_KEY;  // Injected
});
```

---

## Performance Considerations

### Client Performance

**Audio Capture:**
- Minimal CPU usage (handled by Windows audio subsystem)
- Buffers processed as they arrive (event-driven)
- File I/O asynchronous via .NET streams

**Video Capture:**
- CPU-intensive (30 FPS screen capture + encoding)
- Background thread with `BelowNormal` priority
- Doesn't block UI thread
- MJPEG encoding done by SharpAvi (native code)

**Memory Usage:**
- Rolling audio buffer: ~10-20 MB (10 seconds of audio)
- Video writer: ~50-100 MB (frame buffer + encoding buffer)
- Total: ~100-150 MB during recording

**Upload Optimization:**
- Streaming upload (not loading entire file to memory)
- Progress tracking with 8KB buffer
- Parallel uploads possible (not currently implemented)

### Server Performance

**Cloudflare Workers Characteristics:**
- **Cold Start:** 0ms (V8 isolates pre-warmed)
- **Execution Time:** Typically < 50ms for JWT verify + DB insert
- **Memory:** Minimal (no file buffering)
- **Concurrency:** Automatic scaling (thousands of concurrent requests)

**JWKS Caching Impact:**
- Without cache: 2 HTTP requests per report (JWKS fetch + DB insert)
- With cache: 1 HTTP request per report (DB insert only)
- Latency reduction: ~100-200ms per request

**Database Connection:**
- Supabase client manages connection pooling
- Worker doesn't maintain persistent connections
- Each request: connect, query, disconnect
- Fast due to edge proximity to Supabase

---

## Development Workflow

### Local Development Setup

**Terminal 1 - Server:**
```bash
cd Server
npm install
wrangler dev
# Runs on http://localhost:8787
```

**Visual Studio - App:**
```
1. Open App/WinUI App/WinUI App.sln
2. Configure appsettings.json with real credentials
3. Press F5 (Debug) or Ctrl+F5 (Run)
```

**Testing Cycle:**
1. Start server
2. Run app
3. Login
4. Start recording
5. Flag & upload
6. Check Supabase dashboard

### Debugging

**Client Debugging:**
- Visual Studio debugger (breakpoints, watches, etc.)
- Debug output: `System.Diagnostics.Debug.WriteLine()`
- View in Visual Studio Output window

**Server Debugging:**
- Console logs: `console.log()`, `console.error()`
- View in terminal where `wrangler dev` is running
- Stack traces shown for exceptions

**Supabase Debugging:**
- Table Editor: View inserted rows in real-time
- Storage: Browse uploaded files
- Auth: View registered users
- Logs: API request logs (if enabled)

### Build Process

**WinUI App Build:**
```
1. Restore NuGet packages
2. Compile C# code to IL
3. Compile XAML to BAML
4. Copy assets (images, appsettings.json)
5. Link with Windows App SDK runtime
6. Output: .exe + .dll in bin/x64/Debug/
```

**Server Build:**
```
1. TypeScript compilation (tsc)
2. Bundling (esbuild via Wrangler)
3. Worker script generation
4. Output: Bundled JS in .wrangler/tmp/
```

### Deployment

**Server Deployment:**
```bash
cd Server
wrangler secret put SUPABASE_URL
wrangler secret put SUPABASE_SERVICE_ROLE_KEY
wrangler deploy
```

Result: Worker deployed to `https://worker-name.username.workers.dev`

**App Deployment:**
```
1. Visual Studio → Publish
2. Choose target: MSIX package
3. Sign package (code signing certificate)
4. Output: .msix installer
5. Distribute to users
```

---

## Potential Interview Questions & Answers

### Q: Why did you choose WinUI 3 instead of WPF or WinForms?

**Answer:**
WinUI 3 is Microsoft's modern UI framework for Windows desktop apps. It offers:
- Modern Fluent Design (consistent with Windows 11)
- Better performance than WPF
- XAML-based (familiar to WPF developers)
- Long-term support from Microsoft
- Mica backdrop and modern controls

WinForms is too old and limited for modern UI. WPF is mature but WinUI 3 is the future direction for Windows desktop development.

### Q: Why Cloudflare Workers instead of traditional server?

**Answer:**
Cloudflare Workers provide several advantages for this use case:
- **Serverless**: No server management, automatic scaling
- **Global**: Deployed to 200+ edge locations automatically
- **Low latency**: Requests handled near the user
- **Cost**: Free tier handles thousands of requests
- **Simplicity**: TypeScript code, no Docker/infrastructure

For a toxicity reporting system, we don't need stateful servers. Workers are perfect for API gateway + orchestration tasks.

### Q: How do you ensure the user_id cannot be forged?

**Answer:**
The user_id comes from the JWT token's `sub` claim, not from client input:

1. User logs in, Supabase issues JWT
2. JWT is cryptographically signed by Supabase
3. Worker verifies signature using Supabase's public keys (JWKS)
4. Only after successful verification, worker extracts user_id
5. This user_id is inserted into the database

The client never specifies user_id. Even if a malicious client sends a user_id in the request body, the server ignores it and uses the verified token's user_id instead.

Forging a JWT would require:
- Knowing Supabase's private signing key (impossible, never exposed)
- Breaking HMAC-SHA256 cryptography (computationally infeasible)

### Q: What happens if the upload fails midway?

**Answer:**
Current behavior:
- Files that uploaded successfully remain in Storage (orphaned)
- No database row is created (Phase 3 never happens)
- Temp files deleted on app side

Improvement for production:
- Implement cleanup job that removes Storage files without corresponding DB rows
- Add retry logic with exponential backoff
- Track upload state (partial upload table)

### Q: How do you handle multiple users uploading simultaneously?

**Answer:**
System is designed for high concurrency:

**Client:** Each user's app instance is independent
- Separate temp files (timestamped names)
- Separate capture threads
- No shared state between users

**Server:** Cloudflare Workers auto-scale
- Each request handled in isolated V8 context
- No shared memory between requests
- Concurrent DB inserts handled by PostgreSQL
- Supabase Storage handles concurrent uploads

**Database:** PostgreSQL supports high concurrency
- ACID transactions
- Row-level locking
- No conflicts (each report has unique UUID)

**Storage:** S3-compatible storage
- Designed for massive concurrency
- Each file has unique path
- No conflicts possible

### Q: How do you prevent audio/video capture without user consent?

**Answer:**
Current implementation:
- User must explicitly click "Start Recording"
- Status displayed prominently: "Recording... (Press F9...)"
- Red recording indicator (green text)
- User controls when to start/stop

Additional safeguards possible:
- System tray icon when recording
- Audio beep when recording starts
- Notification showing recording is active
- Windows permissions for microphone access (enforced by OS)

### Q: What's the maximum file size you can handle?

**Answer:**
Theoretical limits:

**Client:**
- No hard limit (streaming upload)
- Constrained by disk space for temp files
- Constrained by RAM during capture (~150 MB)

**Server (Cloudflare Worker):**
- Not involved in file transfer (signed URLs)
- Only processes metadata (< 1 KB)

**Supabase Storage:**
- Free tier: 1 GB total storage
- Paid tier: 100 GB+
- Single file limit: 50 GB (via chunked upload API)

**Practical:**
- 10 seconds video @ 1080p30 MJPEG: ~30-50 MB
- 10 seconds audio: ~3-4 MB
- Total per report: ~35-55 MB
- Free tier can handle ~20-30 reports before hitting 1 GB

### Q: How would you implement video replay/review in the app?

**Answer:**
Not currently implemented, but approach would be:

1. **Fetch reports from API:**
   ```
   GET /reports (new endpoint)
   Returns: list of user's reports with metadata
   ```

2. **Download files:**
   ```typescript
   // Server generates signed download URLs
   const { data } = await supabase.storage
     .from('reports-video')
     .createSignedUrl(path, 3600); // 1 hour expiry
   ```

3. **Client playback:**
   ```csharp
   // WinUI MediaPlayerElement
   <MediaPlayerElement x:Name="VideoPlayer" />
   
   VideoPlayer.Source = MediaSource.CreateFromUri(new Uri(signedUrl));
   ```

### Q: How would you add admin dashboard functionality?

**Answer:**
Implementation approach:

1. **Admin Role in Database:**
   ```sql
   alter table auth.users add column is_admin boolean default false;
   ```

2. **Admin Endpoints:**
   ```typescript
   // Middleware to check admin role
   async function adminMiddleware(c, next) {
     const userId = c.get('userId');
     // Query DB to check is_admin
     // If not admin, return 403
     await next();
   }
   
   // Admin routes
   reports.get('/', adminMiddleware, async (c) => {
     // Return all reports
   });
   ```

3. **Web Dashboard:**
   - React/Next.js app
   - List all reports
   - View/download associated files
   - Moderate/review reports
   - Mark as "forwarded_to_modulate"

---

## Known Limitations & Future Improvements

### Current Limitations

**Screen Recording:**
- Currently producing 0-byte AVI (stride/pixel format issue)
- Needs fix: 32bpp buffer with stride-safe copying
- Alternative: Switch to Windows.Graphics.Capture API

**Token Management:**
- No refresh logic (user must re-login after 1 hour)
- No persistent storage (lost on app restart)
- No token revocation

**Upload Reliability:**
- No retry logic for failed uploads
- No partial upload recovery
- No offline queue

**Rolling Buffer:**
- Audio buffer in memory (not actually used during upload)
- Video doesn't have rolling buffer (records full duration)
- Both could be improved to use segmented files

**Error Reporting:**
- Basic error messages
- No detailed diagnostics for capture failures
- No logging/telemetry

### Proposed Improvements

**Phase 1: Fix Video Capture**
- Implement stride-safe 32bpp buffer conversion
- OR: Switch to Windows.Graphics.Capture + MediaRecorder
- Validate AVI playback on multiple devices

**Phase 2: Improve Reliability**
- Token refresh logic (auto-refresh before expiry)
- Persistent token storage (Windows Credential Locker)
- Upload retry with exponential backoff
- Offline queue (submit when connection restored)

**Phase 3: Better UX**
- Upload progress for each file separately
- Cancel upload functionality
- Preview captured media before submit
- Settings page (audio quality, video resolution, buffer duration)

**Phase 4: Advanced Features**
- Global hotkey (F9 works even when app not focused)
- Multiple monitor support (choose which screen to record)
- Audio device selection (choose specific mic/output)
- Clip trimming (adjust 10-second window)
- Report history view (list past submissions)

**Phase 5: Admin/Moderation**
- Web dashboard for reviewing reports
- Download files for review
- Forward to external moderation system
- Statistics and analytics

---

## Summary of Technical Decisions

### Architecture Decisions

| Decision | Rationale |
|----------|-----------|
| WinUI 3 | Modern Windows UI framework, long-term support |
| Cloudflare Workers | Serverless, global edge, low cost, TypeScript |
| Supabase | Integrated auth + DB + storage, generous free tier |
| Three-phase upload | Handles large files reliably with Workers |
| Signed URLs | Direct client→storage, bypasses Worker limits |

### Implementation Decisions

| Decision | Rationale |
|----------|-----------|
| NAudio | Industry-standard Windows audio library, actively maintained |
| SharpAvi | Mature AVI creation library, MJPEG support |
| MJPEG codec | Good compression, simple encoding, universally playable |
| WAV format | Uncompressed audio, simple, fast to write |
| JWT auth | Stateless, secure, standard web auth method |
| Service role + RLS | Server-only DB access, prevents client tampering |

### Data Format Decisions

| Format | Rationale |
|--------|-----------|
| JSON | Standard, human-readable, widely supported |
| ISO 8601 timestamps | Unambiguous, timezone-aware, sortable |
| UUID v4 | Globally unique, no collisions, random |
| Base64 (abandoned) | Too large for video files, switched to signed URLs |
| Comma-separated paths | Simple, sufficient for small lists |

---

## Conclusion

This system implements a complete toxicity reporting pipeline with:
- Secure user authentication
- Multi-stream audio capture
- Screen recording (with ongoing fixes)
- Reliable cloud upload via signed URLs
- Database persistence with user association
- Production-ready security model

The architecture is designed to scale, with clear separation between client, API gateway, and backend storage/database.

Current focus: Fix video capture stride/pixel format issue to achieve 0-frame → real-frame recording.

