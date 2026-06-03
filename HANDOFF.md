# ChatVerse — End-to-End Handoff Document

> **Purpose of this doc:** Drop this into any future session and the assistant will instantly know what ChatVerse is, what's built, what's left, how it's wired, and how Arun likes to work. No re-discovery, no re-asking.

**Last updated:** 2026-06-03
**Owner:** Arun (arunkumaramba84@gmail.com)
**Repo paths on Arun's machine:**
- Backend: `C:\Users\arunk\source\repos\ChatVerse\`
- Frontend: `C:\Users\arunk\OneDrive\Desktop\ChatVerse_Project\ChatVerse__FrontEnd\chatverse-client\`
- Mirror folder: `C:\Users\arunk\OneDrive\Desktop\ChatVerse_Project\ChatVerse\`

---

## 1. What ChatVerse Is

ChatVerse is an **Omegle-style chat + video platform for the Indian market** with strong AI moderation. The pitch:

- Anonymous-first: guest signup is one-click (auto-generated `BraveFox4291` style usernames). No email gate to try the product.
- Four "modes" of interaction:
  1. **Text chat rooms** — public, categorized, with moderation.
  2. **Random 1-on-1 video** — WebRTC P2P, queued matching.
  3. **Random group video** — LiveKit SFU lobbies, max 6 people.
  4. **Direct invite call + hosted (named) groups** — registered-user only.
- Tier gating: guests see modes 1, 2, 3. Registered users get all 4 + DMs + private rooms.
- Strict moderation: nsfwjs runs in-browser on every video tile every few seconds; flagged frames stop the stream, log to Mongo, and (for repeated offenders) auto-kick + queue admin review. Text uses Groq via server-side filter.
- Trust-score system (0–100) with a ledger of every event (signup, OTP-verified, reports filed against, etc).
- Age-verification gate: DOB declaration → AI maturity quiz (15 questions, 5 categories) → optional document upload to Cloudinary for human admin review.
- Hindi/English language toggle (react-i18next).
- Razorpay subscriptions (Basic/Pro tiers).
- Admin dashboard (allow-list-gated) for reports + document review.

---

## 2. Tech Stack (Authoritative)

### Backend (.NET 8)
- **ASP.NET Core 8 Web API + SignalR**
- **Entity Framework Core** (used for the DbContext; most data access is via stored procs through `PostgresProcService`)
- **Npgsql** for PostgreSQL
- **MongoDB.Driver** for Mongo
- **StackExchange.Redis** for Redis
- **BCrypt.Net-Next** (cost 12) for password hashing
- **LiveKit.Server** (server SDK) for group video tokens
- **CloudinaryDotNet** for media uploads
- **Brevo (Sendinblue)** for transactional email (OTP, password reset, welcome)
- **Groq** for fast LLM moderation
- **Razorpay** SDK for payments

### Frontend (React 19 + Vite + TS)
- **React 19** with TypeScript, function components only
- **Vite** dev server
- **Tailwind v4** — design tokens live in `src/index.css` under `@theme`
- **Zustand** (with `persist` middleware) for global state — separate stores per concern (`authStore`, `dmStore`, `uiStore`, etc.)
- **react-router-dom v6**
- **react-i18next** (Hindi + English bundles)
- **livekit-client** + `@livekit/components-react` for group video
- **nsfwjs** (TensorFlow.js) — browser-side NSFW detection
- **axios** for REST
- **`@microsoft/signalr`** for hubs
- **lucide-react** icons
- **i18next-browser-languagedetector** for language autodetect

### Datastores
- **PostgreSQL** on Supabase — relational truth (users, subscriptions, reports, trust ledger, document verifications). Schemas: `user_auth`, `iam`, `trust`, `chat`, `billing`, `logs`.
- **MongoDB Atlas** — high-throughput / unstructured: chat messages, DMs, rooms, moderation logs, video sessions. All collections have TTL or partial indexes.
- **Redis (Upstash)** — sessions, presence, OTP/registration intent staging, video matching queues, room joined-sets, rate limits.
- **LiveKit Cloud** — SFU for group video (4-hour token TTL).
- **Xirsys** — TURN/STUN for WebRTC P2P fallback.
- **Cloudinary** — public avatars + private (signed) ID document uploads.

---

## 3. Architecture (Mental Model)

```
                              ┌─────────────────┐
              Browser ───────►│  React + Vite   │
              ▲               └────────┬────────┘
              │                        │ REST + SignalR + WebRTC + LiveKit WSS
              │                        ▼
              │              ┌──────────────────┐
              │              │ ASP.NET Core API │
              │              │ • Auth/Register  │
              │              │ • Rooms/Chat     │
              │              │ • Video lobbies  │
              │              │ • Admin/Trust    │
              │              │ • DMs/Profile    │
              │              │ • SignalR hubs:  │
              │              │   - ChatHub      │
              │              │   - VideoHub     │
              │              └────────┬─────────┘
              │                       │
              ▼                       ▼
   ┌──────────────────┐  ┌────────────────────────────────────┐
   │ LiveKit SFU      │  │ Postgres   Mongo   Redis   Brevo   │
   │ (group video)    │  │ (truth)    (chat)  (cache) (email) │
   └──────────────────┘  └────────────────────────────────────┘
