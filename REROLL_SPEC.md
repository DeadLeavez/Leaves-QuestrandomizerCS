# Runtime Quest Re-roll — Server Spec (draft)

Status: design only, nothing built. For the feature discussed 2026-07-15: let a player re-roll
the randomization of a specific quest (or all) at runtime, per-profile, via a server-served web
UI (MVP) and optionally an in-game BepInEx trigger later.

---

## 1. The core realization: two separate stores must both change

A "quest" in this system is split across two independent places, and a coherent re-roll has to
touch **both**:

| Store | Location | Holds | Owned by |
|---|---|---|---|
| **Randomization cache** | `assets/generated/<profileId>/quests.json` + `locales.json` (this mod) | the *rolled definition* — conditions, weapon lists, the flavor text | Questrandomizer |
| **Player profile** | `user/profiles/<profileId>.json` (SPT) | quest *status + counters* (started, N/10 kills done) | SPT core |

Re-roll = (a) produce a new rolled **definition** for the quest, and (b) reset the player's
**progress** on it so in-flight counters don't desync against the new conditions. Doing only (a)
is the exact bug the caching was built to prevent (see the comment at
`Questrandomizer.GenerateProfileData` / the `originalTraders`-style desync note): a counter
part-way to the old conditions silently mis-tracks the new ones. **Re-roll resets progress by
design** — that's expected ("give me a fresh version of this quest"), not a regression.

## 2. The re-roll primitive already exists (definition side)

`GenerateProfileData` reuses any quest found in the profile's disk cache and only freshly
randomizes quests *not* in it. So the definition-side re-roll is:

> remove the quest from the profile's cache → re-run `GenerateProfileData` → it comes back
> re-rolled, everything else reused untouched.

`EditQuest` uses `Random.Shared`, so a re-edit produces a genuinely new roll. The vestigial
`purgeQuests` config field ([Config.cs:190]) was clearly meant to be the startup version of
this list; the runtime API generalizes it.

## 3. Identity model — the important consequence of a web UI

- **Game-client requests** carry an authenticated session id → an in-game trigger would get the
  caller's profile for free.
- **A browser** hitting the server has **no** game session context, so it can't be auto-identified.

