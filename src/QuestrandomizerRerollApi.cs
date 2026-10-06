using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Leaves_Questrandomizer;

// Phase 1 of the runtime re-roll feature (see REROLL_SPEC.md). An IHttpListener exposes a small
// JSON API the (future) web page calls. Browser-facing: these requests carry NO game session, so
// every endpoint takes an EXPLICIT profileId - the effect stays per-profile regardless (§3 of the
// spec). Registered automatically by being [Injectable]; SPT routes any request we CanHandle here.
[Injectable]
public class QuestrandomizerRerollApi(
    Questrandomizer questrandomizer,
    ProfileHelper profileHelper,
    QuestHelper questHelper,
    SaveServer saveServer,
    ProfileActivityService profileActivityService,
    ModHelper modHelper,
    JsonUtil jsonUtil,
    Leaves.JsonIdAnnotator jsonIdAnnotator)
    : IHttpListener, IOnLoad
{
    private const string Prefix = "/questrandomizer/";

    // Publish our routes to the suite-wide Dev directory (served at /webui/dev/routes). Utils.Instance is set well before any OnLoad.
    public Task OnLoad()
    {
        var routes = new List<Leaves.RouteInfo>
        {
            // The /questrandomizer/ui* page + asset files are served by the Leaves-WebUI mod now, which
            // registers them under "Web UI (static files)". Left commented so the move is visible.
            //   new Leaves.RouteInfo("GET", "/questrandomizer/ui", "Main web page.", "ui"),
            //   new Leaves.RouteInfo("GET", "/questrandomizer/ui/handbook", "Handbook page.", "ui"),
            //   new Leaves.RouteInfo("GET", "/questrandomizer/ui/info", "Cross-player turn-in Info page.", "ui"),
            //   new Leaves.RouteInfo("GET", "/questrandomizer/ui/debug", "Debug page (quest/outline/locale dumps).", "ui"),
            //   new Leaves.RouteInfo("GET", "/questrandomizer/ui/reroll", "Legacy alias -> permanent redirect to /ui.", "internal"),
            //   new Leaves.RouteInfo("GET", "/questrandomizer/ui/styles.css", "Web UI stylesheet.", "internal"),
            //   new Leaves.RouteInfo("GET", "/questrandomizer/ui/nav.json", "Nav-rail manifest for the shared rail.", "internal"),
            //   new Leaves.RouteInfo("GET", "/questrandomizer/ui/required-list.js", "Shared per-player turn-in list script.", "internal"),
            new Leaves.RouteInfo("GET", "/questrandomizer/api/handbook", "Handbook JSON the handbook page fetches.", "api"),
            new Leaves.RouteInfo("GET", "/questrandomizer/api/profiles", "Profile picker [{ profileId, nickname, level }].", "api"),
            new Leaves.RouteInfo("GET", "/questrandomizer/api/turnins", "Every outstanding turn-in across all visible profiles.", "api"),
            new Leaves.RouteInfo("GET", "/questrandomizer/api/quests/active?profileId=&admin=", "Rerollable active quests (admin=true includes blacklisted).", "api"),
            new Leaves.RouteInfo("GET", "/questrandomizer/api/quests/search?profileId=", "Every accepted quest, resolved for the search tab.", "api"),
            new Leaves.RouteInfo("GET", "/questrandomizer/api/quest/description?profileId=&questId=", "One quest's description text.", "api"),
            new Leaves.RouteInfo("GET", "/questrandomizer/api/debug/quests?profileId=", "The profile's live in-memory quest table (annotated).", "api"),
            new Leaves.RouteInfo("GET", "/questrandomizer/api/debug/browse?profileId=", "Every quest in the served table, resolved.", "api"),
            new Leaves.RouteInfo("GET", "/questrandomizer/api/debug/outlines?profileId=", "The profile's stored outlines (roller decisions).", "api"),
            new Leaves.RouteInfo("GET", "/questrandomizer/api/debug/locale?profileId=", "The profile's live English locale blob (annotated).", "api"),
            new Leaves.RouteInfo("GET", "/questrandomizer/api/debug/handins?profileId=", "Every hand-in in the served tree, regardless of status.", "api"),
            new Leaves.RouteInfo("GET", "/questrandomizer/api/vanilla-names", "Vanilla item name/shortname -> full name map.", "api"),
            new Leaves.RouteInfo("GET", "/questrandomizer/api/history", "Web UI action history (re-rolls / verifies), newest first.", "api"),
            new Leaves.RouteInfo("POST", "/questrandomizer/api/reroll", "Re-roll quests { profileId, questIds } and RESET their progress.", "api", true),
            new Leaves.RouteInfo("POST", "/questrandomizer/api/reroll-locked", "Re-roll every not-yet-accepted quest { profileId }.", "api", true),
            new Leaves.RouteInfo("POST", "/questrandomizer/api/verify", "Heal the one-session turn-in desync { profileId } (safe repair).", "api"),
        };
        Leaves.Utils.Instance.RegisterRoutes("Quest Randomizer", routes);
        return Task.CompletedTask;
    }

    // The web pages + stylesheet + page scripts (questrandomizer.html, handbook/info/debug.html,
    // styles.css, nav.json, required-list.txt) moved to the Leaves-WebUI mod, which serves every
    // Leaves mod's static UI files. This listener is the JSON API only now. See WEBUI_EXTRACTION_PLAN.md.
    //   private readonly string uiDir = System.IO.Path.Combine(
    //       modHelper.GetAbsolutePathToModFolder(System.Reflection.Assembly.GetExecutingAssembly()),
    //       "assets", "ui");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Page paths now served by the Leaves-WebUI mod. Excluded here so exactly one listener claims each
    // (listener dispatch order is not guaranteed). Everything else under /questrandomizer/ - the whole
    // /questrandomizer/api/ surface - is still ours.
    private static readonly HashSet<string> MovedToWebUiMod = new(StringComparer.OrdinalIgnoreCase)
    {
        "/questrandomizer/ui",
        "/questrandomizer/ui/reroll",
        "/questrandomizer/ui/handbook",
        "/questrandomizer/ui/info",
        "/questrandomizer/ui/debug",
        "/questrandomizer/ui/styles.css",
        "/questrandomizer/ui/nav.json",
        "/questrandomizer/ui/required-list.js",
    };

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        // The JSON api under /questrandomizer/api/. "/questrandomizer/" is ours alone, so this never
        // shadows an SPT route; the /questrandomizer/ui* page files are served by Leaves-WebUI.
        if (!Leaves.HttpUtils.HasPrefix(context, Prefix))
        {
            return false;
        }
        string path = (context.Request.Path.Value ?? "").TrimEnd('/');
        return !MovedToWebUiMod.Contains(path);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        string path = context.Request.Path.Value ?? "";
        string method = context.Request.Method;

        try
        {
            // --- Page/asset static serves moved to the Leaves-WebUI mod (WEBUI_EXTRACTION_PLAN.md).
            //     Kept commented; CanHandle already routes these paths elsewhere. ---
            //   "/questrandomizer/ui"                   -> questrandomizer/questrandomizer.html
            //   "/questrandomizer/ui/reroll"            -> 301 redirect to /questrandomizer/ui
            //   "/questrandomizer/ui/handbook"          -> questrandomizer/handbook.html
            //   "/questrandomizer/ui/info"              -> questrandomizer/info.html
            //   "/questrandomizer/ui/debug"             -> questrandomizer/debug.html
            //   "/questrandomizer/ui/styles.css"        -> questrandomizer/styles.css
            //   "/questrandomizer/ui/nav.json"          -> questrandomizer/nav.json
            //   "/questrandomizer/ui/required-list.js"  -> questrandomizer/required-list.txt
            //
            // This branch is the JSON data the handbook page fetches, at /questrandomizer/api/handbook
            // (the old "must sit after /ui/handbook" ordering note no longer applies - that route is gone).
            if (method == "GET" && path.EndsWith("/handbook", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJson(context, StatusCodes.Status200OK, questrandomizer.GetHandbookData());
            }
            else if (method == "GET" && path.EndsWith("/profiles", StringComparison.OrdinalIgnoreCase))
            {
                await HandleProfiles(context);
            }
            else if (method == "GET" && path.EndsWith("/turnins", StringComparison.OrdinalIgnoreCase))
            {
                await HandleTurnIns(context);
            }
            else if (method == "GET" && path.EndsWith("/quests/active", StringComparison.OrdinalIgnoreCase))
            {
                await HandleActiveQuests(context);
            }
            else if (method == "GET" && path.EndsWith("/quests/search", StringComparison.OrdinalIgnoreCase))
            {
                await HandleQuestSearch(context);
            }
            else if (method == "GET" && path.EndsWith("/quest/description", StringComparison.OrdinalIgnoreCase))
            {
                await HandleQuestDescription(context);
            }
            else if (method == "GET" && path.EndsWith("/debug/quests", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDebugQuests(context);
            }
            else if (method == "GET" && path.EndsWith("/debug/browse", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDebugBrowse(context);
            }
            else if (method == "GET" && path.EndsWith("/debug/outlines", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDebugOutlines(context);
            }
            else if (method == "GET" && path.EndsWith("/debug/locale", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDebugLocale(context);
            }
            else if (method == "GET" && path.EndsWith("/debug/handins", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDebugHandins(context);
            }
            else if (method == "GET" && path.EndsWith("/vanilla-names", StringComparison.OrdinalIgnoreCase))
            {
                await HandleVanillaNames(context);
            }
            else if (method == "GET" && path.EndsWith("/history", StringComparison.OrdinalIgnoreCase))
            {
                await HandleHistory(context);
            }
            else if (method == "POST" && path.EndsWith("/reroll-locked", StringComparison.OrdinalIgnoreCase))
            {
                await HandleRerollLocked(context);
            }
            else if (method == "POST" && path.EndsWith("/reroll", StringComparison.OrdinalIgnoreCase))
            {
                await HandleReroll(context);
            }
            else if (method == "POST" && path.EndsWith("/verify", StringComparison.OrdinalIgnoreCase))
            {
                await HandleVerify(context);
            }
            else
            {
                await WriteJson(context, StatusCodes.Status404NotFound, new { error = "Unknown re-roll endpoint." });
            }
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[Questrandomizer] Re-roll API error on {method} {path}: {ex.Message}", LogTextColor.Red);
            await WriteJson(context, StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    // GET /questrandomizer/api/profiles -> [{ profileId, nickname, level }]
    private async Task HandleProfiles(HttpContext context)
    {
        var profiles = new List<object>();
        foreach (var profileId in saveServer.GetProfiles().Keys)
        {
            var pmc = profileHelper.GetPmcProfile(profileId);
            if (pmc?.Info?.Nickname is not { Length: > 0 } nickname)
            {
                continue; // incomplete / not-yet-created profile
            }
            if (questrandomizer.ShouldHideProfileFromWebUi(nickname))
            {
                continue; // utility/alt account (headless, dev) - hidden per config
            }
            profiles.Add(new { profileId = profileId.ToString(), nickname, level = pmc.Info.Level ?? 0 });
        }
        await WriteJson(context, StatusCodes.Status200OK, profiles);
    }

    // GET /questrandomizer/api/turnins
    //   -> [{ player, questName, itemIds, itemNames, fir, remaining }]
    // Every outstanding turn-in across EVERY visible profile's active quests - the cross-player shopping
    // list the Info page renders. Uses the same profile-visibility filter as /profiles, so utility/alt
    // accounts (headless, dev) are excluded. Per-profile amounts already subtract what's been handed in.
    private async Task HandleTurnIns(HttpContext context)
    {
        var needs = new List<object>();
        foreach (var profileId in saveServer.GetProfiles().Keys)
        {
            var pmc = profileHelper.GetPmcProfile(profileId);
            if (pmc?.Info?.Nickname is not { Length: > 0 } nickname)
            {
                continue; // incomplete / not-yet-created profile
            }
            if (questrandomizer.ShouldHideProfileFromWebUi(nickname))
            {
                continue; // utility/alt account (headless, dev) - hidden per config
            }
            foreach (var need in questrandomizer.GetTurnInNeedsForProfile(pmc))
            {
                needs.Add(new
                {
                    player = nickname,
                    questName = need.QuestName,
                    itemIds = need.ItemIds,
                    itemNames = need.ItemNames,
                    fir = need.Fir,
                    remaining = need.Remaining
                });
            }
        }
        await WriteJson(context, StatusCodes.Status200OK, needs);
    }

    // GET /questrandomizer/api/quests/active?profileId=X -> [{ questId, name }]
    private async Task HandleActiveQuests(HttpContext context)
    {
        if (!TryGetProfile(context.Request.Query["profileId"], out var pmc, out var error))
        {
            await WriteJson(context, StatusCodes.Status404NotFound, new { error });
            return;
        }

        // admin=true lists EVERY active quest (blacklisted / never-rolled included) for force re-roll.
        bool admin = string.Equals(context.Request.Query["admin"], "true", StringComparison.OrdinalIgnoreCase);
        var quests = questrandomizer.GetRerollableActiveQuests(pmc!, admin)
            .Select(q => new { questId = q.QuestId.ToString(), name = q.Name, image = q.Image, trader = q.Trader, traderAvatar = q.TraderAvatar, objectives = q.Objectives, previous = q.Previous, leadsTo = q.LeadsTo, rewards = q.Rewards });
        await WriteJson(context, StatusCodes.Status200OK, quests);
    }

    // GET /questrandomizer/api/quests/search?profileId=X
    //   -> [{ questId, name, image, trader, objectives: [...] }] for every ACCEPTED quest.
    // Read-only reference data for the web UI's search tab; the filtering itself is done client-side.
    private async Task HandleQuestSearch(HttpContext context)
    {
        if (!TryGetProfile(context.Request.Query["profileId"], out var pmc, out var error))
        {
            await WriteJson(context, StatusCodes.Status404NotFound, new { error });
            return;
        }

        var quests = questrandomizer.GetActiveQuestDetails(pmc!)
            .Select(q => new
            {
                questId = q.QuestId.ToString(),
                name = q.Name,
                image = q.Image,
                trader = q.Trader,
                traderAvatar = q.TraderAvatar,
                objectives = q.Objectives,
                previous = q.Previous,
                leadsTo = q.LeadsTo,
                rewards = q.Rewards
            });
        await WriteJson(context, StatusCodes.Status200OK, quests);
    }

    // GET /questrandomizer/api/quest/description?profileId=X&questId=Y -> { description }
    // Lazily fetched per quest by the web UI's description toggle, so the bulk quests payload stays free
    // of every quest's full description text (see GetQuestDescription).
    private async Task HandleQuestDescription(HttpContext context)
    {
        if (!TryGetProfile(context.Request.Query["profileId"], out var pmc, out var error))
        {
            await WriteJson(context, StatusCodes.Status404NotFound, new { error });
            return;
        }
        if (context.Request.Query["questId"].ToString() is not { Length: > 0 } questIdRaw)
        {
            await WriteJson(context, StatusCodes.Status400BadRequest, new { error = "Missing questId." });
            return;
        }
        string description = questrandomizer.GetQuestDescription(pmc!, new MongoId(questIdRaw));
        await WriteJson(context, StatusCodes.Status200OK, new { description });
    }

    // GET /questrandomizer/api/debug/quests?profileId=X
    //   -> { generated, quests: [{ id, name, jsonc }] }
    // The profile's LIVE in-memory quest table (what the game is served), one entry per quest so the
    // page can group by quest. Each quest is serialized with SPT's own serializer, then run through
    // the JsonIdAnnotator so raw MongoIds carry a "// what this is" comment. Read-only debug view.
    private async Task HandleDebugQuests(HttpContext context)
    {
        if (!TryGetProfile(context.Request.Query["profileId"], out var pmc, out var error))
        {
            await WriteJson(context, StatusCodes.Status404NotFound, new { error });
            return;
        }

        var profileId = pmc!.Id!.Value;
        var (quests, enLocale) = questrandomizer.GetInMemoryProfileData(profileId);

        var list = new List<object>();
        foreach (var (questId, quest) in quests)
        {
            string raw = jsonUtil.Serialize(quest, true) ?? "";
            string annotated = jsonIdAnnotator.Annotate(raw, "en");

            // Prefer the journal name (what the player sees), which for a re-rolled quest is the
            // profile's OWN rolled locale; fall back to the shared locale, then the internal label.
            string name;
            if (enLocale.TryGetValue($"{questId} name", out var rolledName))
            {
                name = rolledName;
            }
            else
            {
                name = Leaves.Utils.Instance.getLocale("en", questId.ToString(), " name") ?? quest.QuestName ?? questId.ToString();
            }

            list.Add(new { id = questId.ToString(), name = Leaves.Utils.Instance.RTT_Strip(name), jsonc = annotated });
        }

        await WriteJson(context, StatusCodes.Status200OK, new { generated = quests.Count > 0, quests = list });
    }

    // GET /questrandomizer/api/debug/browse?profileId=X
    //   -> [{ questId, name, image, trader, objectives: [...] }] for EVERY quest in the profile's
    // served table (not just accepted ones), objective text resolved but no completion state. Feeds
    // the debug "Browse" tab, which renders quests the same human-readable way the re-roll search does.
    private async Task HandleDebugBrowse(HttpContext context)
    {
        if (!TryGetProfile(context.Request.Query["profileId"], out var pmc, out var error))
        {
            await WriteJson(context, StatusCodes.Status404NotFound, new { error });
            return;
        }

        var profileId = pmc!.Id!.Value;
        var quests = questrandomizer.GetAllProfileQuestDetails(profileId)
            .Select(q => new
            {
                questId = q.QuestId.ToString(),
                name = q.Name,
                image = q.Image,
                trader = q.Trader,
                traderAvatar = q.TraderAvatar,
                objectives = q.Objectives,
                previous = q.Previous,
                leadsTo = q.LeadsTo,
                rewards = q.Rewards
            });
        await WriteJson(context, StatusCodes.Status200OK, quests);
    }

    // GET /questrandomizer/api/debug/outlines?profileId=X
    //   -> { generated, quests: [{ id, name, jsonc }] }
    // This profile's stored OUTLINES - the decisions the roller made, the source of truth its quests
    // are rebuilt from. One entry per rolled quest, grouped exactly like the quest dump. Same
    // serialize + annotate treatment so raw ids in the decisions read plainly.
    private async Task HandleDebugOutlines(HttpContext context)
    {
        if (!TryGetProfile(context.Request.Query["profileId"], out var pmc, out var error))
        {
            await WriteJson(context, StatusCodes.Status404NotFound, new { error });
            return;
        }

        var profileId = pmc!.Id!.Value;
        var (outlines, enLocale) = questrandomizer.GetOutlineDataForDebug(profileId);

        var list = new List<object>();
        foreach (var (questId, outline) in outlines)
        {
            string raw = jsonUtil.Serialize(outline, true) ?? "";
            string annotated = jsonIdAnnotator.Annotate(raw, "en");

            string name;
            if (enLocale.TryGetValue($"{questId} name", out var rolledName))
            {
                name = rolledName;
            }
            else
            {
                name = Leaves.Utils.Instance.getLocale("en", questId.ToString(), " name") ?? questId.ToString();
            }

            list.Add(new { id = questId.ToString(), name = Leaves.Utils.Instance.RTT_Strip(name), jsonc = annotated });
        }

        await WriteJson(context, StatusCodes.Status200OK, new { generated = outlines.Count > 0, quests = list });
    }

    // GET /questrandomizer/api/debug/locale?profileId=X
    //   -> { generated, text, bytes }
    // The profile's LIVE English locale (the full game locale + this mod's per-profile overrides), as
    // one annotated blob. Can't be split by quest in any useful way, so it's served whole; the page
    // searches it line-by-line and can download it for browsing in an editor.
    private async Task HandleDebugLocale(HttpContext context)
    {
        if (!TryGetProfile(context.Request.Query["profileId"], out var pmc, out var error))
        {
            await WriteJson(context, StatusCodes.Status404NotFound, new { error });
            return;
        }

        var profileId = pmc!.Id!.Value;
        var (_, enLocale) = questrandomizer.GetInMemoryProfileData(profileId);

        string raw = jsonUtil.Serialize(enLocale, true) ?? "";
        string annotated = jsonIdAnnotator.Annotate(raw, "en");

        await WriteJson(context, StatusCodes.Status200OK, new
        {
            generated = enLocale.Count > 0,
            text = annotated,
            bytes = System.Text.Encoding.UTF8.GetByteCount(annotated)
        });
    }

    // GET /questrandomizer/api/debug/handins?profileId=X
    //   -> [{ player, questName, itemIds, itemNames, fir, remaining }]
    // EVERY HandoverItem turn-in in the profile's served quest tree, regardless of quest status or
    // progress (locked / active / completed / failed alike), quest items excluded. Feeds the debug
    // "All hand-ins" tab, which renders it with the SAME per-player listing the Info page uses.
    // Remaining is the FULL required count (nothing is subtracted here). The "player" field is the one
    // selected profile's nickname so the shared listing groups it into a single card.
    private async Task HandleDebugHandins(HttpContext context)
    {
        if (!TryGetProfile(context.Request.Query["profileId"], out var pmc, out var error))
        {
            await WriteJson(context, StatusCodes.Status404NotFound, new { error });
            return;
        }

        var profileId = pmc!.Id!.Value;
        string player = pmc.Info?.Nickname is { Length: > 0 } nickname ? nickname : profileId.ToString();

        var needs = new List<object>();
        foreach (var need in questrandomizer.GetAllTurnInsForProfile(profileId))
        {
            needs.Add(new
            {
                player,
                questName = need.QuestName,
                itemIds = need.ItemIds,
                itemNames = need.ItemNames,
                fir = need.Fir,
                remaining = need.Remaining
            });
        }
        await WriteJson(context, StatusCodes.Status200OK, needs);
    }

    // Cached response for /vanilla-names - the vanilla DB never changes while the server runs.
    private string? vanillaNamesJson;

    // GET /questrandomizer/api/vanilla-names -> { "display name": "full item name", ... } for every
    // VANILLA item, keyed by BOTH the full Name and the ShortName (quest text uses either - mystery
    // arsenals print ShortNames like "PM (t)"). The value is always the FULL name, because
    // tarkov.dev's item slug is derived from it ("PM (t)" lives at /item/makarov-pm-t-9x18pm-pistol,
    // not /item/pm-t). Read from the ON-DISK database (SPT_Data), which is the game's pre-mod state -
    // the same "vanilla snapshot" trick BarterEconomy uses. The web UI uses this to only link item
    // names that exist on tarkov.dev; modded items (absent here) render as plain text, not dead links.
    private async Task HandleVanillaNames(HttpContext context)
    {
        if (vanillaNamesJson == null)
        {
            string modPath = modHelper.GetAbsolutePathToModFolder(System.Reflection.Assembly.GetExecutingAssembly());
            string? root = Directory.GetParent(modPath)?.Parent?.Parent?.FullName;
            string itemsFile = System.IO.Path.Combine(root ?? "", "SPT_Data", "database", "templates", "items.json");
            string localeFile = System.IO.Path.Combine(root ?? "", "SPT_Data", "database", "locales", "global", "en.json");
            var names = new Dictionary<string, string>();
            if (File.Exists(itemsFile) && File.Exists(localeFile))
            {
                using var items = JsonDocument.Parse(await File.ReadAllTextAsync(itemsFile));
                using var locale = JsonDocument.Parse(await File.ReadAllTextAsync(localeFile));
                var localeRoot = locale.RootElement;
                foreach (var item in items.RootElement.EnumerateObject())
                {
                    if (!item.Value.TryGetProperty("_type", out var type) || type.GetString() != "Item"
                        || !localeRoot.TryGetProperty($"{item.Name} Name", out var name)
                        || name.GetString() is not { Length: > 0 } fullName)
                    {
                        continue;
                    }
                    names[fullName] = fullName;
                    if (localeRoot.TryGetProperty($"{item.Name} ShortName", out var shortName)
                        && shortName.GetString() is { Length: > 0 } sn)
                    {
                        // TryAdd: a ShortName must never overwrite another item's FULL name, and on
                        // short-name collisions ("AK-12"-style dupes) the first item simply wins.
                        names.TryAdd(sn, fullName);
                    }
                }
            }
            else
            {
                Leaves.Utils.Instance.printColor("[Questrandomizer] vanilla-names: on-disk SPT_Data not found - web UI will link every item.", LogTextColor.Red);
            }
            vanillaNamesJson = JsonSerializer.Serialize(names);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(vanillaNamesJson);
    }

    // GET /questrandomizer/api/history -> [{ time, nickname, action, quests, admin, ip }] newest first.
    private async Task HandleHistory(HttpContext context)
    {
        var entries = questrandomizer.GetWebUiHistory()
            .OrderByDescending(e => e.Time)
            .Select(e => new { time = e.Time, nickname = e.Nickname, action = e.Action, quests = e.Quests, admin = e.Admin, ip = e.Ip });
        await WriteJson(context, StatusCodes.Status200OK, entries);
    }

    // Which machine made this request. The page has no auth, so this is the only thing separating
    // "someone on the LAN/ZeroTier re-rolled X" from "the host did". IPv4-mapped IPv6 addresses
    // (::ffff:10.144.x.x) are unwrapped, and loopback is normalized to 127.0.0.1 so a local click
    // doesn't log as the less-obvious "::1".
    private static string ClientIp(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip == null)
        {
            return "?";
        }
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        return System.Net.IPAddress.IsLoopback(ip) ? "127.0.0.1" : ip.ToString();
    }

    // POST /questrandomizer/api/reroll { profileId, questIds: [...] } -> re-roll + reset progress
    private async Task HandleReroll(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        string body = await reader.ReadToEndAsync();
        var request = JsonSerializer.Deserialize<RerollRequest>(body, Json);

        if (request?.ProfileId is not { Length: > 0 } || request.QuestIds is not { Count: > 0 })
        {
            await WriteJson(context, StatusCodes.Status400BadRequest, new { error = "Body must be { profileId, questIds: [...] }." });
            return;
        }

        if (!TryGetProfile(request.ProfileId, out var pmc, out var error))
        {
            await WriteJson(context, StatusCodes.Status404NotFound, new { error });
            return;
        }

        var profileId = pmc!.Id!.Value;
        var requestedIds = request.QuestIds.Select(id => new MongoId(id)).ToList();

        // Snapshot profile + generated data BEFORE touching either. Flush the in-memory profile first
        // so the zip captures the state as it is RIGHT NOW rather than whatever was last auto-saved.
        await saveServer.SaveProfileAsync(profileId);
        string? backupPath = questrandomizer.CreateWebUiBackup(profileId, "reroll", pmc.Info?.Nickname);

        // Resolve display names BEFORE the re-roll for the history log - the roll rewrites them, and
        // "Leaves re-rolled Kill 9 Enemies" should show the quest as it was when they clicked.
        var namesBefore = questrandomizer.ResolveQuestNamesForWebUi(profileId, requestedIds);
        var nameById = requestedIds.Zip(namesBefore).ToDictionary(p => p.First, p => p.Second);

        // Re-roll the DEFINITION side (cache surgery + regeneration, under the per-profile gate).
        // request.Admin = user opted into the override to force-roll blacklisted/protected quests.
        var rerolled = questrandomizer.RerollProfileQuests(profileId, requestedIds, request.Admin);

        if (rerolled.Count > 0)
        {
            questrandomizer.AppendWebUiHistory(
                pmc.Info?.Nickname ?? "?", "reroll",
                rerolled.Select(id => nameById.GetValueOrDefault(id, id.ToString())).ToList(),
                request.Admin, ClientIp(context));
        }

        // Reset the PROGRESS side for each re-rolled quest: back to Started (clears completed
        // conditions) plus purge its live counters, which ResetQuestState does NOT touch. Without
        // this, in-flight counters would track the OLD conditions against the NEW ones (spec §1).
        // TaskConditionCounters is nullable - a profile that has never progressed a counter simply
        // has none, which just means there's nothing to purge.
        var counters = pmc.TaskConditionCounters;
        foreach (var questId in rerolled)
        {
            questHelper.ResetQuestState(pmc, QuestStatusEnum.Started, questId);
            if (counters == null)
            {
                continue;
            }
            foreach (var counterId in counters
                         .Where(counter => counter.Value.SourceId == questId)
                         .Select(counter => counter.Key)
                         .ToList())
            {
                counters.Remove(counterId);
            }
        }

        if (rerolled.Count > 0)
        {
            await saveServer.SaveProfileAsync(profileId);
        }

        // Soft warning only: we can't yet reliably tell "in raid" from "at menu" (spec: raid guard
        // is a refinement). Progress is reset regardless, and the change takes effect on restart.
        string? warning = profileActivityService.ContainsActiveProfile(profileId)
            ? "This profile looks like it's currently in-game. If you're in a raid, finish/leave it first - the re-roll applies after a client restart."
            : null;

        await WriteJson(context, StatusCodes.Status200OK, new
        {
            ok = true,
            rerolled = rerolled.Select(id => id.ToString()),
            requiresRestart = true,
            warning,
            backup = backupPath == null ? null : System.IO.Path.GetFileName(backupPath)
        });
    }

    // POST /questrandomizer/api/reroll-locked { profileId, admin } -> re-roll EVERY quest the player
    // hasn't accepted, completed, or failed. A maintenance action for re-applying the current config
    // (e.g. after a weapon-category change) to quests not yet in progress. Unlike /reroll it does NOT
    // reset any progress: the target quests aren't accepted, so they have no counters, and flipping a
    // locked quest to Started would wrongly "accept" it. Applies after a client restart.
    private async Task HandleRerollLocked(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        string body = await reader.ReadToEndAsync();
        var request = JsonSerializer.Deserialize<RerollLockedRequest>(body, Json);

        if (request?.ProfileId is not { Length: > 0 })
        {
            await WriteJson(context, StatusCodes.Status400BadRequest, new { error = "Body must be { profileId }." });
            return;
        }
        if (!TryGetProfile(request.ProfileId, out var pmc, out var error))
        {
            await WriteJson(context, StatusCodes.Status404NotFound, new { error });
            return;
        }

        var profileId = pmc!.Id!.Value;
        var lockedIds = questrandomizer.GetLockedRerollableQuestIds(pmc);

        if (lockedIds.Count == 0)
        {
            await WriteJson(context, StatusCodes.Status200OK, new
            {
                ok = true,
                count = 0,
                rerolled = Array.Empty<string>(),
                requiresRestart = false,
                message = "No locked quests to re-roll - everything is accepted, completed, failed, or not yet rolled."
            });
            return;
        }

        // Snapshot profile + generated data BEFORE touching either, exactly like the targeted paths.
        await saveServer.SaveProfileAsync(profileId);
        string? backupPath = questrandomizer.CreateWebUiBackup(profileId, "reroll-locked", pmc.Info?.Nickname);

        // Definition-side only: cache surgery + regeneration under the per-profile gate. Deliberately
        // NO progress reset (see method comment) - these quests carry no live counters. Never admin:
        // this action always respects the blacklist (GetLockedRerollableQuestIds already excludes it).
        var rerolled = questrandomizer.RerollProfileQuests(profileId, lockedIds);

        if (rerolled.Count > 0)
        {
            // A bulk refresh can hit hundreds of quests - store a one-line summary, not a name wall.
            questrandomizer.AppendWebUiHistory(
                pmc.Info?.Nickname ?? "?", "reroll",
                new List<string> { $"all locked quests ({rerolled.Count})" },
                false, ClientIp(context));
        }

        string? warning = profileActivityService.ContainsActiveProfile(profileId)
            ? "This profile looks like it's currently in-game. The re-rolled quests apply after a client restart."
            : null;

        await WriteJson(context, StatusCodes.Status200OK, new
        {
            ok = true,
            count = rerolled.Count,
            rerolled = rerolled.Select(id => id.ToString()),
            requiresRestart = true,
            warning,
            backup = backupPath == null ? null : System.IO.Path.GetFileName(backupPath)
        });
    }

    // POST /questrandomizer/api/verify { profileId } -> { ok, count, healed: [names] }
    // Heals the one-session desync (every objective ticked but the Complete button greyed out) for a
    // profile on demand, so a player doesn't have to restart the server to un-stick a quest. Runs the
    // same repair the boot path does; only quests ALREADY wounded are touched, and healthy one-session
    // quests are left alone. The heal writes live counters - the player reopens the trader/quest screen
    // (no client restart) to see the Complete button light up.
    private async Task HandleVerify(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        string body = await reader.ReadToEndAsync();
        var request = JsonSerializer.Deserialize<VerifyRequest>(body, Json);

        if (request?.ProfileId is not { Length: > 0 })
        {
            await WriteJson(context, StatusCodes.Status400BadRequest, new { error = "Body must be { profileId }." });
            return;
        }
        if (!TryGetProfile(request.ProfileId, out var pmc, out var error))
        {
            await WriteJson(context, StatusCodes.Status404NotFound, new { error });
            return;
        }

        var profileId = pmc!.Id!.Value;
        var healed = questrandomizer.VerifyAndRepairProfile(profileId);

        // Shared audit log, like re-rolls: record who healed what, so the coop group can see it. Only
        // when something was actually fixed - a clean verify is noise.
        if (healed.Count > 0)
        {
            questrandomizer.AppendWebUiHistory(
                pmc.Info?.Nickname ?? "?", "verify", healed, false, ClientIp(context));
        }

        await WriteJson(context, StatusCodes.Status200OK, new
        {
            ok = true,
            count = healed.Count,
            healed
        });
    }

    private bool TryGetProfile(string? profileIdRaw, out PmcData? pmc, out string? error)
    {
        pmc = null;
        error = null;
        if (string.IsNullOrWhiteSpace(profileIdRaw))
        {
            error = "Missing profileId.";
            return false;
        }
        var profileId = new MongoId(profileIdRaw);
        if (!saveServer.ProfileExists(profileId))
        {
            error = $"No profile with id {profileIdRaw}.";
            return false;
        }
        pmc = profileHelper.GetPmcProfile(profileId);
        if (pmc?.Id == null)
        {
            error = $"Profile {profileIdRaw} has no PMC character yet.";
            return false;
        }
        return true;
    }

    // Thin wrapper over Leaves.HttpUtils.WriteJson capturing this mod's camelCase serializer options,
    // so the ~30 call sites below stay short. (ServeUiFile moved out with the pages - Leaves-WebUI.)
    private static async Task WriteJson(HttpContext context, int statusCode, object payload)
    {
        await Leaves.HttpUtils.WriteJson(context, statusCode, payload, Json);
    }

    //   private async Task ServeUiFile(HttpContext context, string fileName, string contentType)
    //   {
    //       await Leaves.HttpUtils.ServeFile(context, System.IO.Path.Combine(uiDir, fileName), contentType);
    //   }


    private sealed record RerollRequest
    {
        public string? ProfileId { get; init; }
        public List<string>? QuestIds { get; init; }

        // Admin override: force-roll even blacklisted / whitelist-excluded quests. Defaults false, so
        // an old client that doesn't send it behaves exactly as before.
        public bool Admin { get; init; }
    }

    private sealed record RerollLockedRequest
    {
        public string? ProfileId { get; init; }
        // No admin field on purpose: the bulk locked re-roll always respects the blacklist.
    }

    private sealed record VerifyRequest
    {
        public string? ProfileId { get; init; }
    }
}