```

**Key cross-cutting flows:**

- **Auth:** JWT (HS256, 24h) with claims `uid`, `username`, `is_guest`, `trust_score`, `is_email_verified`, `age_verified`. Issued by `JwtService`. Guests get a JWT too (with `is_guest=true`) — they can use Redis-keyed features.
- **Registration:** OTP-FIRST. No row is created in `user_auth.users` until OTP is verified. Intent + OTP staged in Redis with TTLs (15min intent / 10min OTP).
- **Moderation:** nsfwjs scans video frames every ~3s on the client. Flagged frames POST to `/api/trust/report-self` (auto-kick) or queued via `RandomGroupController`. Text moderation is server-side in `ChatHub.SendMessage`.
- **Realtime:** SignalR hubs use the same JWT. Hubs:
  - `ChatHub` — room messaging, typing, reactions, DM events, direct-invite calling signals
  - `VideoHub` — random 1-on-1 queue, WebRTC offer/answer/ICE relay, match-found broadcasts

---

## 4. Database Setup — Scripts Run + Status

### 4.1 PostgreSQL (Supabase) — ✅ DONE
Main schema migration (`user_auth.*`, `iam.*`, `trust.*`, `chat.*`, `billing.*`, `logs.*`) was already in place from earlier work.

Bootstrap additions ran from:
**`C:\Users\arunk\source\repos\ChatVerse\db\postgres-init.sql`**

This added:
- `CREATE EXTENSION pg_trgm` — trigram extension for fuzzy username search.
- GIN index `idx_users_username_trgm ON user_auth.users USING GIN (LOWER(username) gin_trgm_ops)`.
- Defensive indexes: `idx_users_email_lower`, `idx_user_reports_status`, `idx_doc_verif_status`.
- `user_auth.usp_search_users(p_query, p_me_id, p_limit)` — trigram-backed search function. **NOT YET WIRED** into the controller (see Pending §11).
- Probe `DO $$` block that warns if `billing.usp_expire_subscriptions` is missing.

**To re-run:** safe — every statement is `IF NOT EXISTS` / `OR REPLACE`.

### 4.2 MongoDB Atlas — ✅ DONE
Bootstrap script: **`C:\Users\arunk\source\repos\ChatVerse\db\mongo-init.js`** (v2 tolerant version)

Run via:
```js
load("C:/Users/arunk/source/repos/ChatVerse/db/mongo-init.js")
```

The script uses a `safeIndex()` wrapper that catches `IndexOptionsConflict` (codes 85/86) so re-running on a DB with pre-existing same-spec-different-name indexes doesn't abort. Indexes created:

| Collection | Index | Notes |
|---|---|---|
| `messages` | `roomId+createdAt`, `senderId+createdAt`, `moderation.status` (partial), `createdAt` TTL 30d | |
| `rooms` | `slug` unique, `isActive+isPrivate+stats.activeNow`, `category`, `inviteToken` unique partial | |
| `dm_messages` | `conversationId+createdAt`, `recipientId+isRead`, `senderId+createdAt` | |
| `moderation_logs` | `senderId+createdAt`, `action+createdAt`, `messageId`, `reviewedBy` partial, `createdAt` TTL 90d | |
| `video_sessions` | `sessionId` unique, `participants.userId+startedAt`, `outcome` partial, `nsfwFlags.userId` sparse, `startedAt` TTL 60d | |

### 4.3 Redis (Upstash) — ✅ DONE (no schema needed)
Key patterns used (for reference):
- `session:{userId}` — JSON session
- `online:{userId}` — online flag (with TTL via heartbeat)
- `otp:reg:{email}` (10min) / `intent:reg:{email}` (15min) — staged registration
- `otp:pwreset:{email}` — password reset code
- `q:video:random` — list (FIFO) for random 1-on-1 matching
- `lobby:group:{lobbyId}` — sorted set for group lobbies
- `rooms:joined:{userId}` — set of private rooms the user has joined
- `invite:call:{toUserId}:{fromUserId}` — direct invite tracking (60s TTL)

---

## 5. Backend — Folder Structure & Key Files

```
ChatVerse.API/
  Controllers/
    Authcontroller.cs          ★ OTP-first registration, BCrypt login, password reset
    RandomGroupController.cs   ★ LiveKit-backed random group lobbies (max 6)
    DirectCallController.cs    ★ Direct 1-to-1 invite call orchestration
    DmsController.cs           ★ DM REST (list/thread/start) — events via ChatHub
    Roomscontroller.cs         ★ Public + private rooms; invite token join
    Userscontroller.cs         ★ Search, profile update, avatar upload
    Agecontroller.cs           ★ DOB declare, AI quiz, document upload
    AdminController.cs         ★ Allow-list-gated dashboard (reports + docs)
    Billingcontroller.cs       ★ Razorpay checkout + webhook verification
    Trustcontroller.cs           Self-report, peer-report (video moderation)
    GroupCallController.cs       Named/hosted LiveKit rooms
    IceController.cs             Dynamic ICE servers (STUN+TURN from Xirsys)
  Hubs/
    Chathub.cs                 ★ Messages, typing, reactions, DMs, direct-invite call signals
    Videohub.cs                Random 1-on-1 WebRTC signaling, queue matching
  Services/
    MatchingService.cs         ★ BackgroundService polling Redis video queue (atomic LPOP)
    JwtService.cs              Token issuance + claim helpers
    PasswordHasher.cs          BCrypt + legacy SHA-256 verify (auto-rehash on login)
  Middleware/
    Globalexceptionmiddleware.cs   Catches + logs unhandled exceptions
  Extensions/                  DI registration helpers
  appsettings.json             ★ ConnectionStrings, JWT secret, Cloudinary, Brevo,
                                 OpenAI, Groq, Razorpay, LiveKit, Xirsys creds.
                                 NOT IN GIT.