**Decision:** the re-roll API takes an **explicit `profileId`** in the request. The web UI shows
a profile picker (id + nickname) and passes the chosen one. This keeps the effect strictly
**per-player** (only that profile's cache + progress change) regardless of entry point, and a
future in-game trigger can pass its own session id into the same API. "Per player, not
server-wide" is satisfied by *scope of effect*, not by auth.

## 4. Two separate features, two pages, two route groups

The web UI is split into two independent tools with separate pages and endpoints, so the
riskier completed-quest logic never touches the re-roll path:

- **Re-roll (active quests)** — Phase 1 / MVP. Lists the profile's currently-accepted,
  uncompleted quests; re-rolls their randomization and resets their counters. *Mutates* an
  existing quest.
- **Re-play (completed quests → fake quests)** — Phase 2, see §11. Lists the profile's completed
  quests; spawns a brand-new "fake" duplicate that looks identical but has randomized rewards,
  so you can replay content (e.g. to accompany a friend on a map you've already cleared).
  *Additive* — never edits the finished quest, so it sidesteps the completed-quest desync
  problems entirely.

Two SPT mechanisms, used for different pieces:

- **`IHttpListener`** (see `15HttpListenerExample`) — raw `HttpContext`, full control of
  response body + `ContentType`. Used to **serve the HTML pages** and the browser-facing JSON
  (no SPT auth needed; `ShouldHandle(method, path)` gate).
- **`StaticRouter`** (see `10CustomRoute`) — clean JSON `RouteAction`s that receive `sessionId`.
  Optional: used if/when an in-game client calls the API with real auth.

Routes (all under `/questrandomizer/…`):

| Feature | Route | Method | Body / query | Returns |
|---|---|---|---|---|
| shared | `/questrandomizer/api/profiles` | GET | — | `[{ profileId, nickname, level }]` |
| **re-roll** | `/questrandomizer/ui/reroll` | GET | — | the re-roll page (HTML) |
| **re-roll** | `/questrandomizer/api/quests/active` | GET | `?profileId=` | `[{ questId, name, status, summary }]` — accepted + uncompleted + randomizable only |
| **re-roll** | `/questrandomizer/api/reroll` | POST | `{ profileId, questIds: [...] \| "all" }` | `{ ok, rerolled, newSummaries, requiresRestart: true }` |
| **re-play** | `/questrandomizer/ui/replay` | GET | — | the re-play page (HTML) |
| **re-play** | `/questrandomizer/api/quests/completed` | GET | `?profileId=` | `[{ questId, name, map, originalRewardSummary }]` — completed only |
| **re-play** | `/questrandomizer/api/replay` | POST | `{ profileId, questId }` | `{ ok, newQuestId, requiresRestart: true }` |

`summary` = a short human string of the current roll (target/map/weapon), so pages are
searchable/browsable without loading the game. The `/quests/active` list is already filtered to
what's re-rollable, so the UI needs no "greyed out" state — see §5 step 1 for the filter.

## 5. Re-roll operation — server algorithm

```
POST /questrandomizer/api/reroll { profileId, questIds }

1. VALIDATE
   - profile exists (ProfileHelper / SaveServer).
   - NOT in an active raid (ProfileActivityService) -> else 409 "finish/leave raid first".
   - each quest is ELIGIBLE:
       * currently ACCEPTED and NOT completed - i.e. the profile's quest status is Started
         or AvailableForFinish (ready to hand in), never Success/Fail/Locked. A completed
         quest is rejected outright (use Re-play instead, §11).
       * randomizable: present in this profile's cache, not blacklisted, and (MVP) not a
         procedural generated quest.
   The /api/quests/active endpoint applies this same filter, so the UI only ever offers
   eligible quests; the POST re-validates server-side (the profile could have changed).

2. UNDER THE EXISTING PER-PROFILE GATE (generationGates[profileId]):   // no races
   a. Load the profile's cache (quests.json + locales.json).
   b. Remove each target questId from the cached quests dict.
      (Locale mostly self-heals: re-editing overwrites the same locale ids. Optionally prune
      the quest's old locale ids too for tidiness.)
   c. Write the trimmed cache back to disk.
   d. Evict the profile from the in-memory context maps (NEW: QuestrandomizerContext.RemoveProfile).
   e. Call GenerateProfileData(profileId): re-rolls the removed quests, reuses the rest,
      repopulates the context maps, re-saves the cache.

3. RESET PROGRESS in the player profile for each rerolled quest
   - set the quest's status back to its pre-start state and clear its counters,
     so the client tracks the new conditions from zero.
   - SaveServer.SaveProfile(profileId) to persist.

4. RESPOND { ok, rerolled, newSummaries, requiresRestart: true }.
```

Step 2d needs a small addition — `QuestrandomizerContext` currently has setters and
`TryGet…` but **no removal** method. Add `RemoveProfile(profileId)` that clears both
`questsByProfile` and every `localeByProfile` language entry for that profile.

## 6. Client reload contract (MVP)

The API returns `requiresRestart: true`. The web page shows: *"Re-rolled. Restart your game
client now to load the new quest(s)."* On relaunch the client re-requests `/client/quest/list`
and `/client/locale/*`, which the existing Harmony patches serve with the re-rolled data — so
**no client-side data surgery is needed for the MVP.** In-client hot-reload (no restart) is a
later stretch and explicitly out of scope here.

Also block re-roll mid-raid (step 1) — changing a quest while its counters are live in a raid
is the desync case.

## 7. Concurrency & coop

- Re-roll takes the **same `generationGates[profileId]` lock** as normal generation, so it can't
  race a first-time generation or a second concurrent re-roll for that profile.
- Coop/Fika: each player picks their own profile in the web UI; different profiles use different
  gates and don't block each other (same property that made concurrent logins safe).

## 8. Resolved decisions (2026-07-15)

1. **Completed quests are NOT re-rollable.** Re-rolling a finished quest causes too many
   downstream issues (the DEV profile that accepts quests out of order already demonstrates the
   chaos). Instead, completed quests get the separate **Re-play** feature (§11) — additive, not
   mutating.
2. **Re-roll targets only currently-accepted, uncompleted quests** (status Started /
   AvailableForFinish). This is both the `/api/quests/active` filter and the re-roll server-side
   guard.
3. **All re-rolls reset counters** — always, by design (§1). Allowed only while the quest is not
   completed (per #1/#2).
4. **Vanilla-edited quests only.** Procedural generated quests (weapon/sniper/handover chains,
   shared stable ids in `generatedQuestIds`, linked in sequences) are out of scope for now —
   excluded from the active list.
5. **Restart, not hot-reload** (§6).
6. **Web UI security: deferred.** Runs on a private ZeroTier LAN, so an unauthenticated page is
   an acceptable starting risk. A password is a nice-to-have to add once it's working — leave a
   hook for it but don't block the MVP.

## 9. Build order & rough effort

Phase 1 — Re-roll (active quests):
1. ✅ DONE (2026-07-15) `QuestrandomizerContext.RemoveProfile` + `Questrandomizer.RerollProfileQuests`
   + `GetRerollableActiveQuests`. Honors questBlacklist + excludes procedural.
2. ✅ DONE + VERIFIED (2026-07-15) `QuestrandomizerRerollApi` (`IHttpListener`): `GET
   /api/profiles`, `GET /api/quests/active?profileId=`, `POST /api/reroll`. Live-tested via curl:
   re-rolled "Debut" (kill 5 Savage -> kill 4 Bear), profile progress reset (status->Started,
   completedConditions cleared, TaskConditionCounters purged, profile saved), blacklisted
   "Grenadier" correctly excluded from the active list.
   - **NOTE: the server is HTTPS with a self-signed cert.** Test with `curl -sk https://127.0.0.1:6969/...`
     (`-k` for the cert; plain `http://` returns empty).
   - Raid guard is a **soft warning** only (response `warning` field when the profile looks
     in-game) - hardening deferred until the 4.0 `ProfileActivityRaidData.RaidConfiguration`
     lifecycle is confirmed (does it clear on raid end?).
3. ✅ DONE (2026-07-15): the `/questrandomizer/ui` (and `/ui/reroll`) HTML page - self-contained
   dark-theme page (profile dropdown, search box, select-visible/clear, checklist, re-roll
   button, "RESTART now" message), served via the same `IHttpListener` as a raw-string constant
   (no external assets). Verified serving (text/html, ~4.6 KB); browser rendering to be eyeballed
   by the user (the in-app test browser can't accept the game's self-signed localhost cert).

**Phase 1 is functionally complete.** Open `https://127.0.0.1:6969/questrandomizer/ui` in a
browser (accept the self-signed cert once), pick the profile, select quests, re-roll, restart
the client.

Phase 2 — Re-play (fake quests): §11.

Later: web UI password; in-game BepInEx trigger; in-client hot-reload.

Phase 1 steps 1–2 are the server endpoint you asked to spec and can be built and verified with
zero client work.

## 10. Client reload — unchanged for both features

Both re-roll and re-play return `requiresRestart: true`; the client picks up the new/changed
quest + locale on relaunch via the existing patches. No client-side surgery either way.

## 11b. Phase 2 status — IMPLEMENTED (2026-07-19)

Built in `QuestrandomizerReplay.cs` (partial of `Questrandomizer`) + two API endpoints +
a "Re-play" tab on the page. Decisions taken (resolving the open questions below):

- **Self-contained**: `AvailableForStart` (prereq quests, level gates) is cleared, so the copy
  spawns immediately available. Objectives (`AvailableForFinish`) are copied **verbatim**.
- **Fresh ids**: quest id + all condition/counter ids re-minted (owned-ids only; references to
  real items/traders untouched). Rewards are **fully re-rolled** (`QuestGeneration.RerollRewards`,
  the new generic reward primitive), so old reward ids never need remapping.
- **Locale**: name/description/messages + objective text are re-resolved from the profile's SERVED
  locale into `<newId> …` keys, per language — format-agnostic (works for vanilla- and mod-keyed
  sources). Caveat: a language the source has no text in yields no clone text there (en always works).
- **Persistence**: written to the profile cache under a non-vanilla, non-procedural id;
  `GenerateProfileData` gained an "extra cached quests" TryAdd pass so replays survive every
  regeneration. Same per-profile gate as re-roll (coop-safe).
- **Repeatability**: stacking allowed (each replay is a new id; no dedupe). **Cleanup/delete: not
  built yet** — a future "delete fake quest" endpoint.
- **Reward magnitude**: `ReplayRewardQuestNumber` constant (15) drives tier/XP — promote to config
  if it wants tuning.
- Verified via curl: replayed "Debut" -> fresh quest with kept objectives, stripped prereqs,
  re-rolled rewards, re-keyed locale.

## 11. Phase 2 — Re-play (fake replay quests)

**Purpose:** you've completed all quests on a map (or never had any there), but want something
to do — e.g. to accompany a friend who still has quests on that map. Re-play spawns a **new,
fake quest that looks identical to a completed one but grants randomized rewards**, giving you
fresh content without touching the original.

**Why it's a separate, additive feature:** it never edits the finished quest or its status, so
it completely avoids the completed-quest desync problems that killed the "re-roll completed"
idea. It only *adds* a new quest.

**Sketch (not yet designed in detail):**
- `/api/quests/completed?profileId=` lists the profile's Success-status quests (name, map,
  original reward summary).
- `POST /api/replay { profileId, questId }`:
  1. Clone the source quest definition; assign a fresh MongoId (like ShinyAirdropGuns / the
     procedural generator already mint ids). Clone its locale entries under the new id so it
     reads identically.
  2. Randomize the **rewards** (reuse the reward-pool logic in `QuestGeneration.AddRewardsToQuest`
     / `misc.jsonc rewards`). Optionally re-randomize conditions too, or keep them identical —
     a decision for when this phase is designed.
  3. Add it to the profile as an available/started quest and to that profile's randomization
     cache so it survives restarts.
  4. `requiresRestart: true`.

**Open design questions for Phase 2 (defer until Phase 1 ships):**
- Should the fake quest re-use the original's conditions verbatim, or re-randomize them?
- Prerequisite/loyalty gating — spawn it unlocked, or mirror the original's start conditions?
- Repeatability — one fake copy at a time per source, or allow stacking?
- Cleanup — can you delete a fake quest you no longer want?
- How it interacts with the normal per-profile generation pass (it's a profile-specific extra
  quest, not a vanilla one — needs to persist in the cache and not get regenerated away).
