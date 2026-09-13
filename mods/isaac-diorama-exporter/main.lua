--[[ Isaac Diorama Exporter - Phase 2b ----------------------------------------
  Streams the room each render frame, now with enough to draw REAL sprites.

  Two packet types on UDP 127.0.0.1:47800 (needs --luadebug):

  MSG_STRINGS (3): registers interned strings (anm2 paths, animation names)
     header: magic,ver,type(3),flags, count(u16)
     each:   id(u16), len(u16), utf8 bytes
     (resent in full every ~2s so a dropped datagram self-heals)

  MSG_SCENE2 (4): the room
     header: magic,ver,type(4),flags, frame(u32), roomMin/Max XY(4xf32), count(u16)
     each entity (32 bytes):
        x(f32), y(f32), cat(u8), eflags(u8: bit0 flipX),
        scale(f32), heightPx(f32),
        anm2Id(u16), animId(u16), animFrame(u16),
        r(u8),g(u8),b(u8),a(u8), rotationDeg(f32)
----------------------------------------------------------------------------]]

local MOD_NAME = "IsaacDioramaExporter"
local mod      = RegisterMod(MOD_NAME, 1)
local HOST, PORT    = "127.0.0.1", 47800
local PROTO_VERSION = 1
local MSG_STRINGS, MSG_SCENE2, MSG_BACKDROP, MSG_SCENE3, MSG_IMG, MSG_HUD = 3, 4, 5, 6, 7, 8
local MSG_STATE = 10       -- game-state (menus/pause/cutscene) so the app can show the flat screen
local MSG_PRICES = 11      -- shop/devil prices per priced pickup (x,y,price)
local MSG_ITEMINFO = 12    -- pedestal collectible / trinket name (+ effect) per pickup
local MSG_LASERS   = 13    -- laser beams (brimstone/tech/etc): sample polyline + variant + radius
local MSG_WATER    = 14    -- water plane: amount, current, and the room's FX water colours
local MSG_STATS    = 15    -- player stat block + EID text for the nearest pickup
local STATS_REFRESH = 10   -- stats change slowly; 6Hz is plenty
local HUD_REFRESH = 10     -- send HUD state every N frames (it doesn't need 60Hz)
local IMG_CHUNK   = 8000   -- RGBA payload bytes per MSG_IMG datagram (atomic on loopback)
local MAX_LAYERS = 16   -- per entity, bounds packet size
local MAX_ENTITIES  = 512
local STRING_REFRESH_FRAMES = 60   -- resend whole string table this often

local C_PLAYER, C_ENEMY, C_PICKUP, C_PROJ, C_EFFECT, C_GRID, C_OTHER, C_DOOR = 0,1,2,3,4,5,6,7

-- Champion body colours, keyed by ChampionColor index (EntityNPC:GetChampionColorIdx).
-- The game recolours champion bodies via an internal palette that is NOT exposed on
-- Color (the sprite reports pure white), so we rebuild it here as an RGBA MULTIPLY
-- tint (255 = unchanged) — multiply preserves the sprite's own shading, matching how
-- the game tints them. Size-only champions (TINY/GIANT) and effect-only ones keep
-- white; TRANSPARENT/FLICKER/CAMO drop alpha. Values are eyeballed and tunable.
local CHAMPION_COLORS = {
  [0]  = {255, 70, 70,255},   -- RED
  [1]  = {255,236, 66,255},   -- YELLOW
  [2]  = { 90,220, 70,255},   -- GREEN
  [3]  = {255,150, 40,255},   -- ORANGE
  [4]  = { 90,120,255,255},   -- BLUE
  [5]  = { 90, 90, 90,255},   -- BLACK
  [6]  = {255,255,255,255},   -- WHITE (can't brighten via multiply; left as-is)
  [7]  = {160,160,170,255},   -- GREY
  [8]  = {255,255,255,128},   -- TRANSPARENT (spectral)
  [9]  = {255,255,255,150},   -- FLICKER (partly invisible)
  [10] = {255,130,200,255},   -- PINK
  [11] = {180, 80,230,255},   -- PURPLE
  [12] = {150, 30, 30,255},   -- DARK_RED
  [13] = {150,200,255,255},   -- LIGHT_BLUE
  [14] = {170,180,140,200},   -- CAMO
  [15] = { 90,220, 70,255},   -- PULSE_GREEN
  [16] = {160,160,170,255},   -- PULSE_GREY
  [17] = {255,255,255,255},   -- FLY_PROTECTED (halo, no recolour)
  [18] = {255,255,255,255},   -- TINY (scale only)
  [19] = {255,255,255,255},   -- GIANT (scale only)
  [20] = {255, 80, 80,255},   -- PULSE_RED
  [21] = {255,255,255,255},   -- SIZE_PULSE
  [22] = {255,236, 66,255},   -- KING (spawns yellow champions)
  [23] = {130,130,130,255},   -- DEATH
  [24] = {150,100, 55,255},   -- BROWN
  [25] = {255,255,255,255},   -- RAINBOW (TODO: cycle by frame)
}

----------------------------------------------------------------------------
local INPUT_PORT = 47801        -- app -> mod gamepad state
local udp = nil                 -- outbound (mod -> app)
local udpIn = nil               -- inbound gamepad (app -> mod)
do
  local ok, socket = pcall(require, "socket")
  if ok and socket then
    local ok2, sk = pcall(function() local u=socket.udp(); u:setpeername(HOST,PORT); return u end)
    if ok2 and sk then udp = sk; Isaac.DebugString(MOD_NAME..": UDP ready.")
    else Isaac.DebugString(MOD_NAME..": udp() failed: "..tostring(sk)) end
    local ok3, sk3 = pcall(function()
      local u = socket.udp(); u:setsockname(HOST, INPUT_PORT); u:settimeout(0); return u end)
    if ok3 and sk3 then udpIn = sk3; Isaac.DebugString(MOD_NAME..": input socket ready ("..INPUT_PORT..").")
    else Isaac.DebugString(MOD_NAME..": input socket failed: "..tostring(sk3)) end
  else Isaac.DebugString(MOD_NAME..": luasocket unavailable (need --luadebug).") end
end
local havePack = (type(string.pack) == "function")

-- REPENTOGON gives us the game's OWN resolved per-layer render state, including
-- spritesheets swapped in at runtime (costumes, pedestal items, rainbow poop) —
-- none of which exist in the .anm2 files on disk.
local HAS_RGON = false
do
  local ok = pcall(function() return REPENTOGON ~= nil end)
  if ok and REPENTOGON ~= nil then HAS_RGON = true end
  if not HAS_RGON then
    -- fall back to a capability probe
    local ok2 = pcall(function()
      local sp = Sprite()
      return sp.GetAllLayers ~= nil
    end)
    if ok2 then HAS_RGON = true end
  end
  Isaac.DebugString(MOD_NAME .. ": REPENTOGON " .. (HAS_RGON and "DETECTED (layer-state export)"
                                                             or "not found (anm2 export)"))
end

----------------------------------------------------------------------------
-- String interning ---------------------------------------------------------
local strMap  = {}     -- string -> id
local strList = {}     -- id -> string  (id starts at 0)
local nextId  = 0
local pending = {}     -- newly-added since last flush: {id, str}

local function intern(s)
  s = s or ""
  local id = strMap[s]
  if id ~= nil then return id end
  id = nextId; nextId = nextId + 1
  strMap[s] = id; strList[id] = s
  pending[#pending+1] = id
  return id
end

local function packStrings(ids, isFullRefresh)
  local parts = { string.pack("<BBBBH", 0x49, PROTO_VERSION, MSG_STRINGS,
                              isFullRefresh and 1 or 0, #ids) }
  for i = 1, #ids do
    local id = ids[i]; local s = strList[id]
    parts[#parts+1] = string.pack("<HH", id, #s) .. s
  end
  return table.concat(parts)
end

local function flushPending()
  if #pending == 0 then return end
  if udp then pcall(function() udp:send(packStrings(pending, false)) end) end
  pending = {}
end

local function refreshAllStrings()
  if nextId == 0 then return end
  local all = {}; for id = 0, nextId-1 do all[#all+1] = id end
  if udp then pcall(function() udp:send(packStrings(all, true)) end) end
  pending = {}
end

----------------------------------------------------------------------------
local frame = 0
local lastBackdropSig = nil
local roomChanged    = true   -- set by MC_POST_NEW_ROOM; forces a wall recapture
local dogmaLaserSeen = {}     -- one-shot log keys for DOGMA LASER diagnostics
-- Hit flash for Dogma's pieces. The TV (950.1) must be destroyed before Dogma itself can
-- be damaged, so it needs a visible reaction -- but the engine's damage flash for a boss
-- rides on an internal render palette we cannot read (the same wall as champion colours).
-- So detect it ourselves: remember each Dogma entity's HP and raise the hurt bit for a few
-- frames whenever it drops. Keyed by GetPtrHash, cleared on room change so it can't grow.
local dogmaHp, dogmaHurtUntil = {}, {}
local DOGMA_HURT_FRAMES = 6

-- Water Droplet (1000.41) — Downpour/Dross rain.
-- In a 2D game "falling" means travelling DOWN THE SCREEN, i.e. +Position.Y, which the
-- diorama maps to the floor's DEPTH axis — so the rain slid away from the camera instead
-- of falling. Nothing in Height/PositionOffset carries the arc (entHeight folds both
-- already), so we rebuild it from Position.Y travel.
--
-- The first cut pinned each drop's floor spot to its SPAWN position, which piled the whole
-- rain field along the back edge: they all spawn at the TOP of the room, so spawn Y carries
-- no useful depth at all. Instead each drop gets a STABLE pseudo-random spot across the
-- room, derived from its pointer hash — deterministic, never re-rolls, and it does not
-- touch Isaac's RNG (which would risk desyncing a run). Its X still comes from the game,
-- which is already scattered; only the depth and the fall height are synthesised.
local dropInfo = {}            -- hash -> { y = depth, sy = spawn Position.Y, h = fall, seen = frame }
local DROP_FALL    = 90        -- nominal starting height, game units
local DROP_FALL_VAR= 0.45      -- +/- fraction, so the rain isn't a marching grid
local DROP_PRUNE   = 300       -- frames between sweeps of dead drops (rain spawns constantly)
local dropPrunedAt = 0
local pillPopup = ""          -- popup text (pill effect OR item/trinket pickup)
local pillPopupUntil = -1     -- show the popup until this frame
-- Pickup popup state: remember the last queued item id so we can detect a NEW
-- collectible / trinket the moment it's queued, and pop its name + flavour text.
local lastQueuedItem = 0
local lastHeldLogged = 0     -- ptr hash of the last logged held entity (log once per pickup)
local pickupsPrimed = false
local playerHitUntil    = -1    -- flash + hide costumes until this frame after a hit
local lastPlayerCooldown = 0    -- to detect the frame a hit lands (cooldown jumps up)

-- Keyed by the backdrops.xml `id` (1-based, WITH GAPS) -- never by parse order.
-- backdrops.xml carries empty placeholder rows (`<backdrop id="29" />`, and 55/56/57) that
-- have no gfx. Appending only the rows that DO have a gfx collapsed 62 ids into 58 slots,
-- so every id above 28 resolved one row too low and Corpse I (34) handed the app
-- `planetarium.anm2`. Because the table has holes, `#BACKDROPS` is not its size -- keep a
-- real count separately.
local BACKDROPS  = {}
local BACKDROP_N = 0
do
  local paths = {
    "resources-dlc3/backdrops.xml", "resources/backdrops.xml",
    "extracted_resources/resources-dlc3/backdrops.xml",
    "extracted_resources/resources/backdrops.xml",
  }
  local text = nil
  for _, p in ipairs(paths) do
    local ok, f = pcall(io.open, p, "r")
    if ok and f then text = f:read("*a"); f:close(); if text and #text > 0 then break end end
  end
  if text then
    for attrs in text:gmatch("<backdrop([^>]*)>") do
      local t = {}
      for k, v in attrs:gmatch('([%w_]+)%s*=%s*"([^"]*)"') do t[k:lower()] = v end
      local id = tonumber(t.id or "")
      if t.gfx and id and id > 0 and id <= 4096 then
        BACKDROPS[id] = { name=t.name or "", gfx=t.gfx or "",
                          nfloor=t.nfloorgfx or "", door=t.door or "" }
        BACKDROP_N = BACKDROP_N + 1
      end
    end
    Isaac.DebugString(MOD_NAME..": parsed "..BACKDROP_N.." backdrops from backdrops.xml (id-keyed)")
  else
    Isaac.DebugString(MOD_NAME..": backdrops.xml NOT found; app falls back to its map")
  end
end

local function categorize(e)
  local t = e.Type
  if t == EntityType.ENTITY_PLAYER then return C_PLAYER end
  if t == EntityType.ENTITY_TEAR or t == EntityType.ENTITY_PROJECTILE
     or t == EntityType.ENTITY_BOMB then return C_PROJ end
  if t == EntityType.ENTITY_PICKUP then return C_PICKUP end
  if t == EntityType.ENTITY_EFFECT then return C_EFFECT end
  local ok, isEnemy = pcall(function() return e:IsEnemy() end)
  if ok and isEnemy then return C_ENEMY end
  return C_OTHER
end

local GRID_DRAW = {
  [GridEntityType.GRID_ROCK]=true,[GridEntityType.GRID_ROCKB]=true,
  [GridEntityType.GRID_ROCKT]=true,[GridEntityType.GRID_ROCK_BOMB]=true,
  [GridEntityType.GRID_ROCK_ALT]=true,[GridEntityType.GRID_ROCK_ALT2]=true,
  [GridEntityType.GRID_ROCK_SS]=true,[GridEntityType.GRID_POOP]=true,
  [GridEntityType.GRID_ROCK_SPIKED]=true,[GridEntityType.GRID_ROCK_GOLD]=true,
  [GridEntityType.GRID_TNT]=true,[GridEntityType.GRID_PILLAR]=true,
  [GridEntityType.GRID_SPIKES]=true,
  -- flat-on-floor grid pieces (cobwebs, pits, trapdoors, plates...)
  [GridEntityType.GRID_SPIDERWEB]=true,
  [GridEntityType.GRID_PIT]=true,
  [GridEntityType.GRID_TRAPDOOR]=true,
  [GridEntityType.GRID_STAIRS]=true,
  [GridEntityType.GRID_PRESSURE_PLATE]=true,
  [GridEntityType.GRID_SPIKES_ONOFF]=true,
  [GridEntityType.GRID_STATUE]=true,
  [GridEntityType.GRID_TELEPORTER]=true,
  -- The KEY BLOCK. Reported as "4000" because that is its id in the ROOM XML, which is a
  -- different numbering from GridEntityType -- at runtime it is GRID_LOCK (11).
  -- It is also in GRID_FLAT: the art is top-down, like the rocks.
  [GridEntityType.GRID_LOCK]=true,
}

-- One line per grid TYPE we refuse to draw, so the next missing obstacle is a log line
-- away instead of a playtest mystery. (An earlier version of this was removed; the key
-- block would have been spotted immediately if it had still been here.)
local gridSkipLogged = {}

-- Grid pieces that lie FLAT on the floor (vs standing up like rocks).
local GRID_FLAT = {
  -- Rocks and the square metal/black blocks: drawn top-down in the game's art,
  -- so they read correctly lying on the floor.
  [GridEntityType.GRID_ROCK]=true,
  [GridEntityType.GRID_ROCKB]=true,
  [GridEntityType.GRID_ROCKT]=true,
  [GridEntityType.GRID_ROCK_BOMB]=true,
  [GridEntityType.GRID_ROCK_SS]=true,
  [GridEntityType.GRID_ROCK_SPIKED]=true,
  [GridEntityType.GRID_ROCK_GOLD]=true,
  -- The key block: a top-down square block in the art, like the rocks above.
  [GridEntityType.GRID_LOCK]=true,
  -- Genuinely floor-level pieces
  [GridEntityType.GRID_SPIDERWEB]=true,
  [GridEntityType.GRID_PIT]=true,
  [GridEntityType.GRID_TRAPDOOR]=true,
  [GridEntityType.GRID_STAIRS]=true,
  [GridEntityType.GRID_PRESSURE_PLATE]=true,
  [GridEntityType.GRID_GRAVITY]=true,
  [GridEntityType.GRID_TELEPORTER]=true,
  [GridEntityType.GRID_SPIKES]=true,
  [GridEntityType.GRID_SPIKES_ONOFF]=true,
  -- NOT flat (stay upright): GRID_POOP, GRID_TNT, GRID_PILLAR, GRID_STATUE,
  -- GRID_ROCK_ALT / GRID_ROCK_ALT2 (pots, urns, skulls, mushrooms).
}

-- Whole ENEMIES/entities whose sprites are drawn top-down and should lie FLAT on the
-- floor (crawlers, floor-hugging bosses). Gabriel's tested list. Mixed granularity:
--   [type] = true                     -> every variant/subtype
--   [type] = { [variant] = true }     -> that variant, any subtype
--   [type] = { [variant] = { [sub] = true } } -> exact type.variant.subtype
local FLAT_ENTITIES = {
  [877] = true,                        -- 877 (all)
  [407] = { [0] = true },              -- 407.0.0 Hush: a huge ground-hugging blob, drawn
                                       -- top-down. He is a BOSS, so if the HP bar or the
                                       -- y-sort misbehave once flat, that is where to look
                                       -- (boss bar reads the entity, not its flatness).
  [44]  = { [0] = true, [1] = true },  -- 44.0, 44.1
  [60]  = { [0] = true, [1] = true },  -- 60.0, 60.1
  [814] = { [0] = true },              -- 814.0
  [201] = { [0] = true },              -- 201.0
  [2]   = { [8] = { [0] = true } },    -- 2.8.0 Scythe Tear (ground-sweeping arc)
  [291] = { [0] = true, [1] = true, [2] = true },  -- 291.0/1/2 (teleport pitfalls)
  [218] = { [0] = { [0] = true, [2] = true } },  -- 218.0.0 / 218.0.2 Wall Hugger (crawls
                                       -- along the wall; drawn top-down, so it lies flat)
  [1000] = { [203] = true,             -- 1000.203 enemy-spawn marker (ground decal → lie flat)
             [74]  = { [0] = true, [1] = true, [2] = true },    -- 1000.74.0/1/2 ground decals
             [123] = { [4] = true },    -- 1000.123.4 "Static Halo" (Dogma's ground halo)
             [138] = { [0] = true },    -- 1000.138.0 "Mist" (ground fog)
             [83]  = { [0] = true },    -- 1000.83.0 "Spear of Destiny" (drawn top-down)
             [190] = { [0] = true } },  -- 1000.190.0 "Mother Tracer": the pale beam that
                                        -- telegraphs where Mother's bullets will come from.
                                        -- Runs horizontally or vertically ALONG THE FLOOR,
                                        -- so it is a decal, not a billboard.
  [5]   = { [380] = { [0] = true, [10] = true } },   -- 5.380.0 and 5.380.10
}
local function isFlatEntity(ty, va, su)
  local t = FLAT_ENTITIES[ty]
  if t == nil then return false end
  if t == true then return true end
  local v = t[va]
  if v == nil then return false end
  if v == true then return true end
  return v[su] == true
end

-- Entities NEVER exported to the app. Same [type][variant][subtype] shape as FLAT_ENTITIES
-- (true at any level = match everything below it). For sprites that read fine as a flat 2D
-- overlay in the real game but break the illusion as an upright billboard in the diorama.
-- PLAYER animations during which the game shows PLAIN Isaac with NO costumes at all.
-- "Happy"/"Sad" (the pill reaction) and the "Pickup*" pose were already handled; these are
-- the rest of the same class — transitions and deaths, where the game swaps to a bespoke
-- animation that the costume layers were never authored against, so relaying costumes leaves
-- hats and hair floating over a sprite that is falling, leaping or dissolving.
--
-- Matched EXACTLY, not by prefix: "Death" must not also catch "DeathTeleport"'s siblings by
-- accident, and an over-broad prefix here silently strips costumes during normal play.
local BARE_ANIMS = {
  ["Happy"]=true, ["Sad"]=true,            -- pill reaction (pre-existing)
  ["Appear"]=true, ["Death"]=true,
  ["TeleportUp"]=true, ["TeleportDown"]=true, ["DeathTeleport"]=true,
  ["Trapdoor"]=true, ["MinecartEnter"]=true,
  ["Jump"]=true, ["Glitch"]=true,
  ["LostDeath"]=true, ["ForgottenDeath"]=true,
  ["FallIn"]=true, ["JumpOut"]=true, ["LightTravel"]=true,
  ["LeapUp"]=true, ["SuperLeapUp"]=true,
  ["LeapDown"]=true, ["SuperLeapDown"]=true,
}

local HIDE_ENTITIES = {
  [1000] = { [121] = true },  -- 1000.121 "Light" (LightGradient.anm2): a soft additive glow
                              -- card. Fine painted over a 2D screen, but in VR it hangs in the
                              -- air as a visible gradient quad, so drop it entirely.
}
-- Entities whose SUBTYPE selects which anm2 LAYERS get drawn. Mother's phase 1 is four
-- separate entities (912.0.0-3) sharing ONE anm2 (`912.000_witness.anm2`, layers:
-- 0 head, 1 back, 2 left arm, 3 right arm) — the engine picks the part per subtype
-- INTERNALLY, so every one of them reports all four layers visible and the app faithfully
-- drew the whole body four times on top of itself.
--
-- A subtype NOT listed here is left alone (all layers drawn), so adding a type here can
-- only ever narrow what is shown. The MOTHERLAYERS log line prints each subtype's real
-- layer state — correct the table from that rather than guessing again.
local LAYER_BY_SUBTYPE = {
  -- MEASURED (MOTHERLAYERS, 2026-09-16): 912.0.0 reports all four layers visible
  --   [0:true:witness_head.png 1:true:witness_head.png 2:true:witness_arm.png 3:true:witness_arm.png]
  -- confirming the engine does NOT express the split through layer visibility. There are
  -- four subtypes and exactly four layers, named head / back / left / right — and the user
  -- describes the sub-entities as "her arms and body" — so subtype N draws layer N.
  [912] = { [0] = {
    [0] = { [0] = true },   -- head
    [1] = { [1] = true },   -- back
    [2] = { [2] = true },   -- left arm
    [3] = { [3] = true },   -- right arm
  } },
}
local function layersForSubtype(ty, va, su)
  local byType = LAYER_BY_SUBTYPE[ty];   if not byType then return nil end
  local byVar  = byType[va];             if not byVar  then return nil end
  return byVar[su]
end

local motherLogged = {}
local creepLogged  = {}
local tracerLogged = {}
local bdLogged     = {}   -- one BACKDROP line per backdrop type
local motherWallLogged = false

-- "id:visible:alpha:sheet" for every layer of a sprite. A layer vanishes in the app for
-- three reasons and they look identical on screen: not visible, ALPHA 0, or a runtime
-- spritesheet path the app can't resolve. This prints all three.
local function layerDump(sp)
  local okAll, layers = pcall(function() return sp:GetAllLayers() end)
  if not okAll or not layers then return "<none>" end
  local parts = {}
  for i = 1, #layers do
    local L = layers[i]
    local id = i - 1
    local ok, v = pcall(function() return L:GetLayerID() end); if ok and v then id = v end
    local vis = "?"; ok, v = pcall(function() return L:IsVisible() end); if ok and v ~= nil then vis = tostring(v) end
    -- NB: clampByte is declared further down the file, so it is not in scope here — a
    -- Lua closure would resolve the name to a nil GLOBAL and blow up when called. Inlined.
    local al = -1
    ok, v = pcall(function() return L:GetColor() end)
    if ok and v then
      al = math.floor((v.A or 1) * 255 + 0.5)
      if al < 0 then al = 0 elseif al > 255 then al = 255 end
    end
    local path = ""; ok, v = pcall(function() return L:GetSpritesheetPath() end); if ok and v then path = v end
    parts[#parts+1] = string.format("%d:%s:a%d:%s", id, vis, al, path)
  end
  return table.concat(parts, " ")
end
local function logMotherLayers(e, sp, ty, va, su)
  if ty ~= 912 then return end
  local key = va * 1000 + su
  if motherLogged[key] then return end
  motherLogged[key] = true
  local okAll, layers = pcall(function() return sp:GetAllLayers() end)
  local parts = {}
  if okAll and layers then
    for i = 1, #layers do
      local L = layers[i]
      local id = i - 1
      local ok, v = pcall(function() return L:GetLayerID() end); if ok and v then id = v end
      local vis = "?"; ok, v = pcall(function() return L:IsVisible() end); if ok and v ~= nil then vis = tostring(v) end
      local path = ""; ok, v = pcall(function() return L:GetSpritesheetPath() end); if ok and v then path = v end
      parts[#parts+1] = string.format("%d:%s:%s", id, vis, path:match("([^/]+)$") or path)
    end
  end
  local anim = ""; local okAn, av = pcall(function() return sp:GetAnimation() end); if okAn and av then anim = av end
  Isaac.DebugString(string.format("%s: MOTHERLAYERS 912.%d.%d anim='%s' layers=[%s]",
    MOD_NAME, va, su, anim, table.concat(parts, " ")))
end

-- Her arms don't animate in the app. Each arm is its OWN entity drawing its own layer, so
-- either those entities' sprites genuinely advance (and we have a frame-plumbing bug) or
-- they sit still while the BODY's animation drives the arm layers — in which case the arms
-- must borrow the body's anim and frame. This samples anim+frame for every Mother
-- sub-entity so the log shows directly whether their frames move.
local function logMotherMotion(e, sp, va, su)
  if (frame % 20) ~= 0 then return end      -- ~3 samples/sec per sub-entity
  local anim = ""; local okA, av = pcall(function() return sp:GetAnimation() end); if okA and av then anim = av end
  local fr = -1;   local okF, fv = pcall(function() return sp:GetFrame() end);     if okF and fv then fr = fv end
  Isaac.DebugString(string.format("%s: MOTHERMOTION 912.%d.%d anim='%s' frame=%d",
    MOD_NAME, va, su, anim, fr))
end

local function isHiddenEntity(ty, va, su)
  local t = HIDE_ENTITIES[ty]
  if t == nil then return false end
  if t == true then return true end
  local v = t[va]
  if v == nil then return false end
  if v == true then return true end
  return v[su] == true
end

-- Breakable rock-likes: State > 1 means already destroyed. The grid entity
-- lingers with an intact-looking animation until the room reloads, which is why
-- destroyed rocks kept showing in the diorama.
local GRID_BREAKABLE = {
  [GridEntityType.GRID_ROCK]=true,[GridEntityType.GRID_ROCKB]=true,
  [GridEntityType.GRID_ROCKT]=true,[GridEntityType.GRID_ROCK_BOMB]=true,
  [GridEntityType.GRID_ROCK_ALT]=true,[GridEntityType.GRID_ROCK_ALT2]=true,
  [GridEntityType.GRID_ROCK_SS]=true,[GridEntityType.GRID_ROCK_SPIKED]=true,
  [GridEntityType.GRID_ROCK_GOLD]=true,
}

local function clampByte(v)
  v = math.floor((v or 1) * 255 + 0.5)
  if v < 0 then return 0 elseif v > 255 then return 255 end
  return v
end

-- Colour OFFSET (RO/GO/BO) is signed (~-1..1): the damage flash sets it to +1
-- (white), hurt tints add red, etc. Map to a byte with 128 = zero so the app
-- can decode (b-128)/127. Default (no offset) -> 128.
local function offByte(v)
  v = math.floor((v or 0) * 127 + 0.5) + 128
  if v < 0 then return 0 elseif v > 255 then return 255 end
  return v
end

-- Colorize (REPENTOGON Color:GetColorize -> {R,G,B,A}). This is a luminance recolour
-- whose RGB can exceed 1 (e.g. homing lasers report (3,1,3.5)). We export the HUE
-- (rgb normalised so the max channel = 1, as a byte) plus the amount A (0..1 byte).
-- Returns (255,255,255,0) when there is no colorize (A=0).
local function readColorize(col)
  local cr, cg, cb, ca = 255, 255, 255, 0
  if col then
    local ok, t = pcall(function() return col:GetColorize() end)
    if ok and t then
      local r = t.R or t[1] or 0
      local g = t.G or t[2] or 0
      local b = t.B or t[3] or 0
      local a = t.A or t[4] or 0
      if type(a) == "number" and a > 0.001 then
        local m = math.max(r or 0, g or 0, b or 0, 0.001)
        cr = clampByte((r or 0) / m)
        cg = clampByte((g or 0) / m)
        cb = clampByte((b or 0) / m)
        ca = clampByte(math.min(a, 1.0))
      end
    end
  end
  return cr, cg, cb, ca
end

local function clampU8(v)
  v = math.floor(v or 0)
  if v < 0 then return 0 elseif v > 255 then return 255 end
  return v
end

-- Clamp a sprite frame to a valid uint16. Some sprites (idle grid pieces, certain
-- rooms) report a negative frame, which overflows string.pack's 'H' field and
-- crashes the whole MC_POST_RENDER callback (freezing the app).
local function clampFrame(v)
  if type(v) ~= "number" then return 0 end
  v = math.floor(v)
  if v < 0 then return 0 elseif v > 65535 then return 65535 end
  return v
end

-- Read an entity's colour offset (RO,GO,BO) as three mapped bytes; 128,128,128
-- (zero) if unavailable.
local function readOffset(col)
  local ro, go, bo = 128, 128, 128
  if col then
    local ok, v
    ok, v = pcall(function() return col.RO end); if ok and v then ro = offByte(v) end
    ok, v = pcall(function() return col.GO end); if ok and v then go = offByte(v) end
    ok, v = pcall(function() return col.BO end); if ok and v then bo = offByte(v) end
  end
  return ro, go, bo
end

local function entHeight(e)
  local h = 0
  local so = e.SpriteOffset
  if so then h = h - so.Y end
  local tp = e:ToTear() or e:ToProjectile()
  if tp then
    -- Tears / projectiles carry their arc in .Height already. Adding PositionOffset
    -- on top would double-lift them, so only Height here.
    local ok, hv = pcall(function() return tp.Height end)
    if ok and hv then h = h - hv end
  else
    local eff = e:ToEffect()
    if eff then
      -- Effects carry their arc in EITHER .Height OR PositionOffset.Y, depending on
      -- the effect. Laser impact bits (1000.70/50/99) launch and fall via
      -- PositionOffset.Y (their .Height is nil); most other effects use .Height with
      -- PositionOffset 0. Both are safe to fold in — whichever is unused reads 0.
      local ok, hv = pcall(function() return eff.Height end)
      if ok and hv then h = h - hv end
      local okPo, po = pcall(function() return e.PositionOffset end)
      if okPo and po then h = h - po.Y end
    else
      -- Jumping / flying enemies raise the sprite via PositionOffset (visual-only;
      -- shadow stays on the floor).
      local okPo, po = pcall(function() return e.PositionOffset end)
      if okPo and po then h = h - po.Y end
    end
  end
  return h
end

-- LayerState carries per-layer OVERRIDES only: the runtime spritesheet (costumes,
-- pedestal art, recolours), visibility, colour and flip. The frame crop still
-- comes from the .anm2, so we send these keyed by layer id and merge app-side.
--   layer: "<BHBBBBB" = layerId, sheetId, lflags(bit0 vis, bit1 flipX, bit2 flipY), r,g,b,a
-- showIds (optional): a whitelist { [layerID]=true } of the layers the game reports as
-- actually visible for this costume (from GetCostumeLayerMap). When provided, every
-- layer NOT in the set is forced invisible — this is how we hide costume layers the game
-- has deactivated (Technology under Brimstone, everything under Blood Rage, ...).
-- hideIds (optional): a blacklist { [layerID]=true } of layers to force invisible — used
-- for the player's BASE body sprite to drop its default head/body where a body costume
-- (Brimstone etc.) replaces it, so Isaac's old head doesn't peek out behind.
-- forceShow (optional): ignore the runtime IsVisible() and treat every layer as visible.
-- Used for the base body during the item-pickup pose: a head costume (Technology/Brimstone)
-- hides Isaac's base head layer at runtime, so relaying that would leave him headless while
-- holding (costumes are skipped then). Forcing visible restores it; the app still ANDs with
-- the anm2's own per-frame visibility, so the pickup pose governs what actually shows.
local function packLayerOverrides(sp, showIds, hideIds, forceShow)
  local okAll, layers = pcall(function() return sp:GetAllLayers() end)
  if not okAll or not layers then return "", 0 end
  local parts, n = {}, 0
  for i = 1, #layers do
    if n >= MAX_LAYERS then break end
    local L = layers[i]
    local id = i - 1
    local ok, v
    ok, v = pcall(function() return L:GetLayerID() end); if ok and v then id = v end

    local vis = true
    if not forceShow then
      ok, v = pcall(function() return L:IsVisible() end); if ok and v ~= nil then vis = v end
    end
    if showIds and not showIds[id] then vis = false end
    if hideIds and hideIds[id] then vis = false end

    local path = ""
    ok, v = pcall(function() return L:GetSpritesheetPath() end); if ok and v then path = v end

    local lf = vis and 1 or 0
    ok, v = pcall(function() return L:GetFlipX() end); if ok and v then lf = lf + 2 end
    ok, v = pcall(function() return L:GetFlipY() end); if ok and v then lf = lf + 4 end

    local r,g,b,a = 255,255,255,255
    ok, v = pcall(function() return L:GetColor() end)
    if ok and v then r=clampByte(v.R); g=clampByte(v.G); b=clampByte(v.B); a=clampByte(v.A) end
    -- Pickup pose (forceShow): a head costume (Technology/Brimstone) hides Isaac's base head
    -- via COLOUR ALPHA=0, NOT IsVisible — so forcing the vis flag alone left him headless. Force
    -- full opacity here to bring the head back; RGB is kept, so a black (Brimstone) body stays black.
    if forceShow then a = 255 end

    if id >= 0 and id <= 255 then
      n = n + 1
      parts[n] = string.pack("<BHBBBBB", id, intern(path), lf, r, g, b, a)
    end
  end
  return table.concat(parts), n
end

----------------------------------------------------------------------------
-- Backdrop image streaming (MSG_IMG) ---------------------------------------
-- REPENTOGON hands us the game's OWN composited room image (blood/water/tint
-- baked in) via Backdrop:GetFloorImage()/GetWallImage(). Format confirmed as
-- RGBA8 (4 bytes/pixel). We read it row-by-row (each row is a single-row
-- GetTexelRegion, so there is no inter-row stride ambiguity from the 1024 pad),
-- then chunk the raw RGBA over UDP. The app reassembles it into a texture.
local imgVersion = 0          -- per-room id so the app can group/replace chunks
local imgCur = {}             -- [kind] = { ver, kind, w, h, buf } last image sent
local imgResendAt = nil       -- absolute frames at which to resend (heals UDP drops)
-- kind 0 = floor render, kind 1 = wall render (both blood/tint baked in).
local IMG_KINDS = { { kind = 0, getter = "GetFloorImage" },
                    { kind = 1, getter = "GetWallImage"  } }

-- Returns w, h, rawRGBA (h*w*4 bytes) or nil on any failure.
local function captureImage(bd, getter)
  local okImg, img = pcall(function() return bd[getter](bd) end)
  if not okImg or not img then return nil end
  local okW, w = pcall(function() return img:GetWidth()  end)
  local okH, h = pcall(function() return img:GetHeight() end)
  if not okW or not okH or not w or not h or w <= 0 or h <= 0 then return nil end
  if w > 4096 or h > 4096 then return nil end          -- sanity clamp
  local expect = w * 4
  local rows = {}
  for y = 0, h - 1 do
    local okR, row = pcall(function() return img:GetTexelRegion(0, y, w, 1) end)
    if not okR or not row then return nil end
    if #row > expect then row = row:sub(1, expect) end  -- trim any row padding
    if #row ~= expect then return nil end
    rows[y + 1] = row
  end
  return w, h, table.concat(rows)
end

-- Diagnostic: map where the real content sits inside an image. For FLOOR we
-- treat alpha>0 as content; for the base image (WALL) we treat non-near-black
-- rgb as content (outside-room reads as opaque black). Reports the content
-- bounding box + a 5-sample scan of the centre row so we can see the layout.
local function scanImage(img, w, h, isFloor)
  local minx, miny, maxx, maxy = w, h, -1, -1
  local COLS, ROWS = 32, 20
  for gy = 0, ROWS - 1 do
    local y = math.floor(gy * (h - 1) / (ROWS - 1))
    for gx = 0, COLS - 1 do
      local x = math.floor(gx * (w - 1) / (COLS - 1))
      local ok, px = pcall(function() return img:GetTexelRegion(x, y, 1, 1) end)
      if ok and px and #px >= 4 then
        local r, g, b, a = string.byte(px, 1, 4)
        local content = isFloor and (a > 0) or ((r + g + b) > 24)
        if content then
          if x < minx then minx = x end
          if x > maxx then maxx = x end
          if y < miny then miny = y end
          if y > maxy then maxy = y end
        end
      end
    end
  end
  local cy = math.floor(h / 2)
  local scan = {}
  for i = 0, 4 do
    local x = math.floor(i * (w - 1) / 4)
    local ok, px = pcall(function() return img:GetTexelRegion(x, cy, 1, 1) end)
    if ok and px and #px >= 4 then
      local r, g, b, a = string.byte(px, 1, 4)
      scan[#scan + 1] = string.format("x%d=[%d,%d,%d,%d]", x, r, g, b, a)
    end
  end
  if maxx < 0 then return "NO-CONTENT" end
  return string.format("bbox=[%d,%d..%d,%d] frac=[%.2f,%.2f..%.2f,%.2f] mid{%s}",
    minx, miny, maxx, maxy, minx / w, miny / h, maxx / w, maxy / h,
    table.concat(scan, " "))
end

local function sendImage(kind, ver, w, h, buf)
  local total = #buf
  local nch = math.ceil(total / IMG_CHUNK)
  if nch < 1 then nch = 1 end
  if nch > 65535 then return end
  local off = 1
  for ci = 0, nch - 1 do
    local part = buf:sub(off, off + IMG_CHUNK - 1)
    off = off + #part
    local hdr = string.pack("<BBBBHHHHHH", 0x49, PROTO_VERSION, MSG_IMG, kind,
                            ver, w, h, nch, ci, #part)
    pcall(function() udp:send(hdr .. part) end)
  end
end

-- Capture one image kind (floor/wall) and (re)send it. force=true always sends
-- (room change); force=false only sends when the pixels actually changed (so
-- mid-room blood shows up, but we don't spam otherwise).
local function maybeUpdateImage(room, curFrame, force, kind, getter)
  if not HAS_RGON then return end
  local okBd, bd = pcall(function() return room:GetBackdrop() end)
  if not okBd or not bd then return end
  local w, h, buf = captureImage(bd, getter)
  if not buf then return end
  local cur = imgCur[kind]
  if (not force) and cur and cur.buf == buf then return end
  imgVersion = imgVersion + 1
  if imgVersion > 65535 then imgVersion = 1 end
  imgCur[kind] = { ver = imgVersion, kind = kind, w = w, h = h, buf = buf }
  sendImage(kind, imgVersion, w, h, buf)
  imgResendAt = { curFrame + 20, curFrame + 60 }   -- two heals over ~1s
end

-- Wall image only changes on room entry, so it's captured once (force) there.
-- Logged, because "the walls didn't change" has two very different causes: the
-- recapture never fired, or it fired and the game handed back the same (or an
-- empty) wall image. The line below distinguishes them.
local function updateWallImage(room, curFrame)
  maybeUpdateImage(room, curFrame, true, 1, "GetWallImage")
  local cur = imgCur[1]
  if cur then
    Isaac.DebugString(string.format("%s: WALLCAP ver=%d %dx%d bytes=%d",
      MOD_NAME, cur.ver, cur.w, cur.h, #cur.buf))
  else
    Isaac.DebugString(MOD_NAME..": WALLCAP produced NOTHING (capture failed)")
  end
end

----------------------------------------------------------------------------
-- Amortized FLOOR capture + send --------------------------------------------
-- Reading the whole floor image in one frame (~468 row reads) + re-chunking it
-- over UDP was the ~1s FPS hitch. Instead we run a small state machine ONE STEP
-- PER FRAME: read a few rows per frame until the whole floor is captured, compare
-- the FULL buffer (so every blood/gut/liquid splat is caught — a sparse probe
-- missed them), then send a few UDP chunks per frame. Cost is spread flat with no
-- spike, and the floor still refreshes ~1-2x/sec during combat.
local FLOOR_ROWS_PER_TICK   = 48     -- rows read per frame while capturing (468/48 ≈ 10 frames)
local FLOOR_CHUNKS_PER_TICK = 96     -- UDP chunks sent per frame while sending (~2 frames total)
local FLOOR_HEAL_CYCLES     = 4      -- re-send an UNCHANGED floor every N capture cycles (heals drops)
local floorPipe = nil                -- capture/send state machine (see floorTick)

local function floorTick(room)
  if not HAS_RGON then return end
  local okBd, bd = pcall(function() return room:GetBackdrop() end)
  if not okBd or not bd then return end

  -- SEND phase: push a batch of chunks, then drop back to capturing.
  if floorPipe and floorPipe.phase == "send" then
    local fp = floorPipe
    local sent = 0
    while fp.ci < fp.nch and sent < FLOOR_CHUNKS_PER_TICK do
      local part = fp.buf:sub(fp.off, fp.off + IMG_CHUNK - 1)
      fp.off = fp.off + #part
      local hdr = string.pack("<BBBBHHHHHH", 0x49, PROTO_VERSION, MSG_IMG, 0,
                              fp.ver, fp.w, fp.h, fp.nch, fp.ci, #part)
      pcall(function() udp:send(hdr .. part) end)
      fp.ci = fp.ci + 1
      sent = sent + 1
    end
    if fp.ci >= fp.nch then floorPipe = { phase = "capture", y = 0, rows = {}, unchanged = 0 } end
    return
  end

  -- CAPTURE phase.
  if not floorPipe then floorPipe = { phase = "capture", y = 0, rows = {}, unchanged = 0 } end
  local fp = floorPipe

  local okImg, img = pcall(function() return bd:GetFloorImage() end)
  if not okImg or not img then floorPipe = nil; return end
  local okW, w = pcall(function() return img:GetWidth()  end)
  local okH, h = pcall(function() return img:GetHeight() end)
  if not okW or not okH or not w or not h or w <= 0 or h <= 0 or w > 4096 or h > 4096 then
    floorPipe = nil; return
  end
  -- Dimensions changed mid-capture (room change): restart the scan.
  if fp.w and (fp.w ~= w or fp.h ~= h) then fp.y = 0; fp.rows = {} end
  fp.w = w; fp.h = h
  local expect = w * 4

  local read = 0
  while fp.y < h and read < FLOOR_ROWS_PER_TICK do
    local okR, row = pcall(function() return img:GetTexelRegion(0, fp.y, w, 1) end)
    if not okR or not row then floorPipe = nil; return end       -- retry from scratch next frame
    if #row > expect then row = row:sub(1, expect) end
    if #row ~= expect then floorPipe = nil; return end
    fp.rows[fp.y + 1] = row
    fp.y = fp.y + 1
    read = read + 1
  end

  if fp.y >= h then
    local buf = table.concat(fp.rows)
    local cur = imgCur[0]
    local changed = not (cur and cur.buf == buf)
    local unchanged = changed and 0 or (fp.unchanged + 1)
    if changed or unchanged >= FLOOR_HEAL_CYCLES then
      imgVersion = imgVersion + 1
      if imgVersion > 65535 then imgVersion = 1 end
      imgCur[0] = { ver = imgVersion, kind = 0, w = w, h = h, buf = buf }
      local nch = math.ceil(#buf / IMG_CHUNK); if nch < 1 then nch = 1 end
      if nch <= 65535 then
        floorPipe = { phase = "send", w = w, h = h, buf = buf, ver = imgVersion,
                      off = 1, ci = 0, nch = nch, unchanged = 0 }
        return
      end
    end
    -- Unchanged and not heal-time: restart the scan, keep the heal counter.
    floorPipe = { phase = "capture", y = 0, rows = {}, unchanged = unchanged }
  end
end

-- Per-room leftover-pickup memory: gridIndex -> content bitmask. We only see the
-- CURRENT room's entities, so we record what's in each room as we pass through it
-- (exactly like the game's minimap remembering what you left behind).
local roomContents = {}
-- Classify a pickup Variant into a content bit for the minimap.
--   1 heart, 2 coin, 4 key, 8 bomb, 16 chest, 32 collectible, 64 other(card/pill/trinket)
local function classifyPickup(v)
  if     v == 10  then return 1
  elseif v == 20  then return 2
  elseif v == 30  then return 4
  elseif v == 40  then return 8
  elseif v == 100 then return 32
  elseif (v >= 50 and v <= 60) or v == 360 or v == 390 then return 16
  else return 64 end
end

-- Grid cells a room occupies (dx,dy from its top-left anchor), keyed by RoomShape.
-- Used to gather leftover-pickup contents across every cell of large/L rooms (the
-- current-room index we store under may be any sub-cell, not the anchor).
local SHAPE_CELLS = {
  [4]={{0,0},{0,1}}, [5]={{0,0},{0,1}},
  [6]={{0,0},{1,0}}, [7]={{0,0},{1,0}},
  [8]={{0,0},{1,0},{0,1},{1,1}},
  [9]={{1,0},{0,1},{1,1}},
  [10]={{0,0},{0,1},{1,1}},
  [11]={{0,0},{1,0},{1,1}},
  [12]={{0,0},{1,0},{0,1}},
}
local function shapeCells(shape) return SHAPE_CELLS[shape] or {{0,0}} end

-- Turn a raw config name into a readable display name. Newer content stores a
-- localization KEY in .Name (e.g. "#SOUL_OF_EDEN_NAME"); prefer REPENTOGON's
-- localized getter, else prettify the key: drop '#'/'_NAME', title-case words.
local SMALL_WORDS = { ["of"]=true, ["the"]=true, ["a"]=true, ["an"]=true, ["and"]=true,
  ["or"]=true, ["to"]=true, ["in"]=true, ["on"]=true, ["for"]=true, ["with"]=true }
local function prettifyKey(s)
  if not s or s == "" then return "" end
  if s:sub(1,1) ~= "#" then return s end            -- already a display name
  s = s:sub(2):gsub("_NAME$", ""):gsub("_", " ")
  local out, i = {}, 0
  for w in s:gmatch("%S+") do
    i = i + 1
    local lw = w:lower()
    if i > 1 and SMALL_WORDS[lw] then out[i] = lw
    else out[i] = lw:sub(1,1):upper() .. lw:sub(2) end
  end
  return table.concat(out, " ")
end
-- Card HUD icons live in gfx/ui/ui_cardfronts.anm2, one animation per card. The
-- animation name IS the card's `hud` name, which Isaac.GetCardIdByName() resolves
-- to a runtime id. We can't read cards.xml (io is sandboxed), so we list the known
-- vanilla anim names and let the game map each to its id. Cards not here (runes,
-- soul cards, modded) get no match -> the app draws a marker instead of a wrong card.
local CARD_HUD_ANIMS = {
  "00_TheFool","01_TheMagician","02_TheHighPriestess","03_TheEmpress","04_TheEmperor",
  "05_TheHierophant","06_TheLovers","07_TheChariot","08_TheJustice","09_TheHermit",
  "10_WheelOfFortune","11_Strength","12_TheHangedMan","13_Death","14_Temperance",
  "15_TheDevil","16_TheTower","17_TheStars","18_TheMoon","19_TheSun","20_Judgement",
  "21_TheWorld","22_TheJoker","23_TwoOfHearts","24_TwoOfSpades","25_TwoOfClubs",
  "26_TwoOfDiamonds","27_SuicideKing","28_ChaosCard","29_CreditCard","30_RulesCard",
  "31_CardAgainstHumanity","32_GetOutOfJail","33_MysteryCard","34_DiceShard",
  "35_EmergencyContact","36_AceOfSpades","37_AceOfHearts","38_AceOfClubs",
  "39_AceOfDiamonds","40_HolyCard","52_HugeGrowth","53_AncientRecall","54_EraWalk",
}
local cardHud = nil
local function ensureCardHud()
  if cardHud ~= nil then return end
  cardHud = {}
  for _, anim in ipairs(CARD_HUD_ANIMS) do
    local ok, id = pcall(function() return Isaac.GetCardIdByName(anim) end)
    if ok and id and id > 0 then cardHud[id] = anim end
  end
end

local function locName(cfg)
  if not cfg then return "" end
  local ok, n = pcall(function() return cfg:GetName() end)   -- REPENTOGON localized
  if ok and type(n) == "string" and n ~= "" and n:sub(1,1) ~= "#" then return n end
  local ok2, nm = pcall(function() return cfg.Name end)
  if ok2 and type(nm) == "string" then return prettifyKey(nm) end
  return ""
end

-- The item/trinket FLAVOUR text — the game's own quip (e.g. "Yay, cancer!",
-- "HP up", "You feel protected"), NOT EID's mechanical breakdown. The game stores it
-- as a localization key in cfg.Description (e.g. "#CANCER_TRINKET_DESCRIPTION"); we
-- resolve it with Isaac.GetString(category, key). Collectibles resolve under "Items"
-- and trinkets under "Trinkets" (tried first); resolveKey short-circuits on the first
-- hit, so a pickup only ever does 1-2 lookups. The rest are kept as a safety fallback.
-- Returns "" if none resolve.
local FLAVOR_CATS = { "Items", "Trinkets", "ItemDescriptions", "ItemDescription",
                      "Descriptions", "EntityName", "UI" }
local function cleanStr(s)
  s = s:gsub("%s+", " "):gsub("^%s+", ""):gsub("%s+$", "")
  return s
end
local function resolveKey(keyRaw)
  if type(keyRaw) ~= "string" or keyRaw == "" then return "" end
  local key = keyRaw:gsub("^#", "")
  for _, cat in ipairs(FLAVOR_CATS) do
    for _, k in ipairs({ key, keyRaw }) do
      local ok, s = pcall(function() return Isaac.GetString(cat, k) end)
      if ok and type(s) == "string" and s ~= "" and s ~= k and s:sub(1,1) ~= "#" then
        return cleanStr(s)
      end
    end
  end
  return ""
end
local function flavorText(cfg)
  if not cfg then return "" end
  local okD, d = pcall(function() return cfg.Description end)
  if okD and type(d) == "string" then return resolveKey(d) end
  return ""
end

local function setPopup(text)
  if text == nil or text == "" then return end
  if #text > 220 then text = text:sub(1, 220) end
  pillPopup = text
  pillPopupUntil = frame + 180      -- ~3s at 60fps (matches the in-game pickup text)
end

-- The curses actually IN EFFECT. Some items (Black Candle) nullify curses while held,
-- so the app must not darken the room / hide the minimap for a curse the player is
-- immune to. GetCurses() normally already reflects that, but we also zero the mask when
-- any player holds Black Candle so a stale bit can never leak through to the app.
-- LevelCurse bits: 1 Darkness, 2 Labyrinth, 4 Lost, 8 Unknown, 16 Cursed, 32 Maze,
-- 64 Blind, 128 Giant.
local BLACK_CANDLE = 260
local function effectiveCurses(lvl)
  if not lvl then return 0 end
  local okC, c = pcall(function() return lvl:GetCurses() end)
  if not okC or type(c) ~= "number" or c <= 0 then return 0 end
  local okN, n = pcall(function() return Game():GetNumPlayers() end)
  if okN and type(n) == "number" then
    for i = 0, n - 1 do
      local okP, p = pcall(function() return Isaac.GetPlayer(i) end)
      if okP and p then
        local okH, h = pcall(function() return p:HasCollectible(BLACK_CANDLE) end)
        if okH and h then return 0 end
      end
    end
  end
  return c
end

-- Some lasers are deliberately INVISIBLE in game and must not be exported, or the app
-- draws a thick red brimstone beam where the player sees nothing.
--   * Generic rule: the engine marks such a beam `Visible = false` (this is how invisible
--     damage-beams are done), so honour that for ANY laser — fixes the whole class at once.
--   * Belt-and-braces: Finger! (collectible 467) damages through a beam owned by the Finger
--     familiar, EntityType 3 variant 110 (`003.110_Finger.anm2`, collisionDamage 0), so drop
--     anything that familiar spawned even if the Visible flag ever stops being set.
local FINGER_FAMILIAR_VARIANT = 110
-- Dogma's attack TELEGRAPH (the white line drawn between the Tech Dots before the
-- beam fires) is a laser the engine marks Visible=false, because it paints that
-- preview itself through coloroffset_dogma.fs. The generic Visible==false rule was
-- therefore eating it, which is why the dots showed but the line never did -- and why
-- it has no entity ID in the app's overlay (lasers go out on MSG_LASERS, not as
-- entities). Exempt anything Dogma spawned from the Visible rule.
-- Walk the ownership chain looking for Dogma. ONE hop is not enough: a laser IMPACT
-- (1000.50.1) is spawned by the LASER, not by Dogma, so a single SpawnerEntity check saw
-- type 7 and left the impact red. Three hops covers boss -> laser -> impact with room to
-- spare, and bails out the moment it finds the player or a familiar.
local function spawnerIsDogma(e)
  local cur = e
  for _ = 1, 3 do
    local okS, sp = pcall(function() return cur.SpawnerEntity end)
    if not (okS and sp) then okS, sp = pcall(function() return cur.Parent end) end
    if not (okS and sp) then return false end
    local okT, st = pcall(function() return sp.Type end)
    if not okT then return false end
    if st == 950 then return true end
    if st == 1 or st == 3 then return false end   -- Isaac's / a familiar's
    cur = sp
  end
  return false
end

-- ...and Dogma's own beams report NO owner at all, so anything they spawn has a chain that
-- dead-ends. In HIS arena an ownerless EFFECT/PROJECTILE/LASER is his. Restricted to those
-- three categories on purpose: a pickup or a grid piece has no spawner either, and greying
-- out a red heart in the Dogma room would be a very silly bug.
local function ownerlessInDogmaRoom(e)
  local okT, t = pcall(function() return e.Type end)
  if not okT then return false end
  if not (t == 1000 or t == 9 or t == 7) then return false end   -- effect / projectile / laser
  local okS, sp = pcall(function() return e.SpawnerEntity end)
  if not (okS and sp) then okS, sp = pcall(function() return e.Parent end) end
  return not (okS and sp)
end

-- Is this beam Dogma's? The spawner test alone was not enough: some of Dogma's beams
-- (the telegraph line between the Tech Dots, and the thick brimstone) report NO
-- SpawnerEntity and NO Parent, so they were treated as ordinary red brimstone. In
-- Dogma's arena every beam is Dogma's unless Isaac or a familiar fired it, so fall
-- back to "not the player's" whenever Dogma is in the room.
local function laserIsDogma(e, dogmaHere)
  if spawnerIsDogma(e) then return true end
  if not dogmaHere then return false end
  local okS, sp = pcall(function() return e.SpawnerEntity end)
  if not (okS and sp) then okS, sp = pcall(function() return e.Parent end) end
  if okS and sp then
    local okT, st = pcall(function() return sp.Type end)
    if okT and (st == 1 or st == 3) then return false end   -- player / familiar beam
    return true
  end
  return true          -- no owner at all: in this room that means the boss
end

local function laserHidden(e, dogmaHere)
  if laserIsDogma(e, dogmaHere) then return false end
  local okV, vis = pcall(function() return e.Visible end)
  if okV and vis == false then return true end
  local okS, sp = pcall(function() return e.SpawnerEntity end)
  if okS and sp then
    local okT, st = pcall(function() return sp.Type end)
    local okVa, sv = pcall(function() return sp.Variant end)
    if okT and okVa and st == EntityType.ENTITY_FAMILIAR and sv == FINGER_FAMILIAR_VARIANT then
      return true
    end
  end
  return false
end

-- Detect a NEW pickup (collectible OR trinket) the moment it's queued above the
-- head, and pop its name + flavour text. Both collectibles and trinkets go through
-- the same queue, so watching QueuedItem catches BOTH at pickup time (the trinket
-- inventory slot only updates later, at queue flush — that was the trinket lag).
-- Primed on the first call so pre-owned items don't pop at start.
local function checkPickups(plr)
  local qid, qIsTrinket = 0, false
  local okQ, q = pcall(function() return plr.QueuedItem end)
  if okQ and q then
    local okIt, it = pcall(function() return q.Item end)
    if okIt and it then
      local okId, id = pcall(function() return it.ID end)
      if okId and id and id > 0 then
        qid = id
        local okT, t = pcall(function() return it:IsTrinket() end); if okT and t then qIsTrinket = true end
      end
    end
  end

  if not pickupsPrimed then
    pickupsPrimed = true
    lastQueuedItem = qid
    return
  end

  if qid > 0 and qid ~= lastQueuedItem then
    local cfg, tag
    if qIsTrinket then
      tag = "T"
      local ok, c = pcall(function() return Isaac.GetItemConfig():GetTrinket(qid) end); if ok then cfg = c end
    else
      tag = "C"
      local ok, c = pcall(function() return Isaac.GetItemConfig():GetCollectible(qid) end); if ok then cfg = c end
    end
    if cfg then
      local nm = locName(cfg)
      if nm ~= "" then
        local ds = flavorText(cfg)
        setPopup(nm .. (ds ~= "" and ("\n" .. ds) or ""))
      end
    end
  end
  lastQueuedItem = qid
end

-- HUD: player health / consumables / active item / minimap, read from the game
-- and rendered app-side as a floating panel. Inline strings (icon path) so there's
-- no string-table timing dependency.
local function sendHud()
  if not udp or not havePack then return end
  local okP, plr = pcall(function() return Isaac.GetPlayer(0) end)
  if not okP or not plr then return end
  local function pv(fn, d) local ok, v = pcall(fn); if ok and v ~= nil then return v end; return d end

  local coins = pv(function() return plr:GetNumCoins() end, 0)
  local bombs = pv(function() return plr:GetNumBombs() end, 0)
  local keys  = pv(function() return plr:GetNumKeys()  end, 0)
  local flags1 = (pv(function() return plr:HasGoldenBomb() end, false) and 1 or 0)

  local maxH    = pv(function() return plr:GetMaxHearts()     end, 0)
  local redH    = pv(function() return plr:GetHearts()        end, 0)
  local soul    = pv(function() return plr:GetSoulHearts()    end, 0)
  local bone    = pv(function() return plr:GetBoneHearts()    end, 0)
  local rotten  = pv(function() return plr:GetRottenHearts()  end, 0)
  local eternal = pv(function() return plr:GetEternalHearts() end, 0)
  local golden  = pv(function() return plr:GetGoldenHearts()  end, 0)
  local blackMask = pv(function() return plr:GetBlackHearts() end, 0)

  local activeId  = pv(function() return plr:GetActiveItem(0) end, 0)
  local charge    = pv(function() return plr:GetActiveCharge(0) end, 0)
                  + pv(function() return plr:GetBatteryCharge(0) end, 0)
  local maxCharge, iconPath = 0, ""
  if activeId and activeId > 0 then
    local okCfg, cfg = pcall(function() return Isaac.GetItemConfig():GetCollectible(activeId) end)
    if okCfg and cfg then
      maxCharge = pv(function() return cfg.MaxCharges end, 0)
      iconPath  = pv(function() return cfg.GfxFileName end, "") or ""
    end
  end

  -- Trinkets (2 slots) + pocket item (card or pill).
  local function trinketIcon(tid)
    if not tid or tid <= 0 then return "" end
    local id = tid % 0x8000   -- strip the golden-trinket bit
    local ok, cfg = pcall(function() return Isaac.GetItemConfig():GetTrinket(id) end)
    if ok and cfg then return (pv(function() return cfg.GfxFileName end, "")) or "" end
    return ""
  end
  local t0 = pv(function() return plr:GetTrinket(0) end, 0)
  local t1 = pv(function() return plr:GetTrinket(1) end, 0)
  local t0icon, t1icon = trinketIcon(t0), trinketIcon(t1)
  local card0 = pv(function() return plr:GetCard(0) end, 0)
  local pill0 = pv(function() return plr:GetPill(0) end, 0)

  -- Pocket items: cards, runes, pills — their display names. Runes are just card
  -- ids, so the card path covers them. Pills show the effect name only once the
  -- run has identified them (faithful to the game); unidentified shows "Pill (?)".
  local function cardName(id)
    local okC, cfg = pcall(function() return Isaac.GetItemConfig():GetCard(id) end)
    if okC and cfg then return locName(cfg) end
    return ""
  end
  local function pillName(pc)
    local okP, pool = pcall(function() return Game():GetItemPool() end)
    if not okP or not pool then return "Pill" end
    local identified = false
    local okI, idf = pcall(function() return pool:IsPillIdentified(pc) end)
    if okI and idf then identified = idf end
    if not identified then return "Pill (?)" end
    local okE, effect = pcall(function() return pool:GetPillEffect(pc) end)
    if okE and effect and effect >= 0 then
      local okC, cfg = pcall(function() return Isaac.GetItemConfig():GetPillEffect(effect) end)
      if okC and cfg then local nm = locName(cfg); return nm ~= "" and nm or "Pill" end
    end
    return "Pill"
  end
  local pockets, npocket = {}, 0
  for slot = 0, 3 do
    local c = pv(function() return plr:GetCard(slot) end, 0)
    local p = pv(function() return plr:GetPill(slot) end, 0)
    local kind, id, nm = 0, 0, ""
    if c and c > 0 then kind, id, nm = 1, c, cardName(c)
    elseif p and p > 0 then kind, id, nm = 2, p, pillName(p) end
    if kind > 0 then
      if #nm > 60 then nm = nm:sub(1, 60) end
      npocket = npocket + 1
      pockets[npocket] = string.pack("<BHH", kind, id % 65536, #nm) .. nm
    end
  end

  -- Boss HP bar: aggregate current/max HP over every live boss in the room (handles
  -- multi-segment bosses); fill = totalHP / totalMaxHP.
  local bossFill, hasBoss = 0, 0
  do
    local totHp, totMax = 0, 0
    local okE, ents = pcall(function() return Isaac.GetRoomEntities() end)
    if okE and ents then
      for i = 1, #ents do
        local e = ents[i]
        local okB, isB = pcall(function() return e:IsBoss() end)
        if okB and isB then
          local okHp, hp   = pcall(function() return e.HitPoints end)
          local okMx, mx   = pcall(function() return e.MaxHitPoints end)
          if okHp and hp and okMx and mx and mx > 0 then
            totHp = totHp + math.max(0, hp); totMax = totMax + mx
          end
        end
      end
    end
    if totMax > 0 then hasBoss = 1; bossFill = totHp / totMax end
  end
  if bossFill < 0 then bossFill = 0 elseif bossFill > 1 then bossFill = 1 end

  -- Pill-effect popup: set by MC_USE_PILL, shown for a short time after use.
  local popup = ""
  if frame <= pillPopupUntil then popup = pillPopup end
  if #popup > 80 then popup = popup:sub(1, 80) end

  -- Minimap: every room descriptor's grid position, type, and display/clear state.
  local curIdx, roomCount, roomParts = -1, 0, {}
  local okL, level = pcall(function() return Game():GetLevel() end)
  if okL and level then
    curIdx = pv(function() return level:GetCurrentRoomIndex() end, -1)

    -- Record what's currently lying around in this room, for the minimap.
    local curMask = 0
    local okE, ents = pcall(function() return Isaac.GetRoomEntities() end)
    if okE and ents then
      for i = 1, #ents do
        local e = ents[i]
        local okT, t = pcall(function() return e.Type end)
        if okT and t == 5 then                      -- ENTITY_PICKUP
          -- Skip PRICED pickups (shop stock, devil-deal items): those are for sale,
          -- not leftover loot, so the minimap shouldn't pip them.
          local priced = false
          local okPk, pk = pcall(function() return e:ToPickup() end)
          if okPk and pk then
            local okPr, pr = pcall(function() return pk.Price end)
            if okPr and pr and pr ~= 0 then priced = true end
          end
          local okV, v = pcall(function() return e.Variant end)
          if (not priced) and okV and v then
            if v == 100 then                        -- collectible: only if still holds an item
              local okS, sub = pcall(function() return e.SubType end)
              if okS and sub and sub > 0 then curMask = curMask | 32 end
            else
              curMask = curMask | classifyPickup(v)
            end
          end
        end
      end
    end
    if curIdx and curIdx >= 0 and curIdx < 32768 then roomContents[curIdx] = curMask end

    local okR, rooms = pcall(function() return level:GetRooms() end)
    if okR and rooms then
      local sz = pv(function() return rooms.Size end, 0)
      for i = 0, sz - 1 do
        if roomCount >= 512 then break end
        local okRd, rd = pcall(function() return rooms:Get(i) end)
        if okRd and rd then
          local gi = pv(function() return rd.GridIndex end, -1)
          local dtype, shape = 0, 1
          local okD, data = pcall(function() return rd.Data end)
          if okD and data then
            dtype = pv(function() return data.Type end, 0)
            shape = pv(function() return data.Shape end, 1)
          end
          local dflags = pv(function() return rd.DisplayFlags end, 0)
          local clear  = pv(function() return rd.Clear end, false) and 1 or 0
          local visited = pv(function() return rd.VisitedCount end, 0)
          local vbyte  = (visited and visited > 0) and 1 or 0
          -- OR leftover-pickup contents across every cell this room occupies, so
          -- large/L rooms report items no matter which sub-cell we stored them under.
          local contents = 0
          local ggx, ggy = gi % 13, math.floor(gi / 13)
          for _, off in ipairs(shapeCells(shape)) do
            contents = contents | (roomContents[(ggy + off[2]) * 13 + (ggx + off[1])] or 0)
          end
          if gi and gi >= 0 and gi < 32768 then
            roomCount = roomCount + 1
            roomParts[roomCount] = string.pack("<hBBBBBB", gi, dtype % 256, shape % 256,
                                               dflags % 256, clear, vbyte, contents % 256)
          end
        end
      end
    end
  end

  -- Curses in effect, for the app's curse visuals (darkness / hide minimap / hide hearts).
  local curses = 0
  do
    local okL, lvl = pcall(function() return Game():GetLevel() end)
    if okL and lvl then curses = effectiveCurses(lvl) end
  end

  local pkt = string.pack("<BBBB", 0x49, PROTO_VERSION, MSG_HUD, 0)
    .. string.pack("<HHHB", coins % 65536, bombs % 65536, keys % 65536, flags1)
    .. string.pack("<BBBBBBB", clampU8(maxH), clampU8(redH), clampU8(soul),
                   clampU8(bone), clampU8(rotten), clampU8(eternal), clampU8(golden))
    .. string.pack("<H", blackMask % 65536)
    .. string.pack("<HBB", (activeId or 0) % 65536, clampU8(charge), clampU8(maxCharge))
    .. string.pack("<H", #iconPath) .. iconPath
    .. string.pack("<HH", (t0 or 0) % 65536, (t1 or 0) % 65536)
    .. string.pack("<H", #t0icon) .. t0icon
    .. string.pack("<H", #t1icon) .. t1icon
    .. string.pack("<BB", clampU8(card0), clampU8(pill0))
    .. string.pack("<B", npocket) .. table.concat(pockets)
    .. string.pack("<BH", hasBoss, math.floor(bossFill * 1000 + 0.5) % 65536)
    .. string.pack("<H", #popup) .. popup
    .. string.pack("<hH", curIdx, roomCount)
    .. table.concat(roomParts)
    .. string.pack("<B", curses % 256)          -- appended last: curse bitmask
  pcall(function() udp:send(pkt) end)
end

-- Pill used: capture the effect name for the app's popup bar. pillEffect here is
-- the resolved effect id (the game passes the real effect even for unidentified
-- pills, matching the on-screen name that pops up).
mod:AddCallback(ModCallbacks.MC_USE_PILL, function(_, pillEffect, player, useFlags)
  local name = ""
  local okC, cfg = pcall(function() return Isaac.GetItemConfig():GetPillEffect(pillEffect) end)
  if okC and cfg then name = locName(cfg) end
  if name == nil or name == "" then name = "Pill" end
  pillPopup = name
  pillPopupUntil = frame + 150      -- ~2.5s at 60fps
end)

----------------------------------------------------------------------------
-- Floor title on level start: "Basement I" plus any active curses, shown through
-- the SAME popup path as item pickups (title line + one line per curse). MOD-ONLY:
-- the app already renders MSG_HUD's popup string, so no app rebuild is needed.
--
-- There is NO Level:GetName() in REPENTOGON (only SetName), so the title is derived
-- from the vanilla level:GetStage() + level:GetStageType(), mapped to the stage ids
-- in resources/stages.xml. Display names try the game's own localisation first
-- (Isaac.GetString on the stages.xml #KEY) and fall back to English.

-- stages.xml id -> { localisation key, English fallback }
local STAGE_INFO = {
  [1]  = { "#BASEMENT_NAME",          "Basement" },
  [2]  = { "#CELLAR_NAME",            "Cellar" },
  [3]  = { "#BURNING_BASEMENT_NAME",  "Burning Basement" },
  [4]  = { "#CAVES_NAME",             "Caves" },
  [5]  = { "#CATACOMBS_NAME",         "Catacombs" },
  [6]  = { "#FLOODED_CAVES_NAME",     "Flooded Caves" },
  [7]  = { "#DEPTHS_NAME",            "Depths" },
  [8]  = { "#NECROPOLIS_NAME",        "Necropolis" },
  [9]  = { "#DANK_DEPTHS_NAME",       "Dank Depths" },
  [10] = { "#WOMB_NAME",              "Womb" },
  [11] = { "#UTERO_NAME",             "Utero" },
  [12] = { "#SCARRED_WOMB_NAME",      "Scarred Womb" },
  [13] = { "#BLUE_WOMB_NAME",         "Blue Womb" },
  [14] = { "#SHEOL_NAME",             "Sheol" },
  [15] = { "#CATHEDRAL_NAME",         "Cathedral" },
  [16] = { "#DARK_ROOM_NAME",         "Dark Room" },
  [17] = { "#CHEST_NAME",             "Chest" },
  [26] = { "#THE_VOID_NAME",          "The Void" },
  [27] = { "#DOWNPOUR_NAME",          "Downpour" },
  [28] = { "#DROSS_NAME",             "Dross" },
  [29] = { "#MINES_NAME",             "Mines" },
  [30] = { "#ASHPIT_NAME",            "Ashpit" },
  [31] = { "#MAUSOLEUM_NAME",         "Mausoleum" },
  [32] = { "#GEHENNA_NAME",           "Gehenna" },
  [33] = { "#CORPSE_NAME",            "Corpse" },
  [35] = { "#HOME_NAME",              "Home" },
  [36] = { "#BACKWARDS_NAME",         "Backwards" },
}

-- stage group -> StageType -> stages.xml id.
-- StageType: 0 original, 1 WOTL, 2 Afterbirth, 4 Repentance, 5 Repentance-B.
local STAGE_BY_TYPE = {
  basement = { [0]=1,  [1]=2,  [2]=3,  [4]=27, [5]=28 },
  caves    = { [0]=4,  [1]=5,  [2]=6,  [4]=29, [5]=30 },
  depths   = { [0]=7,  [1]=8,  [2]=9,  [4]=31, [5]=32 },
  womb     = { [0]=10, [1]=11, [2]=12, [4]=33, [5]=33 },
}

-- LevelCurse bit, localisation key, English fallback (ids from resources/curses.xml).
local CURSE_LIST = {
  { 1,   "#CURSE_OF_DARKNESS_NAME",      "Curse of Darkness" },
  { 2,   "#CURSE_OF_THE_LABYRINTH_NAME", "Curse of the Labyrinth" },
  { 4,   "#CURSE_OF_THE_LOST_NAME",      "Curse of the Lost" },
  { 8,   "#CURSE_OF_THE_UNKNOWN_NAME",   "Curse of the Unknown" },
  { 16,  "#CURSE_OF_THE_CURSED_NAME",    "Curse of the Cursed" },
  { 32,  "#CURSE_OF_THE_MAZE_NAME",      "Curse of the Maze" },
  { 64,  "#CURSE_OF_THE_BLIND_NAME",     "Curse of the Blind" },
  { 128, "#CURSE_OF_THE_GIANT_NAME",     "Curse of the Giant" },
}

-- Resolve a #KEY through the game's localisation, else use the English fallback.
-- Length-capped so a wrong category can't drag in a long description string.
local STAGE_CATS = { "Stages", "Curses", "UI", "EntityName", "Items" }
local function localOrFallback(key, fallback)
  local bare = key:gsub("^#", "")
  for _, cat in ipairs(STAGE_CATS) do
    for _, k in ipairs({ bare, key }) do
      local ok, s = pcall(function() return Isaac.GetString(cat, k) end)
      -- Isaac.GetString returns the LITERAL string "StringTable::InvalidKey" when a key
      -- doesn't resolve in that category — it is short and has no leading '#', so it sails
      -- past the other guards. Reject it (and anything else that smells like an error token)
      -- or the curse line renders as "StringTable::InvalidKey".
      if ok and type(s) == "string" and s ~= "" and s ~= k and s ~= bare
         and s:sub(1,1) ~= "#" and #s <= 40
         and not s:find("InvalidKey", 1, true)
         and not s:find("StringTable", 1, true) then
        return cleanStr(s)
      end
    end
  end
  return fallback
end

-- Test one bit of a mask without relying on bitwise operators.
local function hasBit(mask, bit)
  return (mask % (bit * 2)) >= bit
end

local function stageIdFor(stage, stype)
  local group
  if     stage <= 2 then group = "basement"
  elseif stage <= 4 then group = "caves"
  elseif stage <= 6 then group = "depths"
  elseif stage <= 8 then group = "womb" end
  if group then
    local t = STAGE_BY_TYPE[group]
    return t[stype] or t[0]
  end
  if stage == 9  then return 13 end                        -- Blue Womb
  if stage == 10 then return (stype == 1) and 15 or 14 end -- Cathedral / Sheol
  if stage == 11 then return (stype == 1) and 17 or 16 end -- Chest / Dark Room
  if stage == 12 then return 26 end                        -- The Void
  if stage == 13 then return 35 end                        -- Home
  return nil
end

-- "Basement I" / "Caves II" / "Basement I-II" (Labyrinth merges the two floors).
local function floorTitle(lvl, curses)
  local okS, stage = pcall(function() return lvl:GetStage() end)
  local okT, stype = pcall(function() return lvl:GetStageType() end)
  if not okS or type(stage) ~= "number" then return "" end
  if not okT or type(stype) ~= "number" then stype = 0 end
  local id = stageIdFor(stage, stype)
  if not id then return "" end
  local info = STAGE_INFO[id]
  if not info then return "" end
  local name = localOrFallback(info[1], info[2])
  if stage >= 1 and stage <= 8 then
    if hasBit(curses, 2) then                              -- Curse of the Labyrinth
      name = name .. " I-II"
    else
      name = name .. (((stage - 1) % 2 == 0) and " I" or " II")
    end
  end
  return name
end

-- One line per active curse (the app draws each description line separately).
local function curseLines(curses)
  local out = ""
  if curses <= 0 then return out end
  for _, c in ipairs(CURSE_LIST) do
    if hasBit(curses, c[1]) then
      out = out .. (out ~= "" and "\n" or "") .. localOrFallback(c[2], c[3])
    end
  end
  return out
end

-- The ONLY reliable room-change signal. The backdrop signature (type/shape/bounds,
-- plus index/seeds) describes what a room LOOKS like, and the Dogma fight swaps in
-- "Home Default 1000 Dogma Test" at the same index with the same shape, bounds and
-- backdrop -- so a signature can miss it entirely. This callback cannot.
mod:AddCallback(ModCallbacks.MC_POST_NEW_ROOM, function()
  roomChanged = true
  dogmaHp, dogmaHurtUntil = {}, {}
  dropInfo = {}
end)

mod:AddCallback(ModCallbacks.MC_POST_NEW_LEVEL, function()
  local okL, lvl = pcall(function() return Game():GetLevel() end)
  if not okL or not lvl then return end
  local curses = effectiveCurses(lvl)
  local title = floorTitle(lvl, curses)
  if title == "" then return end
  local lines = curseLines(curses)
  setPopup(title .. (lines ~= "" and ("\n" .. lines) or ""))
end)

----------------------------------------------------------------------------
-- Game state (menus / pause / cutscenes). When we're NOT in normal gameplay the
-- app hides nothing but overlays a flat panel showing the real Isaac window
-- (captured on the PC), so pause menus, the map, boss versus intros, item
-- fanfares and the main menu are all visible in VR. Sent as a single byte:
--   bit0 (1) = paused          (pause menu / map screen)
--   bit1 (2) = HUD hidden      (boss intro, cutscene, item pickup pose, fanfare)
--   bit2 (4) = no active run    (main menu, game over, save select)
-- The app shows the flat screen whenever the byte is non-zero.
local lastState = -1
local stateTick = 0

local function computeState()
  local s = 0
  local okG, game = pcall(function() return Game() end)
  if not okG or not game then return 4 end        -- no game object -> treat as menu

  local okP, paused = pcall(function() return game:IsPaused() end)
  if okP and paused then s = s + 1 end

  local okH, hud = pcall(function() return game:GetHUD() end)
  if okH and hud then
    local okV, vis = pcall(function() return hud:IsVisible() end)
    if okV and vis == false then s = s + 2 end     -- HUD off => cutscene/intro/fanfare
  end

  -- No controllable player => main menu / game-over / character select.
  local okPl, plr = pcall(function() return Isaac.GetPlayer(0) end)
  if not okPl or plr == nil then
    s = s + 4
  else
    -- A run that hasn't started yet (frame count 0 on the menu) also counts.
    local okF, fc = pcall(function() return game:GetFrameCount() end)
    if okF and fc ~= nil and fc <= 0 then s = s + 4 end
  end
  -- Bit 8: the DOWNPOUR/DROSS MIRROR DIMENSION. The game flips that whole stage
  -- horizontally at RENDER time only — the coordinates it reports are unflipped — so the
  -- app has to mirror the diorama itself.
  -- Dimension 1 alone is NOT enough: the Mines/Ashpit mineshaft chase also runs in
  -- dimension 1. Gate on CHAPTER 1 with an alt stage type, which the chase (chapter 2)
  -- can never satisfy, and on HasMirrorDimension() so it is only ever set on a floor that
  -- actually has a mirror.
  local okLv, lvl = pcall(function() return Game():GetLevel() end)
  if okLv and lvl then
    local function lv(fn, d) local ok, v = pcall(fn); if ok and v ~= nil then return v end; return d end
    local dim   = lv(function() return lvl:GetDimension() end, 0)
    local stage = lv(function() return lvl:GetStage() end, 0)
    local stype = lv(function() return lvl:GetStageType() end, 0)
    local hasMirror = lv(function() return lvl:HasMirrorDimension() end, false)
    local downpour = (stage == 1 or stage == 2) and (stype == 4 or stype == 5)
    if dim == 1 and downpour and hasMirror then s = s + 8 end
    if (frame % 120) == 0 then
      Isaac.DebugString(string.format("%s: DIM dim=%s stage=%d/%d hasMirror=%s mirror=%s",
        MOD_NAME, tostring(dim), stage, stype, tostring(hasMirror),
        tostring(dim == 1 and downpour and hasMirror)))
    end
  end
  -- Bit 16: the MAP / SELECT button HELD.
  -- The app used to read this off an XInput pad, but the Quest controller is bound straight
  -- to Isaac: the app's XInput read is starved and Select never reached it, so holding the
  -- button never showed the flat game screen in VR. The GAME is the thing holding the input,
  -- so the game is what should report it.
  --
  -- Read through ACTION_MAP rather than a raw button so it follows whatever the player has
  -- actually bound, on a pad or the keyboard. Player 0's own ControllerIndex first; then a
  -- scan, because the keyboard and a second pad live on their own indices.
  local mapHeld = false
  local ci = 0
  local okPl2, plr2 = pcall(function() return Isaac.GetPlayer(0) end)
  if okPl2 and plr2 then
    local okC, c = pcall(function() return plr2.ControllerIndex end)
    if okC and c then ci = c end
  end
  local okM, m = pcall(function() return Input.IsActionPressed(ButtonAction.ACTION_MAP, ci) end)
  if okM and m then mapHeld = true end
  if not mapHeld then
    for i = 0, 4 do
      local okI, vi = pcall(function() return Input.IsActionPressed(ButtonAction.ACTION_MAP, i) end)
      if okI and vi then mapHeld = true; break end
    end
  end
  if mapHeld then s = s + 16 end

  return s
end

-- Water. The game renders it as a post-process over the already-drawn floor (see
-- resources/shaders/water.fs and water_v2.fs), which we cannot reproduce — so the app
-- fakes it as a translucent, rippling plane laid ON the floor. Everything it needs to
-- match the room comes from here.
--   GetWaterAmount()  0 = dry, 1 = full. Changes live in the rooms that flood.
--   GetWaterCurrent() the flow vector; water_v2 turns it into directional waves and the
--                     game pushes entities along it, so the ripples must follow it.
--   FXParams          WaterColor / WaterColorMultiplier / WaterEffectColor / UseWaterV2.
-- Sent on change (rounded, so a jittering float doesn't spam) plus a slow heartbeat.
local lastWaterSig, lastWaterAt = nil, -999
-- ---------------------------------------------------------------------------
-- MSG_STATS (15): the numbers the 2D HUD never shows, plus EID's description of
-- whatever the player is standing next to.
--
--   f MoveSpeed, f FireRate(tears/sec), f Damage, f Range(tiles), f ShotSpeed, f Luck,
--   f PlanetariumChance(0..1), f AngelChance(modifier), B haveEid,
--   H len + name, H len + description
--
-- FIRE RATE: the game exposes MaxFireDelay, which is a COOLDOWN in frames, not a rate —
-- bigger is slower. The in-game stat everyone quotes is tears/second = 30/(MaxFireDelay+1).
-- RANGE: TearRange is in game units; a tile is 40, and the range stat is quoted in tiles.
--
-- DEVIL ROOM CHANCE IS DELIBERATELY ABSENT. There is no API for it (checked: neither Level
-- nor Game exposes one in REPENTOGON) and it depends on damage taken, deals already taken,
-- items held and the stage. Re-deriving the game's own formula would produce a number that
-- silently drifts from the real one, which is worse than no number. Angel chance is the
-- MODIFIER `Level:GetAngelRoomChance()` returns, not a probability -- the app labels it so.
local function eidTextFor(plr)
  -- EID is a separate mod. Everything here is pcall-wrapped and returns empty on any
  -- failure, so a missing, disabled or changed EID costs us nothing but a blank panel.
  if type(EID) ~= "table" then return false, "", "" end
  local okPos, ppos = pcall(function() return plr.Position end)
  if not okPos or not ppos then return false, "", "" end

  -- Nearest PICKUP within EID's own sort of range. Pedestals first in spirit: we just take
  -- the closest, which is what standing next to one gives you.
  local best, bestD = nil, 1e18
  local okE, ents = pcall(function() return Isaac.GetRoomEntities() end)
  if not okE or not ents then return false, "", "" end
  for i = 1, #ents do
    local e = ents[i]
    local okT, t = pcall(function() return e.Type end)
    if okT and t == 5 then                       -- ENTITY_PICKUP
      local okEp, epos = pcall(function() return e.Position end)
      if okEp and epos then
        local dx, dy = epos.X - ppos.X, epos.Y - ppos.Y
        local d2 = dx*dx + dy*dy
        if d2 < bestD and d2 < (80*80) then best, bestD = e, d2 end
      end
    end
  end
  if not best then return false, "", "" end

  local okV, va = pcall(function() return best.Variant end)
  local okS, su = pcall(function() return best.SubType end)
  if not okV or not okS then return false, "", "" end
  if (su or 0) == 0 then return false, "", "" end   -- empty pedestal

  -- PILLS (variant 70) must not spoil themselves. EID in game respects whether the pill is
  -- identified; asking getDescriptionObj for a raw colour does not, so an unidentified pill
  -- was showing its effect as if PHD were held. `ItemPool:IsPillIdentified(colour)` is the
  -- one call that covers BOTH ways a pill becomes known -- PHD / False PHD / Virgo held, or
  -- the colour already seen this run -- so it is the whole test.
  -- The giant/horse flag (1<<11) rides in the SubType and is not part of the colour.
  if va == 70 then
    local colour = (su or 0) % 2048
    local known = false
    local okPool, pool = pcall(function() return Game():GetItemPool() end)
    if okPool and pool then
      local okId, id = pcall(function() return pool:IsPillIdentified(colour) end)
      if okId and id then known = true end
    end
    if not known then return false, "", "" end
  end

  local okD, obj = pcall(function() return EID:getDescriptionObj(5, va, su, best) end)
  if not okD or type(obj) ~= "table" then return false, "", "" end
  local name = ""
  local desc = ""
  pcall(function() if type(obj.Name) == "string" then name = obj.Name end end)
  pcall(function() if type(obj.Description) == "string" then desc = obj.Description end end)
  if name == "" and desc == "" then return false, "", "" end
  return true, name, desc
end

local function sendStats()
  if not udp or not havePack then return end
  local okP, plr = pcall(function() return Isaac.GetPlayer(0) end)
  if not okP or not plr then return end
  local function pv(fn, d) local ok, v = pcall(fn); if ok and v ~= nil then return v end; return d end

  local speed = pv(function() return plr.MoveSpeed end, 0) or 0
  local delay = pv(function() return plr.MaxFireDelay end, 0) or 0
  local rate  = 30.0 / ((tonumber(delay) or 0) + 1.0)
  local dmg   = pv(function() return plr.Damage end, 0) or 0
  local rng   = (pv(function() return plr.TearRange end, 0) or 0) / 40.0
  local shot  = pv(function() return plr.ShotSpeed end, 0) or 0
  local luck  = pv(function() return plr.Luck end, 0) or 0

  local planet, angel = 0, 0
  local okL, lvl = pcall(function() return Game():GetLevel() end)
  if okL and lvl then
    planet = pv(function() return lvl:GetPlanetariumChance() end, 0) or 0
    angel  = pv(function() return lvl:GetAngelRoomChance() end, 0) or 0
  end

  local haveEid, eName, eDesc = eidTextFor(plr)
  -- Keep the packet inside one datagram: EID descriptions can run long.
  if #eName > 200 then eName = eName:sub(1, 200) end
  if #eDesc > 900 then eDesc = eDesc:sub(1, 900) end

  local function pstr(x) x = x or ""; return string.pack("<H", #x) .. x end
  local pkt = string.pack("<BBBB", 0x49, PROTO_VERSION, MSG_STATS, 0)
           .. string.pack("<ffffff", speed, rate, dmg, rng, shot, luck)
           .. string.pack("<ff", planet, angel)
           .. string.pack("<B", haveEid and 1 or 0)
           .. pstr(eName) .. pstr(eDesc)
  pcall(function() udp:send(pkt) end)
end

local function sendWater(frame)
  if not udp or not havePack then return end
  local okR, room = pcall(function() return Game():GetRoom() end)
  if not okR or not room then return end
  local function rv(fn, d) local ok, v = pcall(fn); if ok and v ~= nil then return v end; return d end

  local amount = rv(function() return room:GetWaterAmount() end, 0) or 0
  if type(amount) ~= "number" then amount = 0 end
  local cur = rv(function() return room:GetWaterCurrent() end, nil)
  local cx, cy = 0, 0
  if cur then cx = rv(function() return cur.X end, 0) or 0; cy = rv(function() return cur.Y end, 0) or 0 end

  -- FXParams colours. KColor fields are floats (a multiplier can exceed 1), so they go
  -- over the wire as floats rather than bytes.
  local v2 = 0
  local wr, wg, wb, wa = 1, 1, 1, 1
  local mr, mg, mb = 1, 1, 1
  local er, eg, eb, ea = 1, 1, 1, 1
  -- COLOUR SOURCE. REPENTOGON puts the live values on Room itself (GetWaterColor /
  -- GetWaterColorMultiplier) and those are the ones the renderer uses; the FXParams copy
  -- is the room's static template and came back as plain white in Dross, which is what
  -- made the app fall back to a generic blue. Room first, FXParams only as a backstop.
  local src = "none"
  -- ⚠ A KColor's fields are Red/Green/Blue/Alpha. R/G/B/A belong to COLOR (the entity
  -- tint) — a different class. Reading .R off a KColor returns nil, which is why every
  -- water colour silently came back as "none" and the app fell through to its generic
  -- blue even though GetWaterColor() was working the whole time (it prints as userdata,
  -- so the console gave no hint either). Try the KColor names first, then Color's, so
  -- this works whatever the accessor hands back.
  local function readKColor(fn)
    local ok, c = pcall(fn)
    if not ok or c == nil then return nil end
    local r = rv(function() return c.Red end, nil)
    if r ~= nil then
      return r, rv(function() return c.Green end,1) or 1,
                rv(function() return c.Blue  end,1) or 1,
                rv(function() return c.Alpha end,1) or 1
    end
    r = rv(function() return c.R end, nil)
    if r == nil then return nil end
    return r, rv(function() return c.G end,1) or 1, rv(function() return c.B end,1) or 1,
           rv(function() return c.A end,1) or 1
  end

  local okFx, fx = pcall(function() return room:GetFXParams() end)
  if okFx and fx then
    if rv(function() return fx.UseWaterV2 end, false) then v2 = 1 end
  end

  local r1,g1,b1,a1 = readKColor(function() return room:GetWaterColor() end)
  if r1 then wr,wg,wb,wa = r1,g1,b1,a1; src = "Room"
  elseif okFx and fx then
    r1,g1,b1,a1 = readKColor(function() return fx.WaterColor end)
    if r1 then wr,wg,wb,wa = r1,g1,b1,a1; src = "FXParams" end
  end

  local r2,g2,b2 = readKColor(function() return room:GetWaterColorMultiplier() end)
  if not r2 and okFx and fx then r2,g2,b2 = readKColor(function() return fx.WaterColorMultiplier end) end
  if r2 then mr,mg,mb = r2,g2,b2 end

  local r3,g3,b3,a3 = readKColor(function() return room:GetWaterEffectColor() end)
  if not r3 and okFx and fx then r3,g3,b3,a3 = readKColor(function() return fx.WaterEffectColor end) end
  if r3 then er,eg,eb,ea = r3,g3,b3,a3 end

  local sig = string.format("%.3f_%.2f_%.2f_%d_%.2f_%.2f_%.2f_%.2f_%.2f_%.2f_%.2f",
    amount, cx, cy, v2, wr, wg, wb, wa, mr, mg, mb)
  if sig == lastWaterSig and (frame - lastWaterAt) < 60 then return end
  if sig ~= lastWaterSig then
    Isaac.DebugString(string.format(
      "%s: WATER amount=%.3f current=(%.1f,%.1f) v2=%d color=(%.2f,%.2f,%.2f,%.2f) mul=(%.2f,%.2f,%.2f) src=%s",
      MOD_NAME, amount, cx, cy, v2, wr, wg, wb, wa, mr, mg, mb, src))
  end
  lastWaterSig, lastWaterAt = sig, frame

  local pkt = string.pack("<BBBB", 0x49, PROTO_VERSION, MSG_WATER, v2)
           .. string.pack("<fff", amount, cx, cy)
           .. string.pack("<ffff", wr, wg, wb, wa)
           .. string.pack("<fff", mr, mg, mb)
           .. string.pack("<ffff", er, eg, eb, ea)
  pcall(function() udp:send(pkt) end)
end

local function sendState(force)
  if not udp or not havePack then return end
  local s = computeState()
  if (not force) and s == lastState then return end
  lastState = s
  local pkt = string.pack("<BBBB", 0x49, PROTO_VERSION, MSG_STATE, s)
  pcall(function() udp:send(pkt) end)
end

----------------------------------------------------------------------------
-- Gamepad passthrough. The app reads an XInput pad and sends its state to
-- INPUT_PORT; we inject it via MC_INPUT_ACTION so it drives Isaac even while
-- Isaac is unfocused (our VR app holds focus).
local PAD_DEAD  = 8000    -- stick deadzone (XInput axis range 0..32767)
local PAD_STALE = 20      -- frames without a packet -> pad treated as neutral
local pad = { buttons=0, prev=0, lt=0, rt=0, lx=0, ly=0, rx=0, ry=0, idle=999 }
local XB = { DUP=0x0001, DDOWN=0x0002, DLEFT=0x0004, DRIGHT=0x0008,
             START=0x0010, BACK=0x0020, LB=0x0100, RB=0x0200,
             A=0x1000, B=0x2000, X=0x4000, Y=0x8000 }

local function pollInput()
  if not udpIn then return end
  local last
  for _ = 1, 32 do local d = udpIn:receive(); if not d then break end; last = d end
  if last and havePack and #last >= 16 and last:byte(1) == 0x49 and last:byte(3) == 9 then
    local pos = 5
    local b;  b,  pos = string.unpack("<H", last, pos)
    local lt; lt, pos = string.unpack("<B", last, pos)
    local rt; rt, pos = string.unpack("<B", last, pos)
    local lx; lx, pos = string.unpack("<h", last, pos)
    local ly; ly, pos = string.unpack("<h", last, pos)
    local rx; rx, pos = string.unpack("<h", last, pos)
    local ry; ry, pos = string.unpack("<h", last, pos)
    pad.prev = pad.buttons
    pad.buttons=b; pad.lt=lt; pad.rt=rt; pad.lx=lx; pad.ly=ly; pad.rx=rx; pad.ry=ry
    pad.idle = 0
  end
end

local function axisPos(v) if v <= PAD_DEAD then return 0 end
  return math.min(1, (v - PAD_DEAD) / (32767 - PAD_DEAD)) end
local function axisNeg(v) return axisPos(-v) end
local function btn(m)     return (pad.buttons & m) ~= 0 end
local function btnEdge(m) return (pad.buttons & m) ~= 0 and (pad.prev & m) == 0 end

-- Analog value 0..1 for movement/shooting; nil if not one of those actions.
local function actionAnalog(action)
  local A = ButtonAction
  if action == A.ACTION_LEFT  then return math.max(axisNeg(pad.lx), btn(XB.DLEFT)  and 1 or 0) end
  if action == A.ACTION_RIGHT then return math.max(axisPos(pad.lx), btn(XB.DRIGHT) and 1 or 0) end
  if action == A.ACTION_UP    then return math.max(axisPos(pad.ly), btn(XB.DUP)    and 1 or 0) end
  if action == A.ACTION_DOWN  then return math.max(axisNeg(pad.ly), btn(XB.DDOWN)  and 1 or 0) end
  if action == A.ACTION_SHOOTLEFT  then return axisNeg(pad.rx) end
  if action == A.ACTION_SHOOTRIGHT then return axisPos(pad.rx) end
  if action == A.ACTION_SHOOTUP    then return axisPos(pad.ry) end
  if action == A.ACTION_SHOOTDOWN  then return axisNeg(pad.ry) end
  return nil
end

-- Default button map (Xbox layout). Rebind here if desired.
local function actionButtonMask(action)
  local A = ButtonAction
  if action == A.ACTION_BOMB        then return XB.A end
  if action == A.ACTION_ITEM        then return XB.Y end
  if action == A.ACTION_PILLCARD    then return XB.B end
  if action == A.ACTION_DROP        then return XB.X end
  if action == A.ACTION_MAP         then return XB.BACK end
  if action == A.ACTION_PAUSE       then return XB.START end
  if action == A.ACTION_MENUCONFIRM then return XB.A end
  if action == A.ACTION_MENUBACK    then return XB.B end
  if action == A.ACTION_MENUUP      then return XB.DUP end
  if action == A.ACTION_MENUDOWN    then return XB.DDOWN end
  if action == A.ACTION_MENULEFT    then return XB.DLEFT end
  if action == A.ACTION_MENURIGHT   then return XB.DRIGHT end
  return nil
end

mod:AddCallback(ModCallbacks.MC_INPUT_ACTION, function(_, entity, hook, action)
  if pad.idle > PAD_STALE then return nil end   -- no fresh gamepad data -> don't inject
  local IH = InputHook
  local av = actionAnalog(action)
  if av ~= nil then
    if hook == IH.GET_ACTION_VALUE then return av > 0 and av or nil end
    return av > 0.25 and true or nil            -- pressed / triggered
  end
  local mask = actionButtonMask(action)
  if not mask then return nil end
  local on = (hook == IH.IS_ACTION_TRIGGERED) and btnEdge(mask) or btn(mask)
  if hook == IH.GET_ACTION_VALUE then return on and 1.0 or nil end
  return on and true or nil
end)

mod:AddCallback(ModCallbacks.MC_POST_RENDER, function()
  pad.idle = pad.idle + 1
  pollInput()
  if not havePack or not udp then return end

  -- Report game state every frame (send on change + a ~0.5s heartbeat). This runs
  -- BEFORE the room guard so the main menu / game-over screens are reported too.
  stateTick = stateTick + 1
  sendState((stateTick % 30) == 0)

  local okRoom, room = pcall(function() return Game():GetRoom() end)
  if not okRoom or not room then return end
  local tl, br = room:GetTopLeftPos(), room:GetBottomRightPos()

  -- Pickup popup: watch for a newly-picked collectible / trinket and pop its
  -- name + the game's flavour text, reusing the pill-popup UI.
  do
    local okPlr, plr = pcall(function() return Isaac.GetPlayer(0) end)
    if okPlr and plr then pcall(checkPickups, plr) end
  end

  local recs, n = {}, 0
  local recs3, n3 = {}, 0   -- REPENTOGON layer-state records

  -- Entities Isaac is ACTIVELY HOLDING (Mom's Bracelet rocks/TNT, throwable bombs).
  -- REPENTOGON: `EntityPlayer:GetHeldEntity()`. Distinct from GetHeldSprite (the pickup pose
  -- for passives/trinkets) — this is a REAL entity that moves and gets thrown.
  -- WHY IT WAS INVISIBLE: while held, the game marks the room entity `Visible = false` (it
  -- draws it itself, attached to the player), and the export loop below skips invisible
  -- entities. So the held object must be exempted from that filter and flagged so the app
  -- lifts it above Isaac's head.
  -- Collected BEFORE the loop because the held entity can appear EARLIER in the room list
  -- than the player holding it.
  local heldEnts, anyHeld = {}, false
  do
    local okN, np = pcall(function() return Game():GetNumPlayers() end)
    if okN and type(np) == "number" then
      for pi = 0, np - 1 do
        local okP, plr = pcall(function() return Isaac.GetPlayer(pi) end)
        if okP and plr then
          local okH, he = pcall(function() return plr:GetHeldEntity() end)
          if okH and he then
            local okHash, hh = pcall(function() return GetPtrHash(he) end)
            if okHash and hh then
              heldEnts[hh] = true; anyHeld = true
              if hh ~= lastHeldLogged then
                lastHeldLogged = hh
                local ht, hv, hs, hvis = -1, -1, -1, "?"
                pcall(function() ht = he.Type; hv = he.Variant; hs = he.SubType end)
                pcall(function() hvis = tostring(he.Visible) end)
                Isaac.DebugString(string.format(
                  "[diorama] HELD ENTITY %d.%d.%d visible=%s", ht, hv, hs, hvis))
              end
            end
          end
        end
      end
    end
  end
  if not anyHeld then lastHeldLogged = 0 end

  local ents = Isaac.GetRoomEntities()

  -- Rain spawns continuously, so dropInfo would grow for as long as you stand in a
  -- Downpour room. Sweep entries not seen recently. (A recycled pointer hash would also
  -- inherit a stale spawn Y and pop; dropping them promptly avoids that too.)
  if frame - dropPrunedAt >= DROP_PRUNE then
    dropPrunedAt = frame
    for k, v in pairs(dropInfo) do
      if (v.seen or 0) < frame - 60 then dropInfo[k] = nil end
    end
  end

  -- Is Dogma in this room? The game binds coloroffset_dogma.fs PER ENTITY, so the app
  -- needs the same per-entity answer -- a whole-spritesheet heuristic cannot work for
  -- shared atlases (BulletAtlas.png is only ~8% blue-coded because only DOGMA's bullets
  -- are drawn that way; the rest are ordinary projectiles on the same sheet).
  local dogmaHere = false
  for i = 1, #ents do
    local okT, t = pcall(function() return ents[i].Type end)
    if okT and t == 950 then dogmaHere = true; break end
  end

  -- MEASURED (MOTHERMOTION, 2026-09-16): only 912.0.0's sprite animates —
  --   912.0.0 Appear -> ThrowKnife(7..67) -> Idle -> GroundPound2 ...
  --   912.0.1/2/3    anim='Appear' frame=0, FOREVER
  -- so the arm ENTITIES are pure collision/position proxies and the arm LAYERS are
  -- animated by the BODY's animation. Each arm therefore has to borrow the body's anim
  -- and frame, or it draws its layer frozen on frame 0 (which is what happened).
  local motherAnim, motherFrame = nil, 0
  -- motherHere = any 912 (Mother) is on the field, i.e. we are in her fight. Used to scope
  -- the fake-wall hide below; deliberately ANY variant, so it stays true across the phase
  -- change rather than switching off the moment phase 1 dies.
  local motherHere = false
  for i = 1, #ents do
    local me = ents[i]
    local okT, t = pcall(function() return me.Type end)
    if okT and t == 912 then
      motherHere = true
      local okV, v = pcall(function() return me.Variant end)
      local okS, su = pcall(function() return me.SubType end)
      if okV and v == 0 then
        -- The body (subtype 0) drives the animation every arm borrows. Do NOT break on
        -- the first 912.x: subtype 0 can die while the arms are still up, and an early
        -- break would also stop motherHere from seeing the rest of the list.
        if okS and su == 0 and motherAnim == nil then
          local okSp2, msp = pcall(function() return me:GetSprite() end)
          if okSp2 and msp then
            local okA, av = pcall(function() return msp:GetAnimation() end)
            local okF, fv = pcall(function() return msp:GetFrame() end)
            if okA and av then motherAnim = av end
            if okF and fv then motherFrame = fv end
          end
        end
      end
    end
  end

  for i = 1, #ents do
    if n >= MAX_ENTITIES then break end
    local e = ents[i]
    -- eflags bit5 (32) = held. Only hash-compare when something is actually held.
    local heldBit = 0
    if anyHeld and e then
      local okHash, hh = pcall(function() return GetPtrHash(e) end)
      if okHash and hh and heldEnts[hh] then heldBit = 32 end
    end
    -- eflags bit7 (128) = render this sprite through the Dogma TV-static shader.
    -- Dogma itself, and anything it spawned (bullets, creep, impacts, halos, debris).
    local dogmaBit = 0
    if dogmaHere and e then
      local okT2, t2 = pcall(function() return e.Type end)
      if okT2 and t2 == 950 then dogmaBit = 128
      elseif spawnerIsDogma(e) then dogmaBit = 128
      elseif ownerlessInDogmaRoom(e) then dogmaBit = 128 end
    end
    -- Water Droplet: scatter it in depth and express its fall as height (see dropInfo).
    local dropY, dropH = nil, 0
    if e then
      local okDt, dt = pcall(function() return e.Type end)
      local okDv, dv = pcall(function() return e.Variant end)
      if okDt and okDv and dt == 1000 and dv == 41 then
        local okHash, hh = pcall(function() return GetPtrHash(e) end)
        if okHash and hh then
          local di = dropInfo[hh]
          if not di then
            -- Two independent slices of the hash: one for the depth across the room, one
            -- for the fall height. Hashes are effectively random, so this reads as rain.
            local r1 = (math.floor(hh) % 997) / 997
            local r2 = (math.floor(hh / 997) % 991) / 991
            di = { y  = tl.Y + r1 * (br.Y - tl.Y),
                   sy = e.Position.Y,
                   h  = DROP_FALL * (1 - DROP_FALL_VAR + 2 * DROP_FALL_VAR * r2) }
            dropInfo[hh] = di
          end
          di.seen = frame
          dropY = di.y
          dropH = di.h - (e.Position.Y - di.sy)
          if dropH < 0 then dropH = 0 elseif dropH > di.h then dropH = di.h end
        end
      end
    end

    -- Damage flash for Dogma's own pieces (the TV especially): watch HP per entity.
    local dogmaHurt = 0
    if dogmaBit ~= 0 then
      local okHash, hh = pcall(function() return GetPtrHash(e) end)
      local okHp, hp = pcall(function() return e.HitPoints end)
      if okHash and hh and okHp and type(hp) == "number" then
        local prev = dogmaHp[hh]
        if prev and hp < prev - 0.001 then dogmaHurtUntil[hh] = frame + DOGMA_HURT_FRAMES end
        dogmaHp[hh] = hp
        if frame <= (dogmaHurtUntil[hh] or -1) then dogmaHurt = 16 end
      end
    end
    -- GROUND EFFECTS the generic liveness filter must not eat. The game draws some
    -- floor-baked effects itself and marks the entity Visible=false; for creep that was one
    -- of the two ways "Creep (Green)" could have been missing entirely, and Mother's tracer
    -- is the same kind of thing — a telegraph painted on the floor. Exempt both, the same
    -- way a held entity is exempted; an effect that is really gone leaves the entity list
    -- anyway, so the exemption cannot strand anything.
    local creepBit, tracerBit = false, false
    if e then
      local okCt, ct = pcall(function() return e.Type end)
      if okCt and ct == 1000 then
        local okCv, cv = pcall(function() return e.Variant end)
        if okCv and cv == 190 then tracerBit = true end
        local okCs, csp = pcall(function() return e:GetSprite() end)
        if okCs and csp then
          local okCf, cf = pcall(function() return csp:GetFilename() end)
          if okCf and cf and cf:lower():find("creep", 1, true) then creepBit = true end
        end
      end
    end
    if e and (e.Visible ~= false or heldBit ~= 0 or creepBit or tracerBit) then
      local pos = e.Position
      local cat = categorize(e)
      local scl = 1.0
      local okS, sv = pcall(function() return e.SpriteScale.X end); if okS and sv then scl = sv end

      -- Floor shadow. Isaac has NO shadow sprite file: the engine draws a shadow
      -- LAYER whose size is the entity's XML shadowSize (GetShadowSize = xml/100),
      -- further scaled by SpriteScale. We read that real size and export it as a
      -- u16 (size*1000) so the app can reproduce the shadow faithfully. Gated to
      -- the classes that should cast one (player/enemies/bosses, tears, pickups,
      -- and FAMILIARS that orbit Isaac — blue flies, cube of meat, bandage ball,
      -- etc.); effects/grid/doors get 0 = no shadow.
      local isFamiliar = (e.Type == EntityType.ENTITY_FAMILIAR)
      local shadowU16 = 0
      if cat == C_PLAYER or cat == C_ENEMY or cat == C_PICKUP or cat == C_PROJ or isFamiliar then
        local okCfg, cfg = pcall(function() return e:GetEntityConfigEntity() end)
        if okCfg and cfg then
          local okSh, sh = pcall(function() return cfg:GetShadowSize() end)
          if okSh and type(sh) == "number" and sh > 0 then
            local v = math.floor(sh * 1000 + 0.5)
            if v > 65535 then v = 65535 end
            shadowU16 = v
          end
        end
        -- Some familiars ship shadowSize 0 in the XML but visually should have one
        -- (they float, so a shadow grounds them). Give those a modest fallback.
        if isFamiliar and shadowU16 == 0 then shadowU16 = 90 end
      end

      -- Lasers (brimstone/tech/etc) are NOT billboards: they get their own polyline
      -- export below (MSG_LASERS). Skip them from the normal sprite path so they
      -- don't draw as a single stubby quad.
      local isLaser = (e.Type == EntityType.ENTITY_LASER)

      -- sprite identity
      local anm2, anim, aframe, flip = "", "", 0, 0
      local ovlAnim, ovlFrame = "", 0
      -- Force specific whole entities to lie FLAT on the floor (eflags bit1 = IsFlat).
      -- The app only receives Cat + anm2 name, not the raw Type, so the per-Type override
      -- is decided here from the FLAT_ENTITIES table (crawlers + floor-hugging enemies).
      local flat = 0
      local okTy, ty = pcall(function() return e.Type end)
      local flatVa = 0; local okFv, fvv = pcall(function() return e.Variant end);  if okFv and type(fvv)=="number" then flatVa = fvv end
      local flatSu = 0; local okFs, fsv = pcall(function() return e.SubType end);   if okFs and type(fsv)=="number" then flatSu = fsv end
      if okTy and ty and isFlatEntity(ty, flatVa, flatSu) then flat = 2 end
      -- Subtype-selected layers (Mother phase 1): nil for everything else.
      local subLayers = okTy and ty and layersForSubtype(ty, flatVa, flatSu) or nil
      local okSp, sp = pcall(function() return e:GetSprite() end)
      if okSp and sp then
        if okTy and ty == 912 then
          logMotherLayers(e, sp, ty, flatVa, flatSu)
          logMotherMotion(e, sp, flatVa, flatSu)
        end
        local okA; local v
        okA, v = pcall(function() return sp:GetFilename() end);   if okA and v then anm2 = v end
        -- Mother's arms (912.0.1-3) borrow the BODY's animation — see motherAnim above.
        local borrowMother = okTy and ty == 912 and flatVa == 0 and flatSu > 0 and motherAnim ~= nil
        okA, v = pcall(function() return sp:GetAnimation() end);  if okA and v then anim = v end
        okA, v = pcall(function() return sp:GetFrame() end);      if okA and v then aframe = v end
        if borrowMother then anim, aframe = motherAnim, motherFrame end
        okA, v = pcall(function() return sp.FlipX end);           if okA and v then flip = 1 end
        okA, v = pcall(function() return sp:GetOverlayAnimation() end); if okA and v then ovlAnim = v end
        okA, v = pcall(function() return sp:GetOverlayFrame() end);     if okA and v then ovlFrame = v end
      end

      -- colour + rotation. The rendered flash/status can live on the ENTITY colour
      -- or the SPRITE colour, so read both and keep whichever deviates more.
      local er,eg,eb,ea = 255,255,255,255   -- entity colour tint
      local ero,ego,ebo = 128,128,128       -- entity colour offset
      local okC, col = pcall(function() return e.Color end)
      if okC and col then
        er=clampByte(col.R); eg=clampByte(col.G); eb=clampByte(col.B); ea=clampByte(col.A)
        ero,ego,ebo = readOffset(col)
      end
      local sr,sg,sb,sa = er,eg,eb,ea       -- sprite colour tint
      local sro,sgo,sbo = ero,ego,ebo       -- sprite colour offset
      if okSp and sp then
        local okSC, scol = pcall(function() return sp.Color end)
        if okSC and scol then
          local okv,v
          okv,v = pcall(function() return scol.R end); if okv and v then sr=clampByte(v) end
          okv,v = pcall(function() return scol.G end); if okv and v then sg=clampByte(v) end
          okv,v = pcall(function() return scol.B end); if okv and v then sb=clampByte(v) end
          okv,v = pcall(function() return scol.A end); if okv and v then sa=clampByte(v) end
          sro,sgo,sbo = readOffset(scol)
        end
      end
      -- Prefer whichever colour carries the effect (larger deviation from neutral).
      local function devT(x1,x2,x3) return math.abs(x1-255)+math.abs(x2-255)+math.abs(x3-255) end
      local function devO(x1,x2,x3) return math.abs(x1-128)+math.abs(x2-128)+math.abs(x3-128) end
      local r,g,b,a = er,eg,eb,ea
      local ro,go,bo = ero,ego,ebo
      if devT(sr,sg,sb) > devT(er,eg,eb) then r,g,b = sr,sg,sb end
      if devO(sro,sgo,sbo) > devO(ero,ego,ebo) then ro,go,bo = sro,sgo,sbo end

      -- Colorize (homing/tinted shots, e.g. purple lasers + their impact bits). Read
      -- from the entity colour; fall back to the sprite colour. App applies it via the
      -- existing colorize path (ColR/G/B/A). 0 amount = no colorize (normal case).
      local ccr,ccg,ccb,cca = readColorize(col)

      -- CREEP DIAGNOSTIC: one line per variant. "Creep (Green)" (1000.23) never appeared,
      -- and every plausible cause -- dropped by a filter, or drawn black because its RED
      -- BloodPool sheet is being multiplied by a green tint -- looks identical on screen.
      -- These are the numbers that tell the two apart.
      if creepBit and not creepLogged[flatVa] then
        creepLogged[flatVa] = true
        local vis = "?"; local okCv, cv = pcall(function() return e.Visible end); if okCv then vis = tostring(cv) end
        Isaac.DebugString(string.format(
          "%s: CREEP 1000.%d.%d vis=%s anm2='%s' anim='%s' tint=(%d,%d,%d,%d) off=(%d,%d,%d) colorize=(%d,%d,%d,%d) layers=[%s]",
          MOD_NAME, flatVa, flatSu, vis, anm2, anim, r, g, b, a, ro, go, bo, ccr, ccg, ccb, cca, layerDump(sp)))
      end
      -- Mother's telegraph (1000.190 "Mother Tracer"). It was invisible in the app and the
      -- causes are the same short list creep worked through, so log the same numbers ONCE
      -- per animation rather than rediscovering them: `anim` says which direction the game
      -- chose (Spot / MoveUp / MoveDown / MoveLeft / MoveRight), `vis` says whether the
      -- liveness exemption above was what it needed, and `layers` says whether the sheet
      -- and alpha are healthy. Keyed on the ANIM, not the variant, because there is only
      -- one variant and the direction is the interesting axis.
      if tracerBit and not tracerLogged[anim or "?"] then
        tracerLogged[anim or "?"] = true
        local vis = "?"; local okTv, tv = pcall(function() return e.Visible end); if okTv then vis = tostring(tv) end
        Isaac.DebugString(string.format(
          "%s: TRACER 1000.%d.%d vis=%s anm2='%s' anim='%s' frame=%d tint=(%d,%d,%d,%d) off=(%d,%d,%d) layers=[%s]",
          MOD_NAME, flatVa, flatSu, vis, anm2, anim, aframe, r, g, b, a, ro, go, bo, layerDump(sp)))
      end
      if cca == 0 and okSp and sp then
        local okSc2, scol2 = pcall(function() return sp.Color end)
        if okSc2 and scol2 then ccr,ccg,ccb,cca = readColorize(scol2) end
      end
      -- Brimstone/laser impacts (wall splash 1000.50 + flying bits 1000.70) carry NO
      -- colour in the file, so a plain RED brimstone leaves them white. A COLOURED shot
      -- (spoon-bender purple, etc) colorizes them and that's handled above (cca>0), so
      -- here we only tint the colourless ones the beam's red. SpawnerType is 0 (none)
      -- for the flying bits, so we key off the variant, not the spawner.
      if cca == 0 and e.Type == 1000 then
        local okVi, vi = pcall(function() return e.Variant end)
        if okVi and (vi == 70 or vi == 50) then
          -- ...but only the RED brimstone-family impacts. Holy beams (Gabriel/Uriel
          -- light beam, Trisagion/shoop) spawn their OWN already-white impact sheets
          -- (1000.050_lightbeamimpact / _shoopimpact) — reddening those is wrong. Gate
          -- on the effect's sprite filename so only red impacts get the red tint.
          local fn = (anm2 or ""):lower()
          local isHoly = fn:find("lightbeam", 1, true) or fn:find("shoop", 1, true)
                      or fn:find("ringimpact", 1, true)
          if not isHoly then ccr,ccg,ccb,cca = 255,0,0,255 end
        end
      end

      -- Player hit: hide costumes + flash the body white for a few frames, as the
      -- game does. Detected by the damage cooldown jumping up on the hit frame.
      local hurtBit = 0
      if cat == C_PLAYER then
        local okPl, plr = pcall(function() return e:ToPlayer() end)
        if okPl and plr then
          local okCd, cd = pcall(function() return plr:GetDamageCooldown() end)
          if okCd and cd then
            if cd > lastPlayerCooldown and cd > 0 then playerHitUntil = frame + 9 end
            lastPlayerCooldown = cd
          end
        end
        if frame <= playerHitUntil then hurtBit = 16; ro,go,bo = 255,255,255 end
      end

      -- Enemy status effects (poison/burn/slow/...). The game draws the little
      -- floating icons as an engine overlay, not as entities, so we detect the
      -- flags here and the app draws an indicator. Packed as a bit field.
      local statusByte = 0
      if cat == C_ENEMY then
        local function hf(flag)
          if flag == nil then return false end
          local ok, v = pcall(function() return e:HasEntityFlags(flag) end)
          return ok and v
        end
        if hf(EntityFlag.FLAG_POISON)    then statusByte = statusByte + 1   end
        if hf(EntityFlag.FLAG_BURN)      then statusByte = statusByte + 2   end
        if hf(EntityFlag.FLAG_SLOW)      then statusByte = statusByte + 4   end
        if hf(EntityFlag.FLAG_FREEZE) or hf(EntityFlag.FLAG_ICE)
                                         then statusByte = statusByte + 8   end
        if hf(EntityFlag.FLAG_FEAR)      then statusByte = statusByte + 16  end
        if hf(EntityFlag.FLAG_CONFUSION) then statusByte = statusByte + 32  end
        if hf(EntityFlag.FLAG_CHARM)     then statusByte = statusByte + 64  end
        if hf(EntityFlag.FLAG_BLEED_OUT) then statusByte = statusByte + 128 end
      end

      -- Champion index (drives the champion body colour below).
      local champIdx = -1
      if cat == C_ENEMY then
        local okN, npc = pcall(function() return e:ToNPC() end)
        if okN and npc then
          local okCi, ci = pcall(function() return npc:GetChampionColorIdx() end)
          if okCi and ci then champIdx = ci end
        end
      end

      -- Champion body colour: rebuilt from the champion index as an RGBA multiply
      -- (the game's own palette recolour isn't readable — see CHAMPION_COLORS). The
      -- app multiplies tint into the sprite, so this recolours the body in place while
      -- keeping its shading. Champion PROJECTILES already carry real tint, so leave
      -- those alone (only enemies get a champion index anyway).
      if champIdx >= 0 then
        local cc = CHAMPION_COLORS[champIdx]
        if cc then
          r = math.floor(r * cc[1] / 255 + 0.5)
          g = math.floor(g * cc[2] / 255 + 0.5)
          b = math.floor(b * cc[3] / 255 + 0.5)
          a = math.floor(a * cc[4] / 255 + 0.5)
        end
      end

      local rot = 0
      local okR, rv = pcall(function() return e.SpriteRotation end); if okR and rv then rot = rv end

      -- Skip one-shot effects whose animation has already finished: in-game the
      -- game has stopped drawing them (they're about to be removed), but they can
      -- linger in the entity list for a few frames — otherwise the last puff of
      -- smoke/poof stays frozen in the diorama. EXCEPTION: creep is a persistent
      -- floor decal that reports 'finished' once settled (Headless Baby's red creep
      -- did) — never skip it, or the creep vanishes.
      -- ...but ONLY for the TRANSIENT one-shot puffs this was written for (smoke/poof/puff).
      --
      -- The old rule skipped ANY finished effect, which silently ate every static-frame
      -- PARTICLE in the game: rock bits (1000.4 — its anm2 is the STATIC `grid/grid_rock.anm2`),
      -- poop bits (1000.58), embers (1000.66), brimstone blood drops (1000.70), blood particles
      -- (1000.5)... none of them ever "play" an animation, so IsFinished is true from birth and
      -- they were dropped the moment they spawned. Same trap that hid the held Grid Projectile
      -- Helper — a deliberately-static sprite looks "finished" to every liveness heuristic.
      --
      -- Keeping this narrow is safe because the APP already has a better-targeted freeze-drop for
      -- genuinely stuck poofs (identical frame AND position for 40 packets) using the SAME hint
      -- list — so a real frozen poof is still caught, now without the collateral damage. Any other
      -- finished one-shot simply lingers the few frames until the game removes it, which is what
      -- the game itself is doing anyway. (The old "creep" exception is no longer needed: creep is
      -- not a puff, so it never reaches the IsFinished test now.)
      -- NEVER skip an entity Isaac is HOLDING either.
      local skip = false
      -- Entities we deliberately never draw in the diorama (see HIDE_ENTITIES).
      do
        local okTy2, ty2 = pcall(function() return e.Type end)
        local okVa2, va2 = pcall(function() return e.Variant end)
        local okSu2, su2 = pcall(function() return e.SubType end)
        if okTy2 and okVa2 and okSu2 and isHiddenEntity(ty2, va2, su2) then skip = true end
        -- 1000.140 "Backdrop Decoration" is the entity the game uses to place BACKDROP ART
        -- as an entity — its entities2.xml anm2path is a placeholder (`1000.015_Poof01`)
        -- because the real sprite is Load()ed at runtime, which is why nothing in the xml
        -- ever referenced `07x_corpse_bottomwall.anm2`. In Mother's fight it is the FAKE
        -- WALL that makes phase 1's arena look smaller than it is; the game removes it
        -- itself when the arena expands for phase 2. Flat on a 2D screen it reads as wall,
        -- but as an upright billboard at the diorama's near edge it is a slab standing
        -- between the viewer and the room.
        --
        -- Scoped to her fight ON PURPOSE: "Backdrop Decoration" is a GENERAL-PURPOSE
        -- entity (Downpour pipes, Mines details, ...), so a blanket hide would strip
        -- legitimate scenery from other stages.
        if okTy2 and okVa2 and ty2 == 1000 and va2 == 140 and motherHere then
          skip = true
          if not motherWallLogged then
            motherWallLogged = true
            Isaac.DebugString(MOD_NAME..": MOTHERWALL hiding 1000.140 (fake arena wall) during Mother")
          end
        end
      end
      if not skip and cat == C_EFFECT and okSp and sp and heldBit == 0 and anm2 ~= "" then
        local al = anm2:lower()
        local transient = al:find("smoke", 1, true) or al:find("poof", 1, true)
                       or al:find("puff", 1, true)
        if transient then
          local okFin, fin = pcall(function() return sp:IsFinished(anim) end)
          if okFin and fin then skip = true end
        end
      end

      if not skip and not isLaser then
        local anm2Id = intern(anm2)
        local animId = intern(anim)
        if aframe < 0 then aframe = 0 elseif aframe > 65535 then aframe = 65535 end

        n = n + 1
        recs[n] = string.pack("<ffBBffHHHBBBBf",
          pos.X, pos.Y, cat, flip + flat, scl, entHeight(e),
          anm2Id, animId, aframe, r,g,b,a, rot)

        -- PLAYER costume resolution (REPENTOGON GetCostumeLayerMap): computed BEFORE the
        -- base-body pack because it drives two things — (a) hiding base-body layers that a
        -- BODY costume replaces (so Isaac's own head doesn't peek out behind Brimstone),
        -- and (b) the per-costume visible-layer whitelist used further below. The map is
        -- the game's resolved arbitration, so it also covers special deactivations
        -- (Blood Rage) that priority/slot guessing misses.
        -- Held-item-above-head (pickup pose). REPENTOGON GetHeldSprite() gives the
        -- pickup sprite (collectible/trinket) playing PlayerPickupSparkle — whose anm2
        -- already carries the Effect_023_StarFlash sparkle as layer 1, so rendering it
        -- through the normal sprite path draws item + sparkle together. While the pose
        -- plays the game hides Isaac's other item costumes, so we skip those.
        local heldActive, heldF, heldA, heldFr, heldOa, heldOf, heldSp = false, "", "", 0, "", 0, nil
        -- The item-pickup POSE (base anim "Pickup"/"PickupWalkX") lasts longer than the held
        -- sprite's sparkle. The game reverts ALL costumes to default Isaac for the whole pose, so
        -- drive the costume-skip + base force-show off the pose (not the held-sprite timing) —
        -- otherwise, once the sparkle ends, a head costume re-hides Isaac's base head mid-pose.
        local pickupPose = (cat == C_PLAYER) and (anim:sub(1, 6) == "Pickup")
        -- Every PLAYER animation the game plays with costumes suppressed: the pill reaction
        -- ("Happy"/"Sad"), plus the transitions and deaths in BARE_ANIMS. Same treatment as
        -- the pickup pose above.
        local reactionPose = (cat == C_PLAYER) and BARE_ANIMS[anim] == true
        -- Poses where the game shows PLAIN default Isaac with no costumes at all.
        local barePose = pickupPose or reactionPose
        local pcHave, pcVisible, pcBaseHide, pcDescs = false, {}, {}, nil
        local pcFrontSlot = {}   -- [costumeIndex]=true if it occupies a HEAD/top slot (>=4) → keep in front
        if HAS_RGON and cat == C_PLAYER then
          local okP, plr = pcall(function() return e:ToPlayer() end)
          if okP and plr then
            local okHs, hsp = pcall(function() return plr:GetHeldSprite() end)
            if okHs and hsp then
              local o, v
              o, v = pcall(function() return hsp:GetFilename() end);  if o and v then heldF = v end
              o, v = pcall(function() return hsp:GetAnimation() end); if o and v then heldA = v end
              local playing = false
              if heldA ~= "" then
                local okp, pv = pcall(function() return hsp:IsPlaying(heldA) end); if okp then playing = pv end
              end
              if heldF ~= "" and heldA ~= "" and playing then
                heldActive = true
                heldSp = hsp
                o, v = pcall(function() return hsp:GetFrame() end);            if o and v then heldFr = v end
                o, v = pcall(function() return hsp:GetOverlayAnimation() end); if o and v then heldOa = v end
                o, v = pcall(function() return hsp:GetOverlayFrame() end);     if o and v then heldOf = v end
              end
            end
            local okD, dd = pcall(function() return plr:GetCostumeSpriteDescs() end); if okD then pcDescs = dd end
            local okM, map = pcall(function() return plr:GetCostumeLayerMap() end)
            if okM and map then
              pcHave = true
              for mi = 1, #map do
                local md = map[mi]
                local ci0, lid
                local okci, cv = pcall(function() return md.costumeIndex end); if okci and type(cv)=="number" then ci0 = cv end
                local okli, lv = pcall(function() return md.layerID end);      if okli and type(lv)=="number" then lid = lv end
                if ci0 and ci0 >= 0 and lid then
                  local set = pcVisible[ci0]; if set == nil then set = {}; pcVisible[ci0] = set end
                  set[lid] = true
                  -- Player SLOT this costume-layer occupies = mi-1. Head/top slots are >=4,
                  -- body/glow slots are <=3. A costume touching a head/top slot must stay in
                  -- front of the head; one that touches ONLY body slots (Cancer hand, Jupiter)
                  -- should sit BEHIND the head — flagged for the app via the "behind" order.
                  if (mi - 1) >= 4 then pcFrontSlot[ci0] = true end
                  -- ANY costume occupying this player layer replaces the base body's layer
                  -- there, so hide the base layer (its id equals the PlayerSpriteLayer mi-1).
                  -- NOTE: not gated on isBodyLayer — the head slot reports isBodyLayer=false
                  -- yet still replaces Isaac's base head (Brimstone), so gating on it left
                  -- the original head visible behind the costume.
                  pcBaseHide[mi - 1] = true
                end
              end
            end
          end
        end

        if HAS_RGON and okSp and sp then
          -- During a BARE pose (item pickup, or the Happy/Sad pill reaction), show the FULL
          -- base body (plain default Isaac): don't hide the layers costumes would replace AND
          -- force-show them, since a head costume hides Isaac's base head at runtime and would
          -- otherwise leave him headless. Driven by the POSE (whole animation), not heldActive
          -- (only the shorter sparkle window).
          -- NOTE: use an explicit branch, NOT `barePose and nil or pcBaseHide` — that Lua
          -- idiom ALWAYS yields pcBaseHide (X and nil -> nil, nil or Y -> Y), which is exactly
          -- the bug that kept the head hidden.
          local baseHide = pcBaseHide
          if barePose then baseHide = nil end
          local blob, lc = packLayerOverrides(sp, subLayers, baseHide, barePose)
          n3 = n3 + 1
          if ovlFrame < 0 then ovlFrame = 0 elseif ovlFrame > 65535 then ovlFrame = 65535 end
          recs3[n3] = string.pack("<ffBBffHHHBBBBfHHBBBBBBBBHB",
            pos.X, dropY or pos.Y, cat, flip + math.max(hurtBit, dogmaHurt) + flat + heldBit + dogmaBit, scl, entHeight(e) + dropH,
            anm2Id, animId, aframe, r,g,b,a, rot,
            intern(ovlAnim), ovlFrame, ro,go,bo, ccr,ccg,ccb,cca, statusByte, shadowU16, lc) .. blob

          -- Held item above the head (pickup pose): emit the held sprite as a
          -- costume-flagged entry (bit3=8) so the app draws it player-attached, in
          -- front, and with no shadow — its anm2's own frame offsets float it above
          -- the head, exactly like the game. The sparkle rides along as layer 1.
          if cat == C_PLAYER and heldActive and heldSp then
            if heldFr < 0 then heldFr = 0 elseif heldFr > 65535 then heldFr = 65535 end
            if heldOf < 0 then heldOf = 0 elseif heldOf > 65535 then heldOf = 65535 end
            -- Layer overrides carry the RUNTIME spritesheets (the actual trinket /
            -- collectible sheet + the starflash sheet), otherwise the app would reload
            -- the anm2's baked default (Fish Head / Sad Onion). eflags: 8=costume
            -- (drawn in front, no shadow) + 32=held (app lifts it above the head).
            local hblob, hlc = packLayerOverrides(heldSp)
            n3 = n3 + 1
            recs3[n3] = string.pack("<ffBBffHHHBBBBfHHBBBBBBBBHB",
              pos.X, pos.Y, C_PLAYER, flip + 8 + 32, scl, entHeight(e),
              intern(heldF), intern(heldA), heldFr, 255,255,255,255, rot,
              intern(heldOa), heldOf, 128,128,128, 0,0,0,0, 0, 0, hlc) .. hblob
          end

          -- Costumes are separate sprites layered over the player — but NOT during a bare
          -- pose: the item-pickup pose (showing just Isaac + the item) or the Happy/Sad pill
          -- reaction. The game hides every cosmetic for both.
          if cat == C_PLAYER and pcDescs and not barePose then
            do
              local descs = pcDescs
              do
                local visibleByCostume, haveMap = pcVisible, pcHave
                for ci = 1, #descs do
                  if n3 >= MAX_ENTITIES then break end
                  local okCs, csp = pcall(function() return descs[ci]:GetSprite() end)
                  if okCs and csp then
                    local ca, cn, cf, coa, cof = "", "", 0, "", 0
                    local o2, v2
                    o2, v2 = pcall(function() return csp:GetFilename() end);         if o2 and v2 then ca = v2 end
                    o2, v2 = pcall(function() return csp:GetAnimation() end);        if o2 and v2 then cn = v2 end
                    o2, v2 = pcall(function() return csp:GetFrame() end);            if o2 and v2 then cf = v2 end
                    o2, v2 = pcall(function() return csp:GetOverlayAnimation() end); if o2 and v2 then coa = v2 end
                    o2, v2 = pcall(function() return csp:GetOverlayFrame() end);     if o2 and v2 then cof = v2 end
                    -- Visible-layer whitelist for THIS costume (0-based index = ci-1).
                    local showIds = haveMap and visibleByCostume[ci - 1] or nil
                    local drawCostume = (not haveMap) or (showIds ~= nil)   -- skip if map says no layers
                    if ca ~= "" and drawCostume then
                      local cblob, clc = packLayerOverrides(csp, showIds)
                      if cf < 0 then cf = 0 elseif cf > 65535 then cf = 65535 end
                      if cof < 0 then cof = 0 elseif cof > 65535 then cof = 65535 end
                      -- eflags bit7 (64): body-only costume (no head/top slot) → app draws it
                      -- BEHIND the head instead of in front (fixes Cancer hand / Jupiter body).
                      local behindHead = (not pcFrontSlot[ci - 1]) and 64 or 0
                      n3 = n3 + 1
                      recs3[n3] = string.pack("<ffBBffHHHBBBBfHHBBBBBBBBHB",
                        pos.X, pos.Y, C_PLAYER, flip + 8 + hurtBit + behindHead, scl, entHeight(e),
                        intern(ca), intern(cn), cf, 255,255,255,255, rot,
                        intern(coa), cof, ro,go,bo, 0,0,0,0, 0, 0, clc) .. cblob
                    end
                  end
                end
              end
            end
          end
        end
      end
    end
  end

  -- grid obstacles (sprite too)
  local gsize = room:GetGridSize()
  for i = 0, gsize-1 do
    if n >= MAX_ENTITIES then break end
    local gd = room:GetGridEntity(i)
    local gdType = gd and gd:GetType() or nil
    if gdType and not GRID_DRAW[gdType] and not gridSkipLogged[gdType] then
      gridSkipLogged[gdType] = true
      Isaac.DebugString(string.format("%s: GRIDSKIP type=%d (not in GRID_DRAW)", MOD_NAME, gdType))
    end
    if gd and GRID_DRAW[gdType] then
      local gtype = gd:GetType()
      local gflags = GRID_FLAT[gtype] and 2 or 0   -- bit1 = lies flat on floor
      if GRID_BREAKABLE[gtype] then
        local okSt, st = pcall(function() return gd.State end)
        if okSt and st and st > 1 then gflags = gflags + 4 end   -- bit2 = destroyed
      end
      local p = room:GetGridPosition(i)
      local anm2, anim, aframe = "", "", 0
      local okSp, sp = pcall(function() return gd:GetSprite() end)
      if okSp and sp then
        local okA,v
        okA,v = pcall(function() return sp:GetFilename() end);  if okA and v then anm2=v end
        okA,v = pcall(function() return sp:GetAnimation() end); if okA and v then anim=v end
        okA,v = pcall(function() return sp:GetFrame() end);     if okA and v then aframe=v end
      end
      aframe = clampFrame(aframe)
      n = n + 1
      recs[n] = string.pack("<ffBBffHHHBBBBf",
        p.X, p.Y, C_GRID, gflags, 1.0, 0.0,
        intern(anm2), intern(anim), aframe, 255,255,255,255, 0.0)

      if HAS_RGON and okSp and sp then
        local blob, lc = packLayerOverrides(sp)
        n3 = n3 + 1
        recs3[n3] = string.pack("<ffBBffHHHBBBBfHHBBBBBBBBHB",
          p.X, p.Y, C_GRID, gflags, 1.0, 0.0,
          intern(anm2), intern(anim), aframe, 255,255,255,255, 0.0,
          intern(""), 0, 128,128,128, 0,0,0,0, 0, 0, lc) .. blob
      end
    end
  end

  -- Doors (slots 0-7): export as sprites so the app can draw them on the walls.
  for slot = 0, 7 do
    if n >= MAX_ENTITIES then break end
    local okD, door = pcall(function() return room:GetDoor(slot) end)
    if okD and door then
      local anm2, anim, aframe = "", "", 0
      local okSp, sp = pcall(function() return door:GetSprite() end)
      if okSp and sp then
        local okA, v
        okA, v = pcall(function() return sp:GetFilename() end);  if okA and v then anm2 = v end
        okA, v = pcall(function() return sp:GetAnimation() end); if okA and v then anim = v end
        okA, v = pcall(function() return sp:GetFrame() end);     if okA and v then aframe = v end
      end
      aframe = clampFrame(aframe)
      local p = door.Position
      n = n + 1
      recs[n] = string.pack("<ffBBffHHHBBBBf",
        p.X, p.Y, C_DOOR, 0, 1.0, 0.0,
        intern(anm2), intern(anim), aframe, 255,255,255,255, 0.0)

      if HAS_RGON and okSp and sp then
        local blob, lc = packLayerOverrides(sp)
        n3 = n3 + 1
        recs3[n3] = string.pack("<ffBBffHHHBBBBfHHBBBBBBBBHB",
          p.X, p.Y, C_DOOR, 0, 1.0, 0.0,
          intern(anm2), intern(anim), aframe, 255,255,255,255, 0.0,
          intern(""), 0, 128,128,128, 0,0,0,0, 0, 0, lc) .. blob
      end
    end
  end

  -- send any new strings first, then the scene
  flushPending()
  if HAS_RGON and n3 > 0 then
    local h3 = string.pack("<BBBBI4ffffH", 0x49, PROTO_VERSION, MSG_SCENE3, 0,
                           frame, tl.X, tl.Y, br.X, br.Y, n3)
    pcall(function() udp:send(h3 .. table.concat(recs3)) end)
  else
    local header = string.pack("<BBBBI4ffffH", 0x49, PROTO_VERSION, MSG_SCENE2, 0,
                               frame, tl.X, tl.Y, br.X, br.Y, n)
    pcall(function() udp:send(header .. table.concat(recs)) end)
  end

  -- Shop / devil-deal prices: send every priced pickup's position + price. Positive
  -- price = coin cost; negative = PickupPrice enum (hearts / soul hearts / spikes).
  do
    local priceParts, np = {}, 0
    for i = 1, #ents do
      local e = ents[i]
      if e then
        local okPk, pk = pcall(function() return e:ToPickup() end)
        if okPk and pk then
          local okPr, price = pcall(function() return pk.Price end)
          if okPr and price and price ~= 0 and np < 64 then
            np = np + 1
            local pp = e.Position
            priceParts[np] = string.pack("<ffh", pp.X, pp.Y, price)
          end
        end
      end
    end
    -- Always send (even 0) so the app clears prices when leaving a shop.
    local hp = string.pack("<BBBBH", 0x49, PROTO_VERSION, MSG_PRICES, 0, np)
    pcall(function() udp:send(hp .. table.concat(priceParts)) end)
  end

  -- Lasers (brimstone / technology / tech-x / etc). Each is exported as its real
  -- SAMPLE POLYLINE in world coords (2 pts = straight, many = curvy/homing) plus
  -- variant, colour, sprite-scale, collision radius and a circle flag. Circle
  -- lasers (Tech X, tractor ring) carry no polyline -- the app draws a ring from
  -- centre (cx,cy) + radius. Sent every frame (even 0) so the app clears old beams.
  do
    local laserParts, nl = {}, 0
    for i = 1, #ents do
      if nl >= 48 then break end
      local e = ents[i]
      local okT, t = pcall(function() return e.Type end)
      if e and okT and t == EntityType.ENTITY_LASER then
        local okL, laser = pcall(function() return e:ToLaser() end)
        local dogmaLaser = laserIsDogma(e, dogmaHere)
        if okL and laser and not laserHidden(e, dogmaHere) then
          local var = 0
          local okV, vv = pcall(function() return e.Variant end); if okV and type(vv)=="number" then var = vv % 256 end
          -- Byte 2 is a FLAGS byte: bit0 = circle laser, bit1 = fired by Dogma.
          -- Dogma's beam is drawn by the game's own coloroffset_dogma shader (white TV
          -- static), not the red brimstone art, so the app needs to know to skip the
          -- variant's red tint.
          local isCirc = 0
          local okC, cc = pcall(function() return laser:IsCircleLaser() end); if okC and cc then isCirc = 1 end
          if dogmaLaser then
            isCirc = isCirc + 2
            -- One line per (variant, circle) pair so we can split Dogma's beams by
            -- variant later instead of styling them all the same.
            local dk = var * 2 + (isCirc % 2)
            if not dogmaLaserSeen[dk] then
              dogmaLaserSeen[dk] = true
              local okVi, vi = pcall(function() return e.Visible end)
              Isaac.DebugString(string.format("%s: DOGMA LASER var=%d circle=%d visible=%s",
                MOD_NAME, var, isCirc % 2, tostring(okVi and vi)))
            end
          end
          local rad = 0.0
          local okR, rr = pcall(function() return laser.Radius end); if okR and type(rr)=="number" then rad = rr end
          local scl = 1.0
          local okS, sv = pcall(function() return e.SpriteScale.X end); if okS and sv then scl = sv end
          -- Colour: read entity AND sprite colour, keep whichever deviates from white
          -- MORE (synergy colours -- e.g. homing purple -- can live on either).
          local pr,pg,pb,pa = 255,255,255,255
          local okCol, col = pcall(function() return e.Color end)
          if okCol and col then pr=clampByte(col.R); pg=clampByte(col.G); pb=clampByte(col.B); pa=clampByte(col.A) end
          local sr,sg,sb = pr,pg,pb
          local okSp, sp = pcall(function() return e:GetSprite() end)
          if okSp and sp then
            local okSC, scol = pcall(function() return sp.Color end)
            if okSC and scol then sr=clampByte(scol.R); sg=clampByte(scol.G); sb=clampByte(scol.B) end
          end
          local function devW(a,b,c) return math.abs(a-255)+math.abs(b-255)+math.abs(c-255) end
          if devW(sr,sg,sb) > devW(pr,pg,pb) then pr,pg,pb = sr,sg,sb end
          -- Colorize (the purple homing laser lives here, NOT in tint/offset).
          local lcr,lcg,lcb,lca = readColorize(col)
          if lca == 0 and okSp and sp then
            local okSc3, scol3 = pcall(function() return sp.Color end)
            if okSc3 and scol3 then lcr,lcg,lcb,lca = readColorize(scol3) end
          end
          local pos = e.Position
          -- Sample polyline for ALL lasers now (incl. circle) so Tech X can animate.
          local pts, npt = {}, 0
          local sminx, sminy, smaxx, smaxy = 1e9, 1e9, -1e9, -1e9
          local okSm, samples = pcall(function() return laser:GetSamples() end)
          if okSm and samples then
            local sz
            local okz, z = pcall(function() return samples:Size() end); if okz and type(z)=="number" then sz = z end
            if sz == nil then local okh, h = pcall(function() return #samples end); if okh and type(h)=="number" then sz = h end end
            if sz and sz > 0 then
              if sz > 64 then sz = 64 end
              for k = 0, sz-1 do
                local okg, p = pcall(function() return samples:Get(k) end)
                if not (okg and p) then okg, p = pcall(function() return samples[k] end) end
                if okg and p then
                  local okx, x = pcall(function() return p.X end)
                  local oky, y = pcall(function() return p.Y end)
                  if okx and oky and type(x)=="number" and type(y)=="number" then
                    npt = npt + 1; pts[npt] = string.pack("<ff", x, y)
                    if x < sminx then sminx = x end; if x > smaxx then smaxx = x end
                    if y < sminy then sminy = y end; if y > smaxy then smaxy = y end
                  end
                end
              end
            end
          end
          -- Enemy-fired brimstone (The Haunt, Uriel/Gabriel, etc.) returns an EMPTY
          -- GetSamples() list, so the app had nothing to draw and dropped the beam.
          -- We still have the origin (Position) and Angle, so synthesise a straight
          -- 2-point polyline: start at the origin, cast along the angle, and clamp the
          -- far end to the room rectangle (tl..br) with a ray/AABB intersection. This
          -- flows through the same flat ribbon path as player beams (fixes horizontal
          -- rendering too). Only when there's no real polyline and it isn't a circle.
          if npt < 2 and (isCirc % 2) == 0 then   -- bit0 only: isCirc is a FLAGS byte now
            local okAng, ang = pcall(function() return laser.Angle end)
            if okAng and type(ang) == "number" then
              local rad = math.rad(ang)
              local dx, dy = math.cos(rad), math.sin(rad)
              local sx, sy = pos.X, pos.Y
              -- Ray vs the room AABB: smallest positive t that reaches an edge.
              local tBest = 1e9
              if dx > 1e-6 then tBest = math.min(tBest, (br.X - sx) / dx)
              elseif dx < -1e-6 then tBest = math.min(tBest, (tl.X - sx) / dx) end
              if dy > 1e-6 then tBest = math.min(tBest, (br.Y - sy) / dy)
              elseif dy < -1e-6 then tBest = math.min(tBest, (tl.Y - sy) / dy) end
              if tBest < 0 or tBest > 1e8 then tBest = 240 end   -- fallback length
              local ex, ey = sx + dx * tBest, sy + dy * tBest
              pts = { string.pack("<ff", sx, sy), string.pack("<ff", ex, ey) }
              npt = 2
            end
          end
          nl = nl + 1
          laserParts[nl] = string.pack("<BBBBBBBBBBffffH",
            var, isCirc, pr, pg, pb, pa, lcr, lcg, lcb, lca,
            scl, rad, pos.X, pos.Y, npt) .. table.concat(pts)
        end
      end
    end
    local hl = string.pack("<BBBBH", 0x49, PROTO_VERSION, MSG_LASERS, 0, nl)
    pcall(function() udp:send(hl .. table.concat(laserParts)) end)
  end

  -- HUD state (throttled; doesn't need per-frame updates).
  if (frame % HUD_REFRESH) == 0 then sendHud() end
  -- Water: cheap, and it changes live in the flooding rooms.
  if (frame % 5) == 0 then sendWater(frame) end
  if (frame % STATS_REFRESH) == 0 then sendStats() end

  -- Backdrop: send on change (type/shape/bounds). App maps type -> atlas png.
  local okB, bt = pcall(function() return room:GetBackdropType() end)
  local okS, rs = pcall(function() return room:GetRoomShape() end)
  local okRt, rtype = pcall(function() return room:GetType() end)
  local roomType = (okRt and rtype) and (rtype % 256) or 0
  if okB and okS then
    -- Room IDENTITY, not just appearance. The Dogma fight swaps the bedroom for
    -- "Home Default 1000 Dogma Test" at the SAME grid index, SAME shape, SAME
    -- bounds and (often) the same backdrop enum, so a signature built only from
    -- type/shape/bounds never changes and the wall is never recaptured -- the app
    -- keeps drawing the old bedroom walls. Fold in the level's current room index
    -- and the room's own spawn/decoration seed (unique per room instance, and it
    -- changes when a room is replaced in place) so any real room swap is caught.
    local function sv(fn, d) local ok, v = pcall(fn); if ok and type(v) == "number" then return v end; return d end
    local sigIdx  = sv(function() return Game():GetLevel():GetCurrentRoomIndex() end, -1)
    local sigSeed = sv(function() return room:GetSpawnSeed() end, 0)
    local sigDec  = sv(function() return room:GetDecorationSeed() end, 0)
    -- The room's LAYOUT variant (its row in the stage's room XML) is the strongest
    -- identity we can read: two different rooms at the same index cannot share it.
    local sigVar  = sv(function()
      local d = Game():GetLevel():GetCurrentRoomDesc().Data
      return d and d.Variant or -1
    end, -1)
    local sig = string.format("%d_%d_%d_%d_%d_%d_%d_%d_%d_%d", bt, rs,
      math.floor(tl.X), math.floor(tl.Y), math.floor(br.X), math.floor(br.Y),
      math.floor(sigIdx), math.floor(sigSeed) % 1000000, math.floor(sigDec) % 1000000,
      math.floor(sigVar))
    if sig ~= lastBackdropSig or roomChanged or (frame % 60) == 0 then
      -- MC_POST_NEW_ROOM is authoritative; the signature is the belt-and-braces.
      local firstTimeThisRoom = (sig ~= lastBackdropSig) or roomChanged
      if firstTimeThisRoom then
        Isaac.DebugString(string.format("%s: ROOMCHANGE via=%s idx=%d var=%d bt=%d shape=%d",
          MOD_NAME, roomChanged and "callback" or "signature",
          math.floor(sigIdx), math.floor(sigVar), bt, rs))
      end
      roomChanged = false
      lastBackdropSig = sig

      -- PROBE (once per room): confirm the assembled floor/wall Image API + format,
      -- so we can build pixel-perfect (bloody) backdrops from the game's own buffers.
      if firstTimeThisRoom and HAS_RGON then
        local okBd, bd = pcall(function() return room:GetBackdrop() end)
        if okBd and bd then
          for _, which in ipairs({ {"FLOOR","GetFloorImage"}, {"WALL","GetWallImage"} }) do
            local okImg, img = pcall(function() return bd[which[2]](bd) end)
            if okImg and img then
              local w = (pcall(function() return img:GetWidth() end)) and img:GetWidth() or -1
              local h = (pcall(function() return img:GetHeight() end)) and img:GetHeight() or -1
              local pw = (pcall(function() return img:GetPaddedWidth() end)) and img:GetPaddedWidth() or -1
              local sample
              if w > 0 and h > 0 then
                sample = scanImage(img, w, h, which[1] == "FLOOR")
              else
                sample = "bad-dims"
              end
              Isaac.DebugString(string.format("%s: IMGPROBE %s w=%d h=%d paddedW=%d %s",
                MOD_NAME, which[1], w, h, pw, sample))
            else
              Isaac.DebugString(MOD_NAME..": IMGPROBE "..which[1].." image is nil")
            end
          end
        else
          Isaac.DebugString(MOD_NAME..": IMGPROBE GetBackdrop() unavailable")
        end
      end

      -- On room change: capture the wall once (it won't change mid-room) and do an
      -- immediate full floor capture so the new room's floor shows instantly, then
      -- hand ongoing floor updates to the amortized floorTick pipeline (below).
      if firstTimeThisRoom then
        updateWallImage(room, frame)
        maybeUpdateImage(room, frame, true, 0, "GetFloorImage")   -- one-time instant floor
        floorPipe = { phase = "capture", y = 0, rows = {}, unchanged = 0 }
      end

      -- backdrops.xml is in enum order; try 1-based then 0-based index.
      -- BackdropType IS the xml id, so look it up directly. The old
      -- `BACKDROPS[bt] or BACKDROPS[bt+1]` fallback was there to paper over the
      -- append-ordering above; with the table id-keyed a miss now means the id genuinely
      -- has no row, and guessing a neighbour would just send the wrong stage silently.
      local def = BACKDROPS[bt] or {name="",gfx="",nfloor="",door=""}
      if not bdLogged[bt] then
        bdLogged[bt] = true
        Isaac.DebugString(string.format("%s: BACKDROP type=%d -> gfx='%s' nfloor='%s' name='%s'",
                                        MOD_NAME, bt, def.gfx, def.nfloor, def.name))
      end
      local function pstr(x) x = x or ""; return string.pack("<H", #x) .. x end
      local bpkt = string.pack("<BBBBHBH", 0x49, PROTO_VERSION, MSG_BACKDROP, roomType, bt, rs, BACKDROP_N)
                 .. pstr(def.gfx) .. pstr(def.nfloor) .. pstr(def.name)
                 .. string.pack("<ffff", tl.X, tl.Y, br.X, br.Y)
      pcall(function() udp:send(bpkt) end)
    end
  end

  -- Amortized floor capture/send: one small step per frame. This continuously
  -- re-captures the WHOLE floor (catching all blood/gut/liquid changes) and sends
  -- it, with the cost spread across frames so there is no ~1s hitch. Runs every
  -- frame; the state machine itself paces the work.
  if HAS_RGON then floorTick(room) end

  -- Resend the WALL a couple of times to heal any dropped chunks (the floor heals
  -- itself via floorTick's periodic re-send, so it's excluded here to avoid a
  -- full-image burst that would reintroduce the hitch).
  if imgResendAt then
    local still = {}
    for i = 1, #imgResendAt do
      if frame >= imgResendAt[i] then
        local c = imgCur[1]                    -- wall only
        if c then sendImage(c.kind, c.ver, c.w, c.h, c.buf) end
      else
        still[#still + 1] = imgResendAt[i]
      end
    end
    imgResendAt = (#still > 0) and still or nil
  end

  frame = frame + 1
  if frame % STRING_REFRESH_FRAMES == 0 then refreshAllStrings() end
  if frame >= 0xFFFFFFFF then frame = 0 end
end)

Isaac.DebugString(MOD_NAME..": loaded (Phase 2b).")