ChatVerse.Infrastructure/
  Persistence/
    PostgreSQL/
      Postgresprocservice.cs   ★ All stored proc calls live here
      ChatVerseDbContext.cs    EF Core context (limited use)
    Redis/
      RedisService.cs          All Redis ops
    Mongo/
      MongoService.cs          Collections + ConversationIdFor() helpers
  ExternalServices/
    Email/BrevoEmailService.cs   OTP + welcome + reset emails
    LiveKit/LiveKitTokenService.cs   JWT for LiveKit (4h TTL)
    Cloudinary/CloudinaryService.cs  Public + signed URL upload

ChatVerse.Domain/
  Constants/        Otp.cs (TTL/expiry), TrustDeltas.cs
  Enums/            TrustEventType, OtpPurpose, etc.

db/
  mongo-init.js     ★ Index + TTL bootstrap (idempotent)
  postgres-init.sql ★ Trigram + procs bootstrap (idempotent)
```

★ = files actively edited in current work cycle.

---

## 6. Frontend — Folder Structure & Key Files

```
chatverse-client/src/
  api/
    index.ts                ★ Axios instance + endpoint groups (auth, rooms, dms, video, etc.)
    auth.ts                 Auth-specific helpers
  hooks/
    useChatHub.ts           ★ SignalR ChatHub wrapper — message + DM events
    useVideoHub.ts          SignalR VideoHub wrapper — match + WebRTC signaling
    useTurnedIce.ts         Dynamic ICE servers fetch
  stores/
    authStore.ts            User session, JWT
    dmStore.ts              Conversations + threads (Zustand)
    uiStore.ts              ★ secondaryCollapsed (persist) + other UI toggles
    chatStore.ts            Active room state
  components/
    layout/
      AppLayout.tsx         ★ 3-pane shell with collapsible secondary sidebar
      Sidebar/
        PrimarySidebar.tsx  Tab switcher (chat/video) + profile button
        SecondarySidebar.tsx ★ Collapsible container (uses uiStore)
        ChatSidebar.tsx     ★ Public rooms + "My rooms" (private) + invite paste
        VideoSidebar.tsx    Video mode picker
    ui/                     Avatar, Badge, Button, Input, Loader, Toast
    moderation/
      NsfwSelfScanner.tsx   nsfwjs in-browser scanning component
  pages/
    auth/
      LandingPage.tsx
      LoginPage.tsx         ★ With "Forgot password?" link
      RegisterPage.tsx      ★ OTP-first flow (send → verify)
      OtpVerifyPage.tsx
      ForgotPasswordPage.tsx
      ResetPasswordPage.tsx
    chat/
      RoomsLandingPage.tsx
      RoomPage.tsx
      NewRoomPage.tsx       Create private room
    video/
      VideoLobbyPage.tsx    ★ 4 mode tiles, tier-gated for guests
      VideoPage.tsx         Random 1-on-1 (WebRTC)
      RandomGroupPage.tsx   LiveKit group (max 6)
      DirectCallPage.tsx    Direct invite call
      HostedGroupPage.tsx   Named LiveKit room
    dms/
      DmsPage.tsx           ★ Conv list + active thread (EMPTY_ARRAY stable ref fix applied)
    profile/
      ProfilePage.tsx       ★ Edit display name, avatar, language switcher
    verification/
      DeclareDobPage.tsx
      MaturityQuizPage.tsx  15 Qs / 5 categories
      DocumentUploadPage.tsx
    admin/
      AdminLayout.tsx       Allow-list check
      AdminReportsPage.tsx
      AdminDocumentsPage.tsx
    billing/
      PricingPage.tsx       Razorpay checkout
  i18n/
    index.ts                react-i18next bootstrap
    en/common.json
    hi/common.json
  App.tsx                   ★ All routes (incl. /dms, /rooms/new, /verify/*, /admin, /pricing)
  index.css                 ★ Tailwind v4 @theme tokens + base/utility layers
```

---

## 7. Feature Status (Authoritative Checklist)

### ✅ Complete
- Full UI redesign — Linear/Vercel minimal dark theme, design system in `src/index.css`.
- BCrypt password hashing (cost 12) with legacy SHA-256 transparent rehash on next login.
- ChatHub `OnDisconnectedAsync` queue-clear bug fix.
- VerifyOtp returns the real username in JWT (was bug previously).
- **OTP-first registration** — no `user_auth.users` row until OTP verified. Redis-staged intent.
- All 4 video modes:
  - Random 1-on-1 (WebRTC P2P via VideoHub)
  - Random group (LiveKit lobbies, max 6, via RandomGroupController)
  - Direct invite (ChatHub `InviteToCall` / `AcceptCall` / `DeclineCall`, 60s window)
  - Hosted/named group (LiveKit, registered-only)
- Tier gating on VideoLobbyPage — guests see 3 modes, registered see 4.
- nsfwjs self-scan on random 1-on-1 video + group video.
- Age verification flow:
  - DOB declare → backend writes to `iam.usp_submit_age_declaration`
  - AI maturity quiz (15 questions across 5 categories)
  - Document upload to Cloudinary (signed/private), backend `usp_submit_document_verification`
  - Admin reviews via `iam.usp_review_document_verification`
- Admin dashboard — allow-list via `Admin:UserIds` in `appsettings.json`. Reports list + review + document review.
- DM system — backend `DmsController` + ChatHub events (`ReceiveDm`, `DmTyping`, `DmRead`) + frontend page with conversation list and thread.
- Password reset (forgot + reset, OTP-style code via Redis, generic responses to prevent enumeration).
- Profile editing — display name + avatar (Cloudinary public).
- Private rooms — user-created rooms default to `IsPrivate=true` with random `InviteToken`. Public list filters them out. Joinable by anyone (guest too) via URL paste. Shown in "My rooms" sidebar section.
- TURN config — Xirsys creds in `appsettings.json` under `WebRtc:`. Dynamic ICE endpoint at `IceController`.
- VideoHub `MatchingService` — BackgroundService polling Redis queue every 500ms with atomic LPOP, safe for multi-replica.
- Hindi i18n — react-i18next with EN + HI bundles, language toggle in ProfilePage.
- Collapsible secondary sidebar — persisted in `uiStore` via Zustand `persist`. Expand handle in `AppLayout` when collapsed.
- DB bootstrap scripts (`mongo-init.js` + `postgres-init.sql`) — both run successfully on Atlas + Supabase.

### Bug fixes applied in latest cycle (after DB bootstrap)
1. **`VerifyOtp` — case-sensitive JSON deserialization bug** → `RegistrationIntent` record properties were PascalCase but Redis JSON was camelCase → deserialized values came as null → Npgsql exploded with `p_username must have its Value set`. Fix: `PropertyNameCaseInsensitive = true` + defensive null check before calling `RegisterUserAsync`. Location: `Authcontroller .cs` line ~178 area.
2. **`SubmitAgeDeclarationAsync` — DateOnly mapped to wrong PG type** → `dob.ToDateTime(...)` made Npgsql infer `timestamp without time zone`, proc wanted `date`. Fix: explicit `NpgsqlDbType.Date` + `Value = dob` directly. Also user added `IPAddress.Parse` with `NpgsqlDbType.Inet` for `p_ip_address` because proc actually takes `inet`. Location: `Postgresprocservice.cs` line ~374.

---

## 8. User's Workflow Rules (CRITICAL — Read This)

1. **DO NOT rewrite files Arun has already committed to git.** Read them, Edit them, but don't `Write` whole new versions unless explicitly asked. Exceptions:
   - OneDrive sync truncated a file (mid-string `[PARSE_ERROR]`).
   - Arun says "wapas lao" / "rewrite kar do".
2. **OneDrive ↔ sandbox sync truncation is a real hazard.** Files between `C:\Users\arunk\OneDrive\Desktop\...` and the Linux sandbox sometimes truncate. If a file looks cut-off mid-string, treat it as truncated; ask before rewriting unless context makes it obvious.
3. **Secrets in `appsettings.json` are NOT in git.** Arun confirmed: "secret ko main dekh lunga abhi wo mere appsettings me pada hai wo git pe nahi gya hai." Don't ever check that file into git from this side.
4. **Workflow order for new infra:** DB scripts FIRST → Arun runs them manually in Atlas/Supabase → confirm → THEN API side → THEN UI side. Don't write code that depends on procs/indexes that haven't been deployed.
5. **Language:** Arun mixes Hindi (Roman/Devanagari) and English. Respond in the same code-switching style — that's not a request for translation, that's how he thinks.
6. **Tone:** Concise + direct. No excessive preamble. He explicitly set user preferences to that effect.
7. **Token budget consciousness:** Arun frequently says "session out hone wala hai" / "token khatam ho rha hai." Don't write giant explainers when a fix + restart instruction will do.

---

## 9. Configuration Reference (`appsettings.json` keys)

Required keys (with current state of `YOUR_*` placeholders that still need real values):

```jsonc
{
  "ConnectionStrings": {
    "PostgreSQL": "Host=db.jzdqgtklgidpzvhchlit.supabase.co;...",  // ✓ set
    "Redis": "rediss://default:...@relieved-gazelle-105364.upstash.io:6379", // ✓ set
    "MongoDB": "mongodb+srv://...@cluster0.u5ovo.mongodb.net/..."  // ✓ set
  },
  "MongoDB": { "DatabaseName": "chatverse" },
  "Jwt": {
    "SecretKey": "...",          // ✓ set
    "Issuer": "ChatVerse",
    "Audience": "ChatVerseUsers",
    "ExpiryHours": 24
  },
  "Cloudinary": {                // ⚠ NEEDS REAL VALUES — currently placeholders
    "CloudName": "YOUR_CLOUD_NAME",
    "ApiKey": "YOUR_API_KEY",
    "ApiSecret": "YOUR_API_SECRET"
  },
  "Brevo": { "ApiKey": "...", "SenderEmail": "arunkumaramba84@gmail.com" }, // ✓ set
  "OpenAI": { "ApiKey": "YOUR_OPENAI_API_KEY" },  // ⚠ not used currently — Groq is primary
  "Groq":   { "ApiKey": "gsk_cyR..." },           // ✓ set
  "Razorpay": {                  // ⚠ NEEDS REAL VALUES
    "KeyId": "YOUR_RAZORPAY_KEY_ID",
    "KeySecret": "YOUR_RAZORPAY_KEY_SECRET",
    "WebhookSecret": "YOUR_WEBHOOK_SECRET"
  },
  "LiveKit": {                   // ✓ set (real)
    "ApiKey": "APIiVvnRaz7EMft",
    "ApiSecret": "ESjws1cQAoR...",
    "ServerUrl": "wss://chatverse-byb8zl8z.livekit.cloud"
  },
  "VideoSettings": {
    "EnableAgeBypass": true,      // dev convenience — switch to false for prod
    "MinimumTrustScoreRequired": 0,
    "RequiredTier": "Basic"
  },
  "Admin": {
    "UserIds": []                 // ⚠ add Arun's UUID here for admin dashboard access
  },
  "WebRtc": {                    // ✓ set (Xirsys real creds)
    "StunUrls": [ "stun:bn-turn2.xirsys.com" ],
    "TurnUrls": [ "turn:bn-turn2.xirsys.com:80?transport=udp", ... ],
    "TurnUsername": "...",
    "TurnCredential": "..."
  }
}
```

**To-do on config:** Cloudinary + Razorpay still placeholders. Admin UserIds is empty.

---

## 10. Recent Bug-Fix Patterns to Remember

These all share a root cause: **Npgsql cannot infer Postgres types reliably from `AddWithValue` when the proc has strict typing.** Pattern to use everywhere:

```csharp
cmd.Parameters.Add(new NpgsqlParameter("p_name", NpgsqlDbType.X) { Value = value ?? (object)DBNull.Value });
```

Specifically watch for:
- `DateOnly` → must be `NpgsqlDbType.Date` (not Timestamp).
- IP addresses → must be `NpgsqlDbType.Inet` with `IPAddress.Parse(...)`, not Text.
- JSON columns → must be `NpgsqlDbType.Jsonb` with serialized string.
- All OUT params → must declare `Direction = ParameterDirection.Output` + correct `NpgsqlDbType` before `ExecuteNonQueryAsync`.

**Always make Redis JSON deserialization case-insensitive:**
```csharp
JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
```
because we serialize with anonymous-object camelCase but deserialize into PascalCase records.

---

## 11. Pre-Hosting Checklist (Hosting TODAY — Arun, do these FIRST)

Items prefixed **[ARUN]** = Arun must do (3rd-party signups, secret rotation, hosting setup). Items prefixed **[CODE]** = already handled in code, just listed for awareness.

### Blockers — without these, features WILL break in prod

1. **[ARUN] Cloudinary real keys** — currently `YOUR_CLOUD_NAME` placeholders. Sign up at cloudinary.com → Dashboard → Settings → Access Keys → copy Cloud Name + API Key + API Secret → set as `Cloudinary__CloudName` / `Cloudinary__ApiKey` / `Cloudinary__ApiSecret` env vars. **Without this:** avatar upload + ID doc upload both return 401.
2. **[ARUN] Razorpay test keys** — currently `YOUR_RAZORPAY_*` placeholders. Razorpay dashboard → API Keys → test mode (`rzp_test_*`). Also generate Webhook secret. Set `Razorpay__KeyId` / `Razorpay__KeySecret` / `Razorpay__WebhookSecret`. **Without this:** pricing page checkout fails.
3. **[ARUN] Own UUID in Admin:UserIds** — `SELECT id FROM user_auth.users WHERE email='arunkumaramba84@gmail.com'`. Add UUID to `Admin__UserIds__0` env var. **Without this:** `/admin` returns 403 even for Arun.
4. **[ARUN] Rotate JWT SecretKey for prod** — dev one is leaked. Generate fresh 64+ char random secret (`openssl rand -base64 64`). Set `Jwt__SecretKey`. All existing dev JWTs invalidate.
5. **[ARUN] VideoSettings:EnableAgeBypass=false in prod** — currently `true` for dev convenience. MUST be `false` in prod or users access video without verification.
6. **[ARUN] Set frontend `VITE_API_BASE_URL`** — in Vercel/Netlify env vars, point to deployed backend URL. Without it, frontend tries `localhost:7001` from production.
7. **[ARUN] Set backend `Cors__Origins__0=<frontend URL>`** — CORS is now config-driven (CHANGED in Program.cs this session). Without setting, falls back to `https://chatverse.app` — won't match your real domain.

### Hosting setup steps

8. **[ARUN] Decide platforms + create accounts:**
   - Backend → Railway or Render (both have free .NET tier). Azure App Service if you want Microsoft stack.
   - Frontend → Vercel or Cloudflare Pages (both auto-deploy from GitHub).
   - Domain → optional for v1; platform subdomains work fine initially.
9. **[ARUN] Push final code to GitHub** — make sure `appsettings.json` with real secrets is NOT committed. Use `appsettings.Production.json` (gitignored) OR env vars on the hosting platform.
10. **[ARUN] Connect repo to hosting platforms** — both Railway and Vercel just need GitHub repo + branch. Auto-deploys on push.

### Post-deploy smoke test

11. Open prod URL → register → check email → enter OTP → confirm user_auth row created with `is_email_verified=true`.
12. Login → /chat → join a public room → send message → reload to confirm persisted.
13. /video → try Random 1-on-1 in 2 incognito tabs (or 2 devices).
14. /admin → confirm dashboard loads (after adding UUID to Admin:UserIds).

---

## 12. Pending Features (POST-HOSTING — do NOT ship same-day as deploy)

### Recently requested by Arun (deferred for safe baseline launch)

1. **Online presence count + UI badges**
   - Backend: Redis Set `presence:global` + `presence:room:{slug}` via SADD on ChatHub.OnConnectedAsync / SREM on OnDisconnectedAsync. SCARD for O(1) count. Add `GET /api/presence/stats` → `{globalOnline, byRoom}`. 60s TTL + 30s client heartbeat.
   - Frontend: top-bar badge global count + per-room dot+number in ChatSidebar.
   - Why deferred: low-risk but new code in critical realtime path. Ship after deploy is stable.

2. **AiPresenceService — AI host in empty rooms**
   - BackgroundService scanning rooms every 30s. If 0–1 real users AND last message > 45s old → spawn AI with persona from pool. 1.5–3s typing delay before each message. Groq prompt with last 5 messages as context.
   - Feature flag `Ai:EnablePresence` (default false).
   - **Legal/ethical:** add small "AI" badge next to persona name + landing-page disclosure "some hosts are AI to keep rooms active." Reduces legal exposure + builds trust.
   - Why deferred: needs careful persona tuning + prompt iteration. Don't rush it under launch pressure.

### Originally pending (from last session)

3. **`SubscriptionExpiryService`** — daily HostedService calling `billing.usp_expire_subscriptions`. Pattern: copy from `MatchingService.cs`.
4. **`UsersController.Search` → trigram proc switchover** — proc already deployed (`user_auth.usp_search_users`). Switch controller to call it instead of raw `LIKE` prefix.
5. **`ChatHub.RemoveReaction`** — companion to existing `ReactToMessage`.
6. **DM read-receipt UI** — backend emits `DmRead`; frontend doesn't render "Seen/Delivered" yet.
7. **Message reactions picker UI** — hover → "+" → emoji popover → chips below bubble.

### Explicitly DEFERRED (not in scope)
- VideoHub frontend migration (Arun said "chhod ke").
- Self-hosted nsfwjs model (Arun said "chhod ke").
- Voice rooms / audio-only mode.
- Mobile app (web-first MVP).

---

## 12. How to Run Locally (Quick Reference)

```bash
# Backend
cd C:\Users\arunk\source\repos\ChatVerse\ChatVerse.API
dotnet run
# Listens on https://localhost:7001 (or whatever launchSettings.json says)

# Frontend
cd C:\Users\arunk\OneDrive\Desktop\ChatVerse_Project\ChatVerse__FrontEnd\chatverse-client
npm install   # first time only
npm run dev
# http://localhost:5173 — Vite proxy targets backend (see vite.config.ts)
```

After ANY backend code change → Ctrl+C and `dotnet run` again. Hot-reload doesn't catch proc-binding edits.

---

## 13. How to Continue in a Fresh Session

Paste this entire `HANDOFF.md` (or just say "read `C:\Users\arunk\source\repos\ChatVerse\HANDOFF.md`"). The assistant should:

1. Read this doc end-to-end.
2. Ask Arun which pending item to tackle (or pick the smallest high-value one: `SubscriptionExpiryService`).
3. NOT rewrite already-committed files.
4. Follow the DB-first → API → UI workflow rule.
5. Respect the bug-fix patterns in §10 when touching Postgres procs.

Don't re-do work that's already marked ✅ in §7. Don't re-audit the codebase from scratch — trust this doc and ask Arun to point out anything stale.

---

## 14. Glossary of "Why is it called that?"

- **`anon001`** — guest UID seen in logs is a test guest Arun has been using.
- **`safeIndex()`** — the tolerant `createIndex` wrapper in `mongo-init.js` that skips IndexOptionsConflict.
- **`EMPTY_ARRAY`** — module-level constant in `DmsPage.tsx` used as a stable Zustand selector return value to avoid infinite re-renders (Gemini diagnosed; applied).
- **"Trust score"** — 0–100 integer on `user_auth.users`. Updated via `trust.usp_apply_trust_event` proc with a ledger row per event.
- **"OTP-first"** — Arun's term for "don't create the user row until OTP is verified, otherwise you get zombie accounts." This was a CRITICAL fix earlier.

---

*End of handoff doc. Drop into a fresh session and tell me which pending item to start.*
