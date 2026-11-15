-- EdgeLink: publishes out-of-combat UI data (bags, equipment, quests, location)
-- as a strip of colored pixels in the top-left corner of the game window.
-- The EdgeLink companion app reads the strip and serves it to Xeneon Edge widgets.
--
-- Guardrails (see project notes):
--   * No combat data. Snapshots are not rebuilt while in combat.
--   * Read-only. This addon sends nothing anywhere and performs no actions.

local ADDON_NAME, ns = ...
ns = ns or {}
_G.EdgeLink = ns

---------------------------------------------------------------------------
-- Protocol (must match the companion's decoder)
---------------------------------------------------------------------------
-- v2 page layout (480 bytes = 160 blocks x RGB):
--   0-1  magic E7 1D        2 version (2)       3 snapshot id
--   4    page index         5 page count        6-7 payload length (BE)
--   8-9  Fletcher-16 over bytes 10..16 and the payload
--   10-11 uiMapID (BE)      12-13 x * 65535     14-15 y * 65535
--   16   facing (0-254 = 0..2pi, 255 = unknown)
--   17   flags: 1 = position valid, 2 = in combat (position frozen)
--   18.. payload
-- Position rides in every page header so the map dot moves ~10x per second
-- without waiting for a full snapshot.
local P = {
    MAGIC1 = 0xE7,
    MAGIC2 = 0x1D,
    VERSION = 2,
    BLOCKS = 160,          -- blocks per page (one row)
    BLOCK_PX = 4,          -- block edge in physical pixels
    HEADER = 18,           -- header bytes
}
P.PAGE_BYTES = P.BLOCKS * 3
P.PAYLOAD_MAX = P.PAGE_BYTES - P.HEADER
ns.Protocol = P

local floor, char, byte, concat = math.floor, string.char, string.byte, table.concat

-- Fletcher-16 over the bytes s[i..j]
local function Fletcher16(s, i, j)
    local s1, s2 = 0, 0
    for k = i, j do
        s1 = (s1 + byte(s, k)) % 255
        s2 = (s2 + s1) % 255
    end
    return s2 * 256 + s1
end
ns.Fletcher16 = Fletcher16

-- Split a payload string into page-sized chunks.
function ns.SplitPayload(payload)
    local chunks, len = {}, #payload
    local count = math.min(255, math.max(1, math.ceil(len / P.PAYLOAD_MAX)))
    for p = 0, count - 1 do
        chunks[#chunks + 1] = payload:sub(p * P.PAYLOAD_MAX + 1, (p + 1) * P.PAYLOAD_MAX)
    end
    return chunks
end

-- Encode one page as a table of 480 byte values. pos = { mapID, x, y, facing, flags }.
function ns.EncodePage(chunk, snapshotId, pageIndex, pageCount, pos)
    local mapID = math.max(0, math.min(65535, floor(pos.mapID or 0)))
    local x = math.max(0, math.min(65535, floor((pos.x or 0) * 65535 + 0.5)))
    local y = math.max(0, math.min(65535, floor((pos.y or 0) * 65535 + 0.5)))
    local facing = pos.facing and (floor((pos.facing % (2 * math.pi)) / (2 * math.pi) * 255) % 255) or 255
    local b = {
        P.MAGIC1, P.MAGIC2, P.VERSION, snapshotId % 256, pageIndex, pageCount, 0, 0, 0, 0,
        floor(mapID / 256), mapID % 256, floor(x / 256), x % 256, floor(y / 256), y % 256,
        facing, pos.flags or 0,
    }
    local plen = #chunk
    b[7], b[8] = floor(plen / 256), plen % 256
    for k = 1, plen do b[P.HEADER + k] = byte(chunk, k) end
    local s1, s2 = 0, 0
    for k = 11, P.HEADER + plen do
        s1 = (s1 + b[k]) % 255
        s2 = (s2 + s1) % 255
    end
    b[9], b[10] = s2, s1
    for k = P.HEADER + plen + 1, P.PAGE_BYTES do b[k] = 0 end
    return b
end

---------------------------------------------------------------------------
-- Snapshot building (WoW API, defensive across client flavors)
---------------------------------------------------------------------------
-- Field cleaner: tabs/newlines would break the record format.
local function clean(s)
    if s == nil then return "" end
    s = tostring(s)
    s = s:gsub("[\t\n\r]", " ")
    return s
end

-- Text inside a multi-value field must also lose the 0x1F separator.
local function cleanPart(s)
    return (clean(s):gsub("\031", " "))
end

local function NameFromLink(link)
    if not link then return nil end
    return link:match("|h%[(.-)%]|h")
end

local function rec(out, ...)
    local n = select("#", ...)
    local t = {}
    for i = 1, n do
        t[i] = clean((select(i, ...)))
    end
    out[#out + 1] = concat(t, "\t")
end

local function GetContainerSlotInfo(bag, slot)
    if C_Container and C_Container.GetContainerItemInfo then
        local info = C_Container.GetContainerItemInfo(bag, slot)
        if not info then return nil end
        return info.itemID, info.stackCount, info.quality, info.hyperlink, info.iconFileID, info.itemName
    elseif GetContainerItemInfo then
        local texture, count, _, quality, _, _, link, _, _, itemID = GetContainerItemInfo(bag, slot)
        if not texture then return nil end
        return itemID, count, quality, link, texture, nil
    end
end

local function NumSlots(bag)
    if C_Container and C_Container.GetContainerNumSlots then
        return C_Container.GetContainerNumSlots(bag) or 0
    elseif GetContainerNumSlots then
        return GetContainerNumSlots(bag) or 0
    end
    return 0
end

-- Item lookups moved into C_Item on newer clients; use whichever exists.
local ItemInfo = (C_Item and C_Item.GetItemInfo) or GetItemInfo
local ItemInfoInstant = (C_Item and C_Item.GetItemInfoInstant) or GetItemInfoInstant

-- Bags you carry: backpack, the four bag slots, and the reagent bag where the client has one.
function ns.LastCarriedBag()
    return NUM_TOTAL_EQUIPPED_BAG_SLOTS or ((NUM_BAG_SLOTS or 4) + (NUM_REAGENTBAG_SLOTS or 0))
end

local function CollectBags(out)
    local lastBag = ns.LastCarriedBag()
    for bag = 0, lastBag do
        local slots = NumSlots(bag)
        if slots > 0 then
            local free, items = 0, {}
            for slot = 1, slots do
                local itemID, count, quality, link, icon, name = GetContainerSlotInfo(bag, slot)
                if itemID then
                    items[#items + 1] = { slot, itemID, count or 1, quality or -1, name or NameFromLink(link) or "", icon or 0 }
                else
                    free = free + 1
                end
            end
            local bagName = (bag == 0) and "Backpack"
                or (C_Container and C_Container.GetBagName and C_Container.GetBagName(bag))
                or (GetBagName and GetBagName(bag)) or ("Bag " .. bag)
            rec(out, "S", bag, slots, free, bagName)
            for _, it in ipairs(items) do
                local classID, subClassID = "", ""
                if ItemInfoInstant then
                    local r = { pcall(ItemInfoInstant, it[2]) }
                    if r[1] then classID, subClassID = r[7] or "", r[8] or "" end
                end
                -- vendor price (each) and, when Auctionator is installed, its last known auction price (each)
                local sell, ah = "", ""
                if ItemInfo then
                    local r = { pcall(ItemInfo, it[2]) }
                    if r[1] and type(r[12]) == "number" then sell = r[12] end
                end
                local A = Auctionator and Auctionator.API and Auctionator.API.v1
                if A and A.GetAuctionPriceByItemID then
                    local ok, price = pcall(A.GetAuctionPriceByItemID, "EdgeLink", it[2])
                    if ok and type(price) == "number" then ah = price end
                end
                rec(out, "B", bag, it[1], it[2], it[3], it[4], it[5], it[6], classID, subClassID, sell, ah)
            end
        end
    end
end

local function CollectEquipment(out)
    for slot = 1, 19 do
        local link = GetInventoryItemLink("player", slot)
        if link then
            local itemID = GetInventoryItemID and GetInventoryItemID("player", slot) or tonumber(link:match("item:(%d+)")) or 0
            local quality = GetInventoryItemQuality and GetInventoryItemQuality("player", slot) or -1
            local cur, max = GetInventoryItemDurability(slot)
            local icon = GetInventoryItemTexture and GetInventoryItemTexture("player", slot) or 0
            rec(out, "E", slot, itemID, quality or -1, NameFromLink(link) or "", cur or "", max or "", icon or 0)
        end
    end
end

local function JoinObjectives(list)
    local parts = {}
    for _, o in ipairs(list) do
        parts[#parts + 1] = (o.finished and "1" or "0") .. cleanPart(o.text)
    end
    return concat(parts, "\031")
end

local function CollectQuests(out)
    ns.questIDs = {}
    if C_QuestLog and C_QuestLog.GetNumQuestLogEntries and C_QuestLog.GetInfo then
        local n = C_QuestLog.GetNumQuestLogEntries() or 0
        for i = 1, n do
            local info = C_QuestLog.GetInfo(i)
            if info then
                if info.isHeader then
                    rec(out, "G", info.title)
                elseif not info.isHidden then
                    local id = info.questID
                    local complete = (C_QuestLog.IsComplete and C_QuestLog.IsComplete(id)) and 1 or 0
                    if C_QuestLog.IsFailed and C_QuestLog.IsFailed(id) then complete = -1 end
                    local objs = {}
                    local raw = C_QuestLog.GetQuestObjectives and C_QuestLog.GetQuestObjectives(id) or {}
                    for _, o in ipairs(raw) do objs[#objs + 1] = { text = o.text, finished = o.finished } end
                    -- tag: "Elite", "Group", "Dungeon"... as shown beside the quest in the log
                    local tag = ""
                    if C_QuestLog.GetQuestTagInfo then
                        local okT, t = pcall(C_QuestLog.GetQuestTagInfo, id)
                        if okT and type(t) == "table" and t.tagName then tag = t.tagName end
                    elseif GetQuestTagInfo then
                        local okT, _, tagName = pcall(GetQuestTagInfo, id)
                        if okT and tagName then tag = tagName end
                    end
                    if tag == "" and (info.suggestedGroup or 0) > 1 then tag = "Group" end
                    rec(out, "Q", id, info.level or "", complete, info.title, JoinObjectives(objs), tag)
                    ns.questIDs[#ns.questIDs + 1] = id
                end
            end
        end
    elseif GetNumQuestLogEntries and GetQuestLogTitle then
        local n = GetNumQuestLogEntries() or 0
        for i = 1, n do
            local title, level, _, isHeader, _, isComplete, _, questID = GetQuestLogTitle(i)
            if title then
                if isHeader then
                    rec(out, "G", title)
                else
                    local objs = {}
                    local nObj = GetNumQuestLeaderBoards and GetNumQuestLeaderBoards(i) or 0
                    for j = 1, nObj do
                        local text, _, finished = GetQuestLogLeaderBoard(j, i)
                        objs[#objs + 1] = { text = text, finished = finished }
                    end
                    local complete = (isComplete == 1 or isComplete == true) and 1 or ((isComplete == -1) and -1 or 0)
                    rec(out, "Q", questID or 0, level or "", complete, title, JoinObjectives(objs))
                    if questID then ns.questIDs[#ns.questIDs + 1] = questID end
                end
            end
        end
    end
end

-- Map hierarchy: current map first, then each parent up to the world map.
-- Each record carries where the CURRENT map sits on that map (minX, maxX, minY, maxY),
-- so the widget can place the live position (sent in every page header) on all levels.
-- No player coordinates here: moving must not change the snapshot.
--   M  mapID  (unused)  (unused)  name  mapType  minX  maxX  minY  maxY
-- Map size in yards (for route distances), from the world positions of two opposite corners.
function ns.MapYards(mapID)
    if not (C_Map and C_Map.GetWorldPosFromMapPos and CreateVector2D) then return nil end
    local ok1, c1, p1 = pcall(C_Map.GetWorldPosFromMapPos, mapID, CreateVector2D(0, 0))
    local ok2, c2, p2 = pcall(C_Map.GetWorldPosFromMapPos, mapID, CreateVector2D(1, 1))
    if not (ok1 and ok2 and p1 and p2) then return nil end
    local ax, ay = p1:GetXY()
    local bx, by = p2:GetXY()
    -- world X runs north-south and world Y east-west, so map width comes from Y
    local w, h = math.abs(ay - by), math.abs(ax - bx)
    if w < 1 or h < 1 then return nil end
    return string.format("%.0f", w), string.format("%.0f", h)
end

local function CollectMap(out)
    local levels = {}
    if not (C_Map and C_Map.GetBestMapForUnit and C_Map.GetMapInfo) then return levels end
    local current = C_Map.GetBestMapForUnit("player")
    local id, guard = current, 0
    while id and id > 0 and guard < 8 do
        guard = guard + 1
        local info = C_Map.GetMapInfo(id)
        if not info then break end
        local minX, maxX, minY, maxY = 0, 1, 0, 1
        if id ~= current and C_Map.GetMapRectOnMap then
            local ok, a, b, c, d = pcall(C_Map.GetMapRectOnMap, current, id)
            if ok and a then minX, maxX, minY, maxY = a, b, c, d else minX = "" end
        end
        local f = function(v) return v == "" and "" or string.format("%.5f", v) end
        local yw, yh = ns.MapYards(id)
        rec(out, "M", id, yw or "", yh or "", info.name or "", info.mapType or "",
            f(minX), minX == "" and "" or f(maxX), minX == "" and "" or f(minY), minX == "" and "" or f(maxY))
        levels[#levels + 1] = { id = id, mapType = info.mapType or 0 }
        if (info.mapType or 0) <= 1 then break end -- reached world/cosmic
        id = info.parentMapID
    end
    return levels
end

-- Map tree for browsing on the Edge: the world map's continents and each continent's zones, with where each
-- child sits on its parent.   C  parentID  childID  name  mapType  minX maxX minY maxY
-- The tree never changes during a session, so it is built once.
local function CollectTree(out, levels)
    if not ns.mapTree then
        if not (C_Map and C_Map.GetMapChildrenInfo and C_Map.GetMapRectOnMap) then return end
        local world
        for _, l in ipairs(levels or {}) do if l.mapType == 1 then world = l.id end end
        if not world then return end                          -- e.g. inside an instance: try again later
        local lines, n = {}, 0
        local function add(parent, wantType)
            local kids = C_Map.GetMapChildrenInfo(parent, wantType, false) or {}
            table.sort(kids, function(a, b) return a.mapID < b.mapID end)
            local found = {}
            for _, k in ipairs(kids) do
                local ok, a, b, c, d = pcall(C_Map.GetMapRectOnMap, k.mapID, parent)
                if ok and a and b and b > a and d > c and n < 160 then
                    n = n + 1
                    lines[#lines + 1] = { parent, k.mapID, k.name or "", k.mapType or 0,
                        string.format("%.4f", a), string.format("%.4f", b), string.format("%.4f", c), string.format("%.4f", d) }
                    found[#found + 1] = k.mapID
                end
            end
            return found
        end
        for _, cont in ipairs(add(world, 2)) do add(cont, 3) end
        if n == 0 then return end
        ns.mapTree = lines
    end
    for _, l in ipairs(ns.mapTree) do rec(out, "C", l[1], l[2], l[3], l[4], l[5], l[6], l[7], l[8]) end
end

-- Map pins for the zone and continent maps. Every provider is optional and pcall-guarded:
-- which ones exist depends on the client (Retail vs Classic vs WoW Forever).
--   N  mapID  kind  x  y  name  state  faction
--      kind: F flight point (state 0 current, 1 known/reachable, 2 not discovered)
--            D dungeon/raid entrance, A area point of interest, Q quest on map (state = questID)
--   Z  comma list of providers that answered (F,D,A,Q)
local function XY(pos)
    if type(pos) ~= "table" and type(pos) ~= "userdata" then return nil end
    if pos.GetXY then return pos:GetXY() end
    return pos.x, pos.y
end

local function pin(out, mapID, kind, x, y, name, state, faction)
    if not x or not y or x < 0 or x > 1 or y < 0 or y > 1 then return end
    rec(out, "N", mapID, kind, string.format("%.4f", x), string.format("%.4f", y), name or "", state or "", faction or "")
end

local function CollectPins(out, levels)
    local caps = {}
    for _, lvl in ipairs(levels) do
        if lvl.mapType >= 2 then -- continent, zone, dungeon, micro (not world/cosmic)
            local mapID = lvl.id
            if C_TaxiMap and C_TaxiMap.GetTaxiNodesForMap then
                local ok, nodes = pcall(C_TaxiMap.GetTaxiNodesForMap, mapID)
                if ok and type(nodes) == "table" then
                    caps.F = true
                    for _, n in ipairs(nodes) do
                        local x, y = XY(n.position)
                        pin(out, mapID, "F", x, y, n.name, n.state, n.faction)
                    end
                end
            end
            if C_EncounterJournal and C_EncounterJournal.GetDungeonEntrancesForMap then
                local ok, list = pcall(C_EncounterJournal.GetDungeonEntrancesForMap, mapID)
                if ok and type(list) == "table" then
                    caps.D = true
                    for _, d in ipairs(list) do
                        local x, y = XY(d.position)
                        pin(out, mapID, "D", x, y, d.name, "", "")
                    end
                end
            end
            if C_AreaPoiInfo and C_AreaPoiInfo.GetAreaPOIForMap and C_AreaPoiInfo.GetAreaPOIInfo then
                local ok, ids = pcall(C_AreaPoiInfo.GetAreaPOIForMap, mapID)
                if ok and type(ids) == "table" then
                    caps.A = true
                    for _, poiID in ipairs(ids) do
                        local ok2, info = pcall(C_AreaPoiInfo.GetAreaPOIInfo, mapID, poiID)
                        if ok2 and info then
                            local x, y = XY(info.position)
                            pin(out, mapID, "A", x, y, info.name, "", "")
                        end
                    end
                end
            end
            if C_QuestLog and C_QuestLog.GetQuestsOnMap then
                local ok, quests = pcall(C_QuestLog.GetQuestsOnMap, mapID)
                if ok and type(quests) == "table" then
                    caps.Q = true
                    for _, q in ipairs(quests) do
                        pin(out, mapID, "Q", q.x, q.y, "", q.questID, "")
                    end
                end
            end
        end
    end
    local list = {}
    for _, k in ipairs({ "F", "D", "A", "Q" }) do if caps[k] then list[#list + 1] = k end end
    if ns.areaSupport then list[#list + 1] = "R" end
    if ns.inputReady then list[#list + 1] = "I" end
    if TomTom then list[#list + 1] = "T" end
    if Auctionator and Auctionator.API then list[#list + 1] = "U" end
    if C_VignetteInfo and C_VignetteInfo.GetVignettes then list[#list + 1] = "N" end
    if _G.Polestar_NavEngine or (C_AddOns and C_AddOns.IsAddOnLoaded and C_AddOns.IsAddOnLoaded("Polestar")) then list[#list + 1] = "P" end
    if C_Map and C_Map.SetUserWaypoint and UiMapPoint then list[#list + 1] = "W" end
    rec(out, "Z", concat(list, ","))
end

-- Live position for the page header.
---------------------------------------------------------------------------
-- Professions: skill levels.   K  name  rank  maxRank
---------------------------------------------------------------------------
local PROFESSIONS = {
    Herbalism = true, Mining = true, Skinning = true, Alchemy = true, Blacksmithing = true, Enchanting = true,
    Engineering = true, Leatherworking = true, Tailoring = true, Jewelcrafting = true, Inscription = true,
    Cooking = true, ["First Aid"] = true, Fishing = true,
}
-- Skill line IDs, so the Edge can ask the game to open a profession window (C_TradeSkillUI.OpenTradeSkill)
ns.PROF_IDS = { Alchemy = 171, Blacksmithing = 164, Enchanting = 333, Engineering = 202, Herbalism = 182, Inscription = 773,
    Jewelcrafting = 755, Leatherworking = 165, Mining = 186, Skinning = 393, Tailoring = 197, Cooking = 185, ["First Aid"] = 129,
    Fishing = 356 }
local function CollectSkills(out)
    if GetNumSkillLines and GetSkillLineInfo then
        local section
        for i = 1, GetNumSkillLines() or 0 do
            local name, isHeader, _, rank, _, _, maxRank = GetSkillLineInfo(i)
            if isHeader then
                section = name
            elseif name and (PROFESSIONS[name] or (TRADE_SKILLS and section == TRADE_SKILLS) or (SECONDARY_SKILLS and section == SECONDARY_SKILLS)) then
                rec(out, "K", name, rank or 0, maxRank or 0, ns.PROF_IDS[name] or "")
            end
        end
    elseif GetProfessions and GetProfessionInfo then
        local list = { GetProfessions() }
        for i = 1, 5 do
            if list[i] then
                local name, _, rank, maxRank, _, _, skillLine = GetProfessionInfo(list[i])
                if name then rec(out, "K", name, rank or 0, maxRank or 0, skillLine or ns.PROF_IDS[name] or "") end
            end
        end
    end
end

---------------------------------------------------------------------------
-- Gathering nodes, learned from your own gathering and kept between sessions
-- (EdgeLinkDB.nodes[mapID] = { {kind, name, x, y, count}, ... }).
-- kind: H herb, M ore/mining, F fishing spot, T treasure/chest.
--   W  kind  name  "x,y,count;x,y,count;..."   (current zone only)
---------------------------------------------------------------------------
ns.nodeDB = ns.nodeDB or {}
local NODE_RADIUS = { H = 0.01, M = 0.01, T = 0.01, F = 0.02 } -- merge distance in map units
local NODE_CAP = 400                                            -- per map

function ns.RecordGather(db, kind, name, mapID, x, y)
    if not (kind and name and mapID and x and y) then return nil end
    local list = db[mapID]
    if not list then list = {}; db[mapID] = list end
    local r = NODE_RADIUS[kind] or 0.01
    for _, n in ipairs(list) do
        if n[1] == kind and n[2] == name and (n[3] - x) ^ 2 + (n[4] - y) ^ 2 <= r * r then
            n[3] = (n[3] * n[5] + x) / (n[5] + 1)
            n[4] = (n[4] * n[5] + y) / (n[5] + 1)
            n[5] = n[5] + 1
            return n
        end
    end
    if #list >= NODE_CAP then return nil end
    local n = { kind, name, x, y, 1 }
    list[#list + 1] = n
    return n
end

-- Which gathering spell is this? (English client names; ranks share the name.)
function ns.GatherKind(spellName)
    if not spellName then return nil end
    local s = spellName:lower()
    -- Names differ between clients ("Herb Gathering", "Herbalism", ...), so match loosely.
    if s:find("herb", 1, true) then return "H" end
    if s:find("mining", 1, true) then return "M" end
    if s == "opening" or s:find("^opening") or s == "pick lock" then return "T" end
    return nil
end

local function CollectNodes(out, mapID)
    local list = mapID and ns.nodeDB[mapID]
    if not list then return end
    local groups, keys = {}, {}
    for _, n in ipairs(list) do
        local key = n[1] .. "\t" .. n[2]
        if not groups[key] then groups[key] = {}; keys[#keys + 1] = key end
        local g = groups[key]
        g[#g + 1] = string.format("%.3f,%.3f,%d", n[3], n[4], n[5])
    end
    table.sort(keys)
    for _, key in ipairs(keys) do
        local kind, name = key:match("^(.-)\t(.*)$")
        rec(out, "W", kind, name, concat(groups[key], ";"))
    end
end

local livePos = { mapID = 0, x = 0, y = 0, facing = nil, flags = 0 }
function ns.UpdateLivePos(inCombat)
    if inCombat then
        livePos.flags = (livePos.flags % 2) + 2 -- keep "valid" bit, add "in combat"
        return livePos -- position frozen in combat (guardrail)
    end
    livePos.flags = 0
    local mapID = C_Map and C_Map.GetBestMapForUnit and C_Map.GetBestMapForUnit("player")
    livePos.mapID = mapID or 0
    if mapID and C_Map.GetPlayerMapPosition then
        local pos = C_Map.GetPlayerMapPosition(mapID, "player")
        if pos then
            local x, y = pos:GetXY()
            if x and y and (x > 0 or y > 0) then
                livePos.x, livePos.y, livePos.flags = x, y, 1
                -- bit 4: riding a flight path (so the companion doesn't learn it as a walkable trail)
                if UnitOnTaxi and UnitOnTaxi("player") then livePos.flags = 5 end
            end
        end
    end
    livePos.facing = GetPlayerFacing and GetPlayerFacing() or nil
    return livePos
end

---------------------------------------------------------------------------
-- Quest objective areas (the blue regions on the built-in map).
-- The shapes come from the game server and aren't readable directly, so we draw one
-- quest at a time on our own invisible QuestPOIFrame and ask the game's own hit-test
-- (UpdateMouseOverTooltip) which grid cells are inside it. Result: a 48x32 mask,
-- run-length encoded as dot-separated run lengths starting with an "outside" run.
--   R  questID  mapID  width  height  runs
---------------------------------------------------------------------------
local AREA_W, AREA_H = 48, 32
ns.AREA_W, ns.AREA_H = AREA_W, AREA_H
ns.areaCache = {}    -- [mapID] = { [questID] = runs or "" }
ns.areaSupport = nil -- set once the QuestPOIFrame has been tried

function ns.SampleArea(f, questID, mapID)
    if f.SetMapID then pcall(f.SetMapID, f, mapID) end
    if f.DrawNone then pcall(f.DrawNone, f) end
    if not pcall(f.DrawBlob, f, questID, true) then return nil end
    local runs, cur, n, any = {}, 0, 0, false
    for gy = 0, AREA_H - 1 do
        for gx = 0, AREA_W - 1 do
            local ok, q = pcall(f.UpdateMouseOverTooltip, f, (gx + 0.5) / AREA_W, (gy + 0.5) / AREA_H)
            local v = (ok and q == questID) and 1 or 0
            if v == 1 then any = true end
            if v == cur then n = n + 1 else runs[#runs + 1] = n; cur = v; n = 1 end
        end
    end
    runs[#runs + 1] = n
    pcall(f.DrawBlob, f, questID, false)
    return any and concat(runs, ".") or ""
end

local function CollectAreas(out, mapID)
    local c = mapID and ns.areaCache[mapID]
    if not c then return end
    local ids = {}
    for qid, runs in pairs(c) do if runs ~= "" then ids[#ids + 1] = qid end end
    table.sort(ids) -- stable order: identical state must give an identical payload
    for _, qid in ipairs(ids) do rec(out, "R", qid, mapID, AREA_W, AREA_H, c[qid]) end
end

---------------------------------------------------------------------------
-- Character sheet stats, grouped like the in-game panel.
--   T  group  label  value  color   (color: g = buffed, r = debuffed)
---------------------------------------------------------------------------
local function CollectStats(out)
    local function T(group, label, value, color) rec(out, "T", group, label, value, color or "") end
    local function pass(ok, ...) if ok then return ... end end
    local function try(fn, ...) return pass(pcall(fn, ...)) end
    local function pct(v) return v and string.format("%.2f%%", v) or nil end

    if UnitHealthMax then T("General", "Health", try(UnitHealthMax, "player")) end
    if UnitPowerType and UnitPowerMax then
        local pType, pToken = try(UnitPowerType, "player")
        local pmax = try(UnitPowerMax, "player", pType)
        if pmax and pmax > 0 then T("General", (pToken and _G[pToken]) or pToken or "Power", pmax) end
    end
    if GetUnitSpeed then
        local _, run = try(GetUnitSpeed, "player")
        if run and run > 0 then T("General", "Movement Speed", string.format("%d%%", floor(run / 7 * 100 + 0.5))) end
    end
    if UnitStat then
        for i = 1, 5 do
            local _, stat, pos, neg = try(UnitStat, "player", i)
            if stat then
                T("Primary Attributes", _G["SPELL_STAT" .. i .. "_NAME"] or ("Stat " .. i), stat,
                    (pos and pos > 0) and "g" or ((neg and neg < 0) and "r" or ""))
            end
        end
    end
    if UnitDamage then
        local minD, maxD = try(UnitDamage, "player")
        if minD and maxD then T("Melee", "Damage", floor(minD) .. " - " .. floor(maxD + 0.5)) end
    end
    if UnitAttackSpeed then
        local sp = try(UnitAttackSpeed, "player")
        if sp then T("Melee", "Speed", string.format("%.2f", sp)) end
    end
    if UnitAttackPower then
        local base, pos, neg = try(UnitAttackPower, "player")
        if base then T("Melee", "Attack Power", base + (pos or 0) + (neg or 0), (pos and pos > 0) and "g" or ((neg and neg < 0) and "r" or "")) end
    end
    if GetCritChance then T("Melee", "Crit Chance", pct(try(GetCritChance))) end
    if UnitRangedDamage then
        local speed, minD, maxD = try(UnitRangedDamage, "player")
        if speed and speed > 0 and minD then T("Ranged", "Damage", floor(minD) .. " - " .. floor(maxD + 0.5)) end
    end
    if UnitRangedAttackPower then
        local base, pos, neg = try(UnitRangedAttackPower, "player")
        if base and base > 0 then T("Ranged", "Attack Power", base + (pos or 0) + (neg or 0)) end
    end
    if GetRangedCritChance then
        local c = try(GetRangedCritChance)
        if c and c > 0 then T("Ranged", "Crit Chance", pct(c)) end
    end
    local pToken
    if UnitPowerType then pToken = select(2, try(UnitPowerType, "player")) end
    if GetSpellBonusDamage then
        local best = 0
        for school = 2, 7 do best = math.max(best, try(GetSpellBonusDamage, school) or 0) end
        if best > 0 or pToken == "MANA" then T("Spell", "Bonus Damage", best) end
    end
    if GetSpellBonusHealing then
        local h = try(GetSpellBonusHealing)
        if h and (h > 0 or pToken == "MANA") then T("Spell", "Bonus Healing", h) end
    end
    if GetSpellCritChance and pToken == "MANA" then T("Spell", "Crit Chance", pct(try(GetSpellCritChance, 2))) end
    if UnitArmor then
        local _, effective = try(UnitArmor, "player")
        if effective then T("Defense", "Armor", effective) end
    end
    if GetDodgeChance then T("Defense", "Dodge", pct(try(GetDodgeChance))) end
    if GetParryChance then local v = try(GetParryChance); if v and v > 0 then T("Defense", "Parry", pct(v)) end end
    if GetBlockChance then local v = try(GetBlockChance); if v and v > 0 then T("Defense", "Block", pct(v)) end end
    if UnitResistance then
        local names = { [2] = "Fire", [3] = "Nature", [4] = "Frost", [5] = "Shadow", [6] = "Arcane" }
        for i = 2, 6 do
            local _, total = try(UnitResistance, "player", i)
            if total then T("Resistances", names[i], total) end
        end
    end
end

---------------------------------------------------------------------------
-- Session numbers (XP, gold, gathering), nearby rares/treasures, reputations, bank, extra waypoints
--   Y  sessionStart xp xpMax rested xpGained moneyStart herbs ore fish chests fps latency lowestDurability
--   V  name mapID x y atlas vignetteID          things the minimap is currently pointing out (rares, treasures)
--   U  name reaction barMin barMax value        reputations
--   O  itemID count quality name                bank contents, remembered from the last bank visit
---------------------------------------------------------------------------
ns.session = { xpGained = 0, gathered = { H = 0, M = 0, F = 0, T = 0 }, fps = 0, ms = 0 }
local function CollectSession(out)
    local s = ns.session
    local low = ""
    if GetInventoryItemDurability then
        for slot = 1, 19 do
            local cur, max = GetInventoryItemDurability(slot)
            if cur and max and max > 0 then
                local p = floor(cur / max * 100)
                if low == "" or p < low then low = p end
            end
        end
    end
    local g = s.gathered or {}
    rec(out, "Y", s.start or "", UnitXP and UnitXP("player") or 0, UnitXPMax and UnitXPMax("player") or 0,
        GetXPExhaustion and GetXPExhaustion() or 0, s.xpGained or 0, s.moneyStart or "",
        g.H or 0, g.M or 0, g.F or 0, g.T or 0, s.fps or 0, s.ms or 0, low)
end

ns.vignettes = {}
function ns.ScanVignettes()
    local list = {}
    if C_VignetteInfo and C_VignetteInfo.GetVignettes and C_VignetteInfo.GetVignetteInfo then
        local mapID = C_Map and C_Map.GetBestMapForUnit and C_Map.GetBestMapForUnit("player")
        for _, guid in ipairs(C_VignetteInfo.GetVignettes() or {}) do
            local info = C_VignetteInfo.GetVignetteInfo(guid)
            if info and type(info.name) == "string" and info.name ~= "" and not info.isDead then
                local x, y = "", ""
                if mapID and C_VignetteInfo.GetVignettePosition then
                    local pos = C_VignetteInfo.GetVignettePosition(guid, mapID)
                    if pos then
                        local px, py = pos:GetXY()
                        if px and py then x, y = string.format("%.4f", px), string.format("%.4f", py) end
                    end
                end
                list[#list + 1] = { info.name, mapID or 0, x, y, info.atlasName or "", info.vignetteID or 0 }
            end
        end
        table.sort(list, function(a, b) if a[1] ~= b[1] then return a[1] < b[1] end return tostring(a[6]) < tostring(b[6]) end)
    end
    ns.vignettes = list
end
local function CollectVignettes(out)
    for i, v in ipairs(ns.vignettes) do
        if i > 12 then break end
        rec(out, "V", v[1], v[2], v[3], v[4], v[5], v[6])
    end
end

local function CollectReputation(out)
    if C_Reputation and C_Reputation.GetNumFactions and C_Reputation.GetFactionDataByIndex then
        for i = 1, C_Reputation.GetNumFactions() do
            local d = C_Reputation.GetFactionDataByIndex(i)
            if d and d.name and (not d.isHeader or d.isHeaderWithRep) then
                rec(out, "U", d.name, d.reaction or 0, d.currentReactionThreshold or 0, d.nextReactionThreshold or 0, d.currentStanding or 0)
            end
        end
    elseif GetNumFactions and GetFactionInfo then
        for i = 1, GetNumFactions() do
            local name, _, standing, barMin, barMax, barValue, _, _, isHeader, _, hasRep = GetFactionInfo(i)
            if name and (not isHeader or hasRep) then rec(out, "U", name, standing or 0, barMin or 0, barMax or 0, barValue or 0) end
        end
    end
end

-- Recipes: read while a profession window is open, remembered per character, sent every time.
--   J  profession  recipeName  difficulty(0 orange..3 grey)  outputItemID  outputCount  reagents
--      reagents = "itemID,count,name" joined by 0x1F
local function ItemIdFromLink(link) return link and tonumber(tostring(link):match("item:(%d+)")) end
local function ItemName(id)
    return (C_Item and C_Item.GetItemNameByID and C_Item.GetItemNameByID(id)) or (ItemInfo and ItemInfo(id)) or ""
end
-- Reads the open profession window. Returns profession name, recipe list, and a note saying which API answered.
function ns.ReadRecipes()
    local T = C_TradeSkillUI
    local list, profName, how = {}, nil, "none"
    if T and T.GetAllRecipeIDs and T.GetRecipeInfo then
        if T.IsTradeSkillLinked and T.IsTradeSkillLinked() then return nil, list, "linked profession (not yours)" end
        local base = T.GetBaseProfessionInfo and T.GetBaseProfessionInfo()
        profName = base and (base.professionName or base.parentProfessionName)
        if not profName or profName == "" then
            local child = T.GetChildProfessionInfo and T.GetChildProfessionInfo()
            profName = child and (child.parentProfessionName or child.professionName)
        end
        if (not profName or profName == "") and T.GetTradeSkillLine then
            local _, lineName, _, _, _, _, parentName = T.GetTradeSkillLine()
            profName = parentName or lineName
        end
        local ids = T.GetAllRecipeIDs() or {}
        how = "C_TradeSkillUI (" .. #ids .. " ids)"
        for _, id in ipairs(ids) do
            local info = T.GetRecipeInfo(id)
            if info and info.learned and info.name and not info.isRecraft then
                local reagents, outID, outQty = {}, 0, 1
                local okS, sch = pcall(T.GetRecipeSchematic, id, false)
                if okS and type(sch) == "table" then
                    outID, outQty = sch.outputItemID or 0, sch.quantityMin or 1
                    for _, slot in ipairs(sch.reagentSlotSchematics or {}) do
                        local r = slot.reagents and slot.reagents[1]
                        if r and r.itemID and slot.required ~= false and (slot.reagentType == nil or slot.reagentType == 1) then
                            reagents[#reagents + 1] = { r.itemID, slot.quantityRequired or 1, ItemName(r.itemID) }
                        end
                    end
                end
                list[#list + 1] = { id, info.name, info.relativeDifficulty or 3, outID, outQty, reagents }
            end
        end
    end
    -- Older trade skill API (classic-style clients)
    if #list == 0 and GetNumTradeSkills and GetTradeSkillInfo then
        local DIFF = { optimal = 0, medium = 1, easy = 2, trivial = 3 }
        profName = (GetTradeSkillLine and GetTradeSkillLine()) or profName
        local n = GetNumTradeSkills() or 0
        how = "GetTradeSkillInfo (" .. n .. " rows)"
        for i = 1, n do
            local name, kind = GetTradeSkillInfo(i)
            if name and kind ~= "header" and kind ~= "subheader" then
                local reagents = {}
                for r = 1, (GetTradeSkillNumReagents and GetTradeSkillNumReagents(i) or 0) do
                    local rname, _, count = GetTradeSkillReagentInfo(i, r)
                    local rid = ItemIdFromLink(GetTradeSkillReagentItemLink and GetTradeSkillReagentItemLink(i, r))
                    if rid then reagents[#reagents + 1] = { rid, count or 1, rname or "" } end
                end
                local outID = ItemIdFromLink(GetTradeSkillItemLink and GetTradeSkillItemLink(i)) or 0
                local made = GetTradeSkillNumMade and GetTradeSkillNumMade(i) or 1
                list[#list + 1] = { outID > 0 and outID or i, name, DIFF[kind] or 3, outID, made, reagents }
            end
        end
    end
    return profName, list, how
end
function ns.ScanRecipes(db)
    local profName, list = ns.ReadRecipes()
    if not profName or profName == "" or #list == 0 then return 0 end
    table.sort(list, function(a, b) if a[2] ~= b[2] then return a[2] < b[2] end return a[1] < b[1] end)
    db.recipes = db.recipes or {}
    local key = ns.CharKey()
    db.recipes[key] = db.recipes[key] or {}
    db.recipes[key][profName] = list
    return #list, profName
end
local function CollectRecipes(out)
    local mine = ns.recipeDB and ns.recipeDB[ns.CharKey()]
    if not mine then return end
    local names = {}
    for name in pairs(mine) do names[#names + 1] = name end
    table.sort(names)
    local n = 0
    for _, prof in ipairs(names) do
        for _, r in ipairs(mine[prof]) do
            n = n + 1
            if n > 240 then return end
            local parts = {}
            for _, g in ipairs(r[6] or {}) do
                parts[#parts + 1] = cleanPart(g[1] .. "," .. g[2] .. "," .. (g[3] or ""))
            end
            out[#out + 1] = concat({ "J", clean(prof), clean(r[2]), tostring(r[3] or 3), tostring(r[4] or 0), tostring(r[5] or 1),
                                     concat(parts, "\031") }, "\t")
        end
    end
end

function ns.CharKey()
    return (UnitName("player") or "?") .. "-" .. (GetRealmName and GetRealmName() or "")
end
-- Called while the bank window is open: remember what is in it (stacks of the same item are merged).
function ns.ScanBank(db)
    local bags, seen, totals, order = {}, {}, {}, {}
    local function add(b) if b and not seen[b] then seen[b] = true; bags[#bags + 1] = b end end
    add(BANK_CONTAINER or -1)
    local first = ns.LastCarriedBag() + 1                     -- skip the reagent bag: it is carried, not banked
    for b = first, first + (NUM_BANKBAGSLOTS or 7) do add(b) end
    if Enum and Enum.BagIndex then
        for i = 1, 8 do add(Enum.BagIndex["CharacterBankTab_" .. i]) end
    end
    for _, bag in ipairs(bags) do
        local ok, slots = pcall(NumSlots, bag)
        if ok and slots and slots > 0 then
            for slot = 1, slots do
                local itemID, count, quality, link, _, name = GetContainerSlotInfo(bag, slot)
                if itemID then
                    local t = totals[itemID]
                    if not t then
                        t = { itemID, 0, quality or -1, name or NameFromLink(link) or "" }
                        totals[itemID] = t; order[#order + 1] = t
                    end
                    t[2] = t[2] + (count or 1)
                end
            end
        end
    end
    table.sort(order, function(a, b) if a[4] ~= b[4] then return a[4] < b[4] end return a[1] < b[1] end)
    db.bank = db.bank or {}
    db.bank[ns.CharKey()] = order
    return #order
end
local function CollectBank(out)
    local list = ns.bankDB and ns.bankDB[ns.CharKey()]
    if not list then return end
    for i, t in ipairs(list) do
        if i > 250 then break end
        rec(out, "O", t[1], t[2], t[3], t[4])
    end
end

-- TomTom waypoints and the game's own map pin, for the maps currently shown (kind "W")
local function CollectWaypoints(out, levels)
    local want, list = {}, {}
    for _, l in ipairs(levels or {}) do want[l.id] = true end
    if TomTom and type(TomTom.waypoints) == "table" then
        for _, perMap in pairs(TomTom.waypoints) do
            if type(perMap) == "table" then
                for _, wp in pairs(perMap) do
                    if type(wp) == "table" and want[wp[1]] and type(wp[2]) == "number" and type(wp[3]) == "number" then
                        list[#list + 1] = { wp[1], wp[2], wp[3], wp.title or "TomTom waypoint", 0 }
                    end
                end
            end
        end
    end
    if C_Map and C_Map.GetUserWaypoint then
        local p = C_Map.GetUserWaypoint()
        if p and p.position and want[p.uiMapID] then
            local x, y = p.position:GetXY()
            if x and y then list[#list + 1] = { p.uiMapID, x, y, "Map pin", 1 } end
        end
    end
    table.sort(list, function(a, b)
        if a[1] ~= b[1] then return a[1] < b[1] end
        if a[2] ~= b[2] then return a[2] < b[2] end
        return a[3] < b[3]
    end)
    for i, w in ipairs(list) do
        if i > 30 then break end
        pin(out, w[1], "W", w[2], w[3], w[4], w[5], 0)
    end
end

---------------------------------------------------------------------------
-- Optional: walking paths from Polestar's router, when the player has Polestar installed. EdgeLink only asks
-- that addon's engine (the global Polestar_NavEngine) a question at run time; none of its code or maps are copied.
--   L  status  yards  targetMap  targetX  targetY  "x,y;x,y;..."   (path in the current zone map's coordinates)
-- status: ok, searching, or why there is no path (noengine, othermap, nodata, outside, offmesh, unreachable, error)
---------------------------------------------------------------------------
ns.nav = { status = "" }
local function WorldFromMap(mapID, x, y)
    if not (C_Map and C_Map.GetWorldPosFromMapPos and CreateVector2D) then return nil end
    local ok, inst, pos = pcall(C_Map.GetWorldPosFromMapPos, mapID, CreateVector2D(x, y))
    if not ok or not pos then return nil end
    local wx, wy = pos:GetXY()
    return inst, wx, wy
end
local function CollectNav(out, levels)
    local nav, t = ns.nav, ns.nav.target
    if not t then return end
    local pts = ""
    local zone = levels and levels[1] and levels[1].id
    if nav.path and zone then
        -- world -> zone map: world X runs north-south (map y), world Y east-west (map x)
        local i0, x0, y0 = WorldFromMap(zone, 0, 0)
        local i1, x1, y1 = WorldFromMap(zone, 1, 1)
        if i0 and i1 and i0 == t.inst and x0 ~= x1 and y0 ~= y1 then
            local parts = {}
            for i, w in ipairs(nav.path) do
                parts[i] = string.format("%.4f,%.4f", (y0 - w[2]) / (y0 - y1), (x0 - w[1]) / (x0 - x1))
            end
            pts = concat(parts, ";")
        end
    end
    rec(out, "L", nav.status or "", string.format("%.0f", nav.yards or 0), t.map, string.format("%.4f", t.x), string.format("%.4f", t.y), pts)
end

function ns.BuildSnapshot()
    local out = {}
    local ok_, err_
    -- No timestamp here: an unchanged game state must produce an identical payload,
    -- otherwise every rebuild starts a new snapshot and multi-page sends never settle.
    rec(out, "H", P.VERSION)
    local _, className = UnitClass("player")
    local raceName = UnitRace("player")
    rec(out, "P", UnitName("player"), GetRealmName and GetRealmName() or "", UnitLevel("player"),
        className or "", raceName or "", GetMoney and GetMoney() or 0,
        GetZoneText and GetZoneText() or "", GetSubZoneText and GetSubZoneText() or "",
        UnitFactionGroup and UnitFactionGroup("player") or "")
    CollectBags(out)
    ok_, err_ = pcall(CollectSkills, out)
    if not ok_ then rec(out, "X", "skills: " .. tostring(err_)) end
    CollectEquipment(out)
    CollectQuests(out)
    local levels = CollectMap(out)
    local ok, err = pcall(CollectPins, out, levels)
    if not ok then rec(out, "X", "pins: " .. tostring(err)) end
    ok, err = pcall(CollectAreas, out, levels[1] and levels[1].id)
    if not ok then rec(out, "X", "areas: " .. tostring(err)) end
    ok, err = pcall(CollectNodes, out, levels[1] and levels[1].id)
    if not ok then rec(out, "X", "nodes: " .. tostring(err)) end
    ok, err = pcall(CollectStats, out)
    if not ok then rec(out, "X", "stats: " .. tostring(err)) end
    for _, c in ipairs({ { "session", CollectSession }, { "nearby", CollectVignettes }, { "reputation", CollectReputation },
                         { "bank", CollectBank }, { "recipes", CollectRecipes } }) do
        ok, err = pcall(c[2], out)
        if not ok then rec(out, "X", c[1] .. ": " .. tostring(err)) end
    end
    ok, err = pcall(CollectNav, out, levels)
    if not ok then rec(out, "X", "nav: " .. tostring(err)) end
    ok, err = pcall(CollectTree, out, levels)
    if not ok then rec(out, "X", "map tree: " .. tostring(err)) end
    ok, err = pcall(CollectWaypoints, out, levels)
    if not ok then rec(out, "X", "waypoints: " .. tostring(err)) end
    return concat(out, "\n")
end

---------------------------------------------------------------------------
-- Pixel strip frame (only when running inside WoW)
---------------------------------------------------------------------------
if not CreateFrame then return end

local defaults = { enabled = true, pageInterval = 0.10, rebuildInterval = 1.0 }

local strip, textures
local chunks, pageIndex = nil, 1
local snapshotId, lastPayload = 0, nil
local dirty, sinceRebuild, sincePage = true, 99, 0
local pagesShown = 0   -- pages shown since the current snapshot started

local function ApplyScale()
    local _, physH = GetPhysicalScreenSize()
    if not physH or physH <= 0 then physH = 768 end
    strip:SetScale(768 / physH) -- 1 UI unit == 1 physical pixel
end

local function CreateStrip()
    strip = CreateFrame("Frame", "EdgeLinkStrip", UIParent)
    if strip.SetIgnoreParentScale then strip:SetIgnoreParentScale(true) end
    strip:SetFrameStrata("TOOLTIP")
    strip:SetFrameLevel(10000)
    strip:SetSize(P.BLOCKS * P.BLOCK_PX, P.BLOCK_PX)
    strip:SetPoint("TOPLEFT", UIParent, "TOPLEFT", 0, 0)
    strip:SetClampedToScreen(true)
    strip:EnableMouse(false)
    textures = {}
    for i = 1, P.BLOCKS do
        local t = strip:CreateTexture(nil, "OVERLAY")
        t:SetSize(P.BLOCK_PX, P.BLOCK_PX)
        t:SetPoint("TOPLEFT", strip, "TOPLEFT", (i - 1) * P.BLOCK_PX, 0)
        if t.SetSnapToPixelGrid then t:SetSnapToPixelGrid(false) end
        if t.SetTexelSnappingBias then t:SetTexelSnappingBias(0) end
        t:SetColorTexture(0, 0, 0, 1)
        textures[i] = t
    end
    ApplyScale()
end

-- Draw the current page, with a fresh live position in its header.
local function ShowPage()
    if not chunks then return end
    local pos = ns.UpdateLivePos(InCombatLockdown())
    local b = ns.EncodePage(chunks[pageIndex], snapshotId, pageIndex - 1, #chunks, pos)
    for i = 1, P.BLOCKS do
        local o = (i - 1) * 3
        textures[i]:SetColorTexture(b[o + 1] / 255, b[o + 2] / 255, b[o + 3] / 255, 1)
    end
end

local function Rebuild()
    local ok, payload = pcall(ns.BuildSnapshot)
    if not ok then
        payload = "H\t" .. P.VERSION .. "\nX\t" .. clean(payload)
    end
    if payload ~= lastPayload then
        lastPayload = payload
        snapshotId = (snapshotId + 1) % 256
        chunks = ns.SplitPayload(payload)
        pageIndex = 1
        pagesShown = 1
        ShowPage()
    end
    QueueAreas()
end

-- Quest-area sampling: one quest per frame, out of combat only, results cached per map.
local areaFrame, areaQueue = nil, {}
local function EnsureAreaFrame()
    if areaFrame ~= nil then return areaFrame end
    local ok, f = pcall(CreateFrame, "QuestPOIFrame", nil, UIParent)
    if ok and f and f.DrawBlob and f.UpdateMouseOverTooltip then
        f:SetSize(480, 320)
        f:SetPoint("BOTTOMLEFT", UIParent, "BOTTOMLEFT", 0, 0)
        f:SetFrameStrata("BACKGROUND")
        f:SetAlpha(0)
        if f.SetFillAlpha then pcall(f.SetFillAlpha, f, 0) end
        if f.SetBorderAlpha then pcall(f.SetBorderAlpha, f, 0) end
        if f.EnableMouse then f:EnableMouse(false) end
        f:Show()
        areaFrame, ns.areaSupport = f, true
    else
        areaFrame, ns.areaSupport = false, false
    end
    return areaFrame
end

local function QueueAreas()
    if not EnsureAreaFrame() then return end
    local mapID = C_Map and C_Map.GetBestMapForUnit and C_Map.GetBestMapForUnit("player")
    if not mapID or not ns.questIDs then return end
    local c = ns.areaCache[mapID] or {}
    ns.areaCache[mapID] = c
    for i = #areaQueue, 1, -1 do areaQueue[i] = nil end
    for _, qid in ipairs(ns.questIDs) do
        if c[qid] == nil then areaQueue[#areaQueue + 1] = { qid, mapID } end
    end
end

local function SampleNextArea()
    local job = table.remove(areaQueue, 1)
    if not job or not areaFrame then return end
    local runs = ns.SampleArea(areaFrame, job[1], job[2])
    local c = ns.areaCache[job[2]] or {}
    ns.areaCache[job[2]] = c
    c[job[1]] = runs or ""
    if #areaQueue == 0 then dirty = true end -- publish the new shapes
end

local function OnUpdate(_, elapsed)
    if not EdgeLinkDB.enabled then return end
    if #areaQueue > 0 and not InCombatLockdown() then SampleNextArea() end
    sinceRebuild = sinceRebuild + elapsed
    sincePage = sincePage + elapsed
    -- Let the companion see every page of the current snapshot once before replacing it; otherwise
    -- frequent changes to a long snapshot would keep restarting it and nothing would ever arrive.
    local passDone = not chunks or pagesShown >= #chunks + 1
    if dirty and passDone and sinceRebuild >= EdgeLinkDB.rebuildInterval and not InCombatLockdown() then
        dirty, sinceRebuild = false, 0
        Rebuild()
    end
    -- Advance (or, with one page, just refresh) every interval so the position stays live.
    if chunks and sincePage >= EdgeLinkDB.pageInterval then
        sincePage = 0
        pageIndex = pageIndex % #chunks + 1
        pagesShown = pagesShown + 1
        ShowPage()
    end
end

local events = CreateFrame("Frame")
local DIRTY_EVENTS = {
    "BAG_UPDATE_DELAYED", "PLAYER_EQUIPMENT_CHANGED", "UNIT_INVENTORY_CHANGED",
    "QUEST_LOG_UPDATE", "ZONE_CHANGED", "ZONE_CHANGED_NEW_AREA", "ZONE_CHANGED_INDOORS",
    "PLAYER_MONEY", "PLAYER_LEVEL_UP", "PLAYER_REGEN_ENABLED", "GET_ITEM_INFO_RECEIVED",
    "UNIT_STATS", "UNIT_ATTACK_POWER", "UNIT_RANGED_ATTACK_POWER", "UNIT_RESISTANCES", "UNIT_MAXHEALTH",
    "UNIT_ATTACK_SPEED", "UNIT_DAMAGE", "PLAYER_DAMAGE_DONE_MODS", "COMBAT_RATING_UPDATE", "SKILL_LINES_CHANGED",
    "CHAT_MSG_SKILL",
}
-- Events after which quest areas must be sampled again
local AREA_EVENTS = {
    QUEST_POI_UPDATE = true, QUEST_ACCEPTED = true, QUEST_REMOVED = true, QUEST_TURNED_IN = true,
    UNIT_QUEST_LOG_CHANGED = true, ZONE_CHANGED_NEW_AREA = true,
}
for e in pairs(AREA_EVENTS) do pcall(events.RegisterEvent, events, e) end
events:RegisterEvent("PLAYER_LOGIN")
events:RegisterEvent("DISPLAY_SIZE_CHANGED")
events:RegisterEvent("UI_SCALE_CHANGED")
for _, e in ipairs(DIRTY_EVENTS) do
    pcall(events.RegisterEvent, events, e)
end
-- Gathering detection: remember the target of a gathering cast, record it when the cast succeeds.
local function PlayerSpot()
    local mapID = C_Map and C_Map.GetBestMapForUnit and C_Map.GetBestMapForUnit("player")
    if not mapID or not C_Map.GetPlayerMapPosition then return nil end
    local pos = C_Map.GetPlayerMapPosition(mapID, "player")
    if not pos then return nil end
    local x, y = pos:GetXY()
    if not x or (x == 0 and y == 0) then return nil end
    return mapID, x, y
end
local function SpellName(spellID)
    if C_Spell and C_Spell.GetSpellName then return C_Spell.GetSpellName(spellID) end
    if GetSpellInfo then return (GetSpellInfo(spellID)) end
end
local pendingGather, lastGatherAt = nil, 0
-- Recent casts on named targets, for '/edgelink casts' (diagnostics; not saved).
ns.castLog = {}
local function LogCast(spellID, spellName, target)
    table.insert(ns.castLog, 1, string.format("%s (%s) -> %s", tostring(spellName), tostring(spellID), tostring(target)))
    if #ns.castLog > 12 then table.remove(ns.castLog) end
end
-- Loot fallback: what did a world object drop? Trade Goods (7): Herb (9) = herb node, Metal & Stone (7) = mining node.
function ns.LootKind(classID, subClassID)
    if classID ~= 7 then return nil end
    if subClassID == 9 then return "H" end
    if subClassID == 7 then return "M" end
    return nil
end
local function LootFallback()
    if GetTime() - lastGatherAt < 5 then return end            -- the cast path already recorded this one
    if not GetNumLootItems or not GetLootSourceInfo then return end
    local ok, guid = pcall(GetLootSourceInfo, 1)
    if not ok or type(guid) ~= "string" or not guid:find("^GameObject") then return end
    for i = 1, GetNumLootItems() do
        local link = GetLootSlotLink and GetLootSlotLink(i)
        local itemID = link and tonumber(link:match("item:(%d+)"))
        if itemID and ItemInfoInstant then
            local name, _, _, _, _, classID, subClassID = ItemInfoInstant(itemID)
            local kind = ns.LootKind(classID, subClassID)
            if kind then
                local mapID, x, y = PlayerSpot()
                local nodeName = (GetLootSlotInfo and select(2, GetLootSlotInfo(i))) or name or (kind == "H" and "Herb" or "Ore")
                if mapID and ns.RecordGather(ns.nodeDB, kind, nodeName, mapID, x, y) then dirty = true; lastGatherAt = GetTime() end
                return
            end
        end
    end
end
local gather = CreateFrame("Frame")
for _, e in ipairs({ "UNIT_SPELLCAST_SENT", "UNIT_SPELLCAST_SUCCEEDED", "UNIT_SPELLCAST_FAILED",
                     "UNIT_SPELLCAST_INTERRUPTED", "LOOT_OPENED" }) do
    pcall(gather.RegisterEvent, gather, e)
end
gather:SetScript("OnEvent", function(_, event, a1, a2, a3, a4)
    if event == "UNIT_SPELLCAST_SENT" then
        -- (unit, target, castGUID, spellID)
        if a1 ~= "player" then return end
        local spell = SpellName(a4)
        if a2 and a2 ~= "" then LogCast(a4, spell, a2) end
        local kind = ns.GatherKind(spell)
        if kind and a2 and a2 ~= "" then pendingGather = { kind = kind, name = a2, guid = a3 } else pendingGather = nil end
    elseif event == "UNIT_SPELLCAST_SUCCEEDED" then
        -- (unit, castGUID, spellID)
        if a1 ~= "player" or not pendingGather or pendingGather.guid ~= a2 then return end
        local mapID, x, y = PlayerSpot()
        if mapID and ns.RecordGather(ns.nodeDB, pendingGather.kind, pendingGather.name, mapID, x, y) then dirty = true; lastGatherAt = GetTime() end
        pendingGather = nil
    elseif event == "UNIT_SPELLCAST_FAILED" or event == "UNIT_SPELLCAST_INTERRUPTED" then
        if a1 == "player" then pendingGather = nil end
    elseif event == "LOOT_OPENED" then
        if IsFishingLoot and IsFishingLoot() then
            local mapID, x, y = PlayerSpot()
            if mapID and ns.RecordGather(ns.nodeDB, "F", "Fishing spot", mapID, x, y) then dirty = true end
        else
            LootFallback()
        end
    end
end)

events:SetScript("OnEvent", function(_, event)
    if event == "PLAYER_LOGIN" then
        EdgeLinkDB = EdgeLinkDB or {}
        for k, v in pairs(defaults) do
            if EdgeLinkDB[k] == nil then EdgeLinkDB[k] = v end
        end
        EdgeLinkDB.nodes = EdgeLinkDB.nodes or {}
        ns.nodeDB = EdgeLinkDB.nodes
        EdgeLinkDB.bank = EdgeLinkDB.bank or {}
        ns.bankDB = EdgeLinkDB.bank
        EdgeLinkDB.recipes = EdgeLinkDB.recipes or {}
        ns.recipeDB = EdgeLinkDB.recipes
        -- A session survives /reload and short relogs (same character, seen in the last 10 minutes)
        local old, now = EdgeLinkDB.session, time()
        if type(old) == "table" and old.char == ns.CharKey() and old.seen and now - old.seen < 600 and old.start then
            ns.session = old
            old.gathered = old.gathered or { H = 0, M = 0, F = 0, T = 0 }
        else
            ns.session = { char = ns.CharKey(), start = now, xpGained = 0, moneyStart = GetMoney and GetMoney() or 0,
                           gathered = { H = 0, M = 0, F = 0, T = 0 }, fps = 0, ms = 0 }
        end
        EdgeLinkDB.session = ns.session
        ns.session.lastXP, ns.session.lastMax = UnitXP("player"), UnitXPMax("player")
        CreateStrip()
        strip:SetShown(EdgeLinkDB.enabled)
        strip:SetScript("OnUpdate", OnUpdate)
        ns.BindInput()
    elseif event == "DISPLAY_SIZE_CHANGED" or event == "UI_SCALE_CHANGED" then
        if strip then ApplyScale() end
    else
        if event == "PLAYER_REGEN_ENABLED" and not ns.inputReady then ns.BindInput() end
        if AREA_EVENTS[event] then
            for k in pairs(ns.areaCache) do ns.areaCache[k] = nil end
        end
        dirty = true
    end
end)

-- Session, rares, reputation, bank and waypoint events
do
    local origRecord = ns.RecordGather
    ns.RecordGather = function(db, kind, ...)
        local r = origRecord(db, kind, ...)
        local g = ns.session and ns.session.gathered
        if r and g and g[kind] then g[kind] = g[kind] + 1 end
        return r
    end
    local bankOpen = false
    local extra = CreateFrame("Frame")
    for _, e in ipairs({ "PLAYER_XP_UPDATE", "UPDATE_EXHAUSTION", "UPDATE_FACTION", "VIGNETTE_MINIMAP_UPDATED", "VIGNETTES_UPDATED",
                         "BANKFRAME_OPENED", "BANKFRAME_CLOSED", "PLAYERBANKSLOTS_CHANGED", "BAG_UPDATE_DELAYED",
                         "USER_WAYPOINT_UPDATED", "UPDATE_INVENTORY_DURABILITY", "PLAYER_LOGOUT", "PLAYER_ENTERING_WORLD",
                         "TRADE_SKILL_SHOW", "TRADE_SKILL_LIST_UPDATE", "NEW_RECIPE_LEARNED" }) do
        pcall(extra.RegisterEvent, extra, e)
    end
    extra:SetScript("OnEvent", function(_, event)
        local s = ns.session
        if event == "PLAYER_LOGOUT" then
            if s then s.seen = time() end
            return
        elseif event == "PLAYER_XP_UPDATE" then
            local xp, max = UnitXP("player"), UnitXPMax("player")
            if s and s.lastXP then
                local d = xp - s.lastXP
                if d < 0 then d = (s.lastMax or 0) - s.lastXP + xp end     -- levelled up
                if d > 0 then s.xpGained = (s.xpGained or 0) + d end
            end
            if s then s.lastXP, s.lastMax = xp, max end
        elseif event == "VIGNETTE_MINIMAP_UPDATED" or event == "VIGNETTES_UPDATED" or event == "PLAYER_ENTERING_WORLD" then
            pcall(ns.ScanVignettes)
        elseif event == "TRADE_SKILL_SHOW" or event == "TRADE_SKILL_LIST_UPDATE" or event == "NEW_RECIPE_LEARNED" then
            -- the list fills in over a few frames; read it once it has settled
            if ns.recipeScanPending or not C_Timer then return end
            ns.recipeScanPending = true
            C_Timer.After(1, function()
                ns.recipeScanPending = false
                local ok, n = pcall(ns.ScanRecipes, EdgeLinkDB)
                if ok and n and n > 0 then dirty = true end
            end)
            return
        elseif event == "BANKFRAME_OPENED" then
            bankOpen = true
            pcall(ns.ScanBank, EdgeLinkDB)
        elseif event == "BANKFRAME_CLOSED" then
            bankOpen = false
            return
        elseif event == "PLAYERBANKSLOTS_CHANGED" or event == "BAG_UPDATE_DELAYED" then
            if not bankOpen then return end
            pcall(ns.ScanBank, EdgeLinkDB)
        end
        dirty = true
    end)
    -- Frame rate and latency: sampled twice a minute so they don't force a new snapshot every second
    if C_Timer and C_Timer.NewTicker then
        C_Timer.NewTicker(30, function()
            local s = ns.session
            if not s then return end
            local fps = GetFramerate and floor(GetFramerate() + 0.5) or 0
            local ms = 0
            if GetNetStats then local _, _, home, world = GetNetStats(); ms = world and world > 0 and world or home or 0 end
            if fps ~= s.fps or ms ~= s.ms then s.fps, s.ms = fps, ms; dirty = true end
        end)
    end
end

-- Polestar walking paths: ask its router, keep the answer, ask again when the player strays from it
do
    local nav = ns.nav
    local function Engine()
        local P = _G.Polestar_NavEngine
        if (type(P) ~= "table" or type(P.Find) ~= "function") and not nav.loadTried and not InCombatLockdown()
            and C_AddOns and C_AddOns.LoadAddOn then
            nav.loadTried = true                     -- its maps are large: loaded once, on the first request
            pcall(C_AddOns.LoadAddOn, "Polestar_Nav")
            P = _G.Polestar_NavEngine
        end
        if type(P) == "table" and type(P.Find) == "function" then return P end
    end
    -- Keep corners, drop points on straight stretches, and cap the count so the path stays small on the strip
    local function Thin(points)
        local out, n = {}, #points
        local step = math.max(1, math.ceil(n / 400))
        local keep = {}
        for i = 1, n, step do keep[#keep + 1] = points[i] end
        if keep[#keep] ~= points[n] then keep[#keep + 1] = points[n] end
        out[1] = { keep[1].x, keep[1].y }
        for i = 2, #keep - 1 do
            local a, b, c = out[#out], keep[i], keep[i + 1]
            local ax, ay, bx, by = b.x - a[1], b.y - a[2], c.x - b.x, c.y - b.y
            local cross = math.abs(ax * by - ay * bx)
            local len = math.sqrt(ax * ax + ay * ay)
            -- keep when the line bends (more than ~2.5 yd off straight) or the stretch is getting long
            if (len > 0 and cross / len > 2.5) or len > 150 then out[#out + 1] = { b.x, b.y } end
        end
        local last = keep[#keep]
        out[#out + 1] = { last.x, last.y }
        while #out > 160 do
            local thin = { out[1] }
            for i = 2, #out - 1, 2 do thin[#thin + 1] = out[i] end
            thin[#thin + 1] = out[#out]
            out = thin
        end
        return out
    end
    local function Fail(why) nav.path, nav.yards, nav.status = nil, 0, why; dirty = true end
    function ns.NavRequest()
        local t = nav.target
        if not t then return end
        nav.lastTry = GetTime()
        local P = Engine()
        if not P then return Fail("noengine") end
        local px, py, pz, inst = UnitPosition("player")
        if not px or inst ~= t.inst then return Fail("othermap") end
        if P.Available and not P.Available(inst) then return Fail("nodata") end
        if nav.job and P.Cancel then pcall(P.Cancel, nav.job) end
        local mine = {}
        nav.token = mine
        local faction = UnitFactionGroup and UnitFactionGroup("player") or nil
        if faction ~= "Alliance" and faction ~= "Horde" then faction = nil end
        local ok, job = pcall(P.Find, inst, { x = px, y = py, z = pz }, { x = t.wx, y = t.wy }, function(points, cost, job)
            if nav.token ~= mine then return end       -- an older search finishing late
            nav.job = nil
            if type(points) == "table" and #points > 1 then
                local okT, thin = pcall(Thin, points)
                if okT then
                    nav.path, nav.status = thin, "ok"
                    nav.yards = (type(job) == "table" and type(job.yards) == "number" and job.yards) or 0
                    dirty = true
                    return
                end
            end
            Fail(type(cost) == "string" and cost or "unreachable")
        end, { waterWalking = false, profile = faction })
        if ok then
            nav.job = job
            if nav.status ~= "ok" then nav.status = "searching"; dirty = true end
        else
            Fail("error")
        end
    end
    function ns.NavSet(mapID, x, y)
        if not mapID then
            if nav.job and _G.Polestar_NavEngine and _G.Polestar_NavEngine.Cancel then pcall(_G.Polestar_NavEngine.Cancel, nav.job) end
            nav.target, nav.path, nav.job, nav.token, nav.status = nil, nil, nil, nil, ""
            dirty = true
            return true
        end
        local inst, wx, wy = WorldFromMap(mapID, x, y)
        if not inst then print("|cff7fd4ffEdgeLink|r that spot has no world position."); return false end
        nav.target = { map = mapID, x = x, y = y, inst = inst, wx = wx, wy = wy }
        nav.path, nav.status = nil, "searching"
        dirty = true
        ns.NavRequest()
        return true
    end
    -- How far the player is from the path (yards, to the nearest corner or along a stretch)
    local function OffPath(px, py)
        local best = math.huge
        local path = nav.path
        for i = 1, #path - 1 do
            local ax, ay, bx, by = path[i][1], path[i][2], path[i + 1][1], path[i + 1][2]
            local dx, dy = bx - ax, by - ay
            local len2 = dx * dx + dy * dy
            local u = len2 > 0 and ((px - ax) * dx + (py - ay) * dy) / len2 or 0
            if u < 0 then u = 0 elseif u > 1 then u = 1 end
            local ex, ey = ax + u * dx - px, ay + u * dy - py
            local d = ex * ex + ey * ey
            if d < best then best = d end
        end
        return math.sqrt(best)
    end
    if C_Timer and C_Timer.NewTicker then
        C_Timer.NewTicker(2, function()
            local t = nav.target
            if not t or nav.job or InCombatLockdown() then return end
            local px, py, _, inst = UnitPosition("player")
            if not px then return end
            local since = GetTime() - (nav.lastTry or 0)
            if nav.path and inst == t.inst then
                if OffPath(px, py) > 40 and since > 4 then ns.NavRequest() end
            elseif since > 15 then
                ns.NavRequest()                          -- no path yet (or just changed continent): try again
            end
        end)
    end
end

-- Taps on the Edge widget arrive as short key bursts sent by the companion (only when the user enables it):
--   CTRL-SHIFT-F11, then digits as CTRL-SHIFT-F1..F10 (= 0..9), then CTRL-SHIFT-F12.
-- First digit is the action; the rest are its argument. One tap = one interface action (never movement or combat).
--   1<questID>                track the quest (and point the in-game arrow at it where supported)
--   2<questID>                open the quest log at that quest
--   3<mapID:5><x:3><y:3>      set the in-game map waypoint (x, y in thousandths)
--   4                         clear the in-game waypoint
--   7<mapID:5><x:4><y:4>      ask Polestar (if installed) for a walking path to that spot; 70 clears it
--   5<n>                      toggle a window: 1 bags, 2 world map, 3 character, 4 sort bags, 5 quest log
local inputBuf, inputAt
local inputOwner = CreateFrame("Frame")
local function say(text) print("|cff7fd4ffEdgeLink|r " .. text) end
local function QuestIndex(id)
    if C_QuestLog and C_QuestLog.GetLogIndexForQuestID then return C_QuestLog.GetLogIndexForQuestID(id) end
    if GetQuestLogIndexByID then return GetQuestLogIndexByID(id) end
end
local function QuestTitle(id)
    if C_QuestLog and C_QuestLog.GetTitleForQuestID then local t = C_QuestLog.GetTitleForQuestID(id); if t then return t end end
    local i = QuestIndex(id)
    if i and i > 0 and GetQuestLogTitle then return (GetQuestLogTitle(i)) end
    return "quest " .. id
end
function ns.RunCommand(digits)
    local action, arg = digits:sub(1, 1), digits:sub(2)
    if action == "1" or action == "2" then
        local id = tonumber(arg)
        local index = id and QuestIndex(id)
        if not index or index <= 0 then say("that quest is not in your quest log."); return false end
        if action == "1" then
            if C_QuestLog and C_QuestLog.AddQuestWatch then pcall(C_QuestLog.AddQuestWatch, id)
            elseif AddQuestWatch then pcall(AddQuestWatch, index) end
            if C_SuperTrack and C_SuperTrack.SetSuperTrackedQuestID then pcall(C_SuperTrack.SetSuperTrackedQuestID, id) end
            if QuestWatch_Update then pcall(QuestWatch_Update) end
            say("tracking " .. QuestTitle(id) .. ".")
        else
            if SelectQuestLogEntry then pcall(SelectQuestLogEntry, index) end
            if QuestLogFrame and ShowUIPanel and not QuestLogFrame:IsShown() then pcall(ShowUIPanel, QuestLogFrame) end
            if QuestLog_Update then pcall(QuestLog_Update) end
            if QuestMapFrame_OpenToQuestDetails then pcall(QuestMapFrame_OpenToQuestDetails, id) end
        end
        return true
    elseif action == "3" and #arg == 11 then
        local mapID, x, y = tonumber(arg:sub(1, 5)), tonumber(arg:sub(6, 8)) / 1000, tonumber(arg:sub(9, 11)) / 1000
        if not (C_Map and C_Map.SetUserWaypoint and UiMapPoint) then say("this game client has no map waypoints."); return false end
        local ok = pcall(C_Map.SetUserWaypoint, UiMapPoint.CreateFromCoordinates(mapID, x, y))
        if ok and C_SuperTrack and C_SuperTrack.SetSuperTrackedUserWaypoint then pcall(C_SuperTrack.SetSuperTrackedUserWaypoint, true) end
        say(ok and string.format("waypoint set at %.1f, %.1f.", x * 100, y * 100) or "could not set that waypoint.")
        return ok
    elseif action == "4" then
        if C_Map and C_Map.ClearUserWaypoint then pcall(C_Map.ClearUserWaypoint) end
        return true
    elseif action == "7" then
        -- Route target for Polestar's walking router: 7<mapID:5><x:4><y:4> (ten-thousandths), or 70 to clear
        if arg == "0" then return ns.NavSet(nil) end
        if #arg ~= 13 then return false end
        return ns.NavSet(tonumber(arg:sub(1, 5)), tonumber(arg:sub(6, 9)) / 10000, tonumber(arg:sub(10, 13)) / 10000)
    elseif action == "6" then
        -- Open one of your profession windows so its recipes can be read. Not in combat.
        if InCombatLockdown() then say("profession windows can't be opened from the Edge during combat."); return false end
        local id = tonumber(arg)
        if not (id and C_TradeSkillUI and C_TradeSkillUI.OpenTradeSkill) then
            say("open the profession window in game once and its recipes will appear on the Edge."); return false
        end
        local okO, opened = pcall(C_TradeSkillUI.OpenTradeSkill, id)
        if not okO or opened == false then say("could not open that profession from the Edge; open it in game once instead.") end
        return okO
    elseif action == "5" then
        -- Open or close a game window (the same thing its keybind does). Not in combat.
        if InCombatLockdown() then say("windows can't be opened from the Edge during combat."); return false end
        local fn
        if arg == "1" then fn = ToggleAllBags
        elseif arg == "2" then fn = ToggleWorldMap
        elseif arg == "3" then fn = function() if ToggleCharacter then ToggleCharacter("PaperDollFrame") end end
        elseif arg == "4" then fn = function()
                if C_Container and C_Container.SortBags then C_Container.SortBags() elseif SortBags then SortBags() end
            end
        elseif arg == "5" then fn = ToggleQuestLog
        end
        if not fn then return false end
        return (pcall(fn))
    end
    return false
end
local function OnInputKey(sym)
    if sym == "B" then inputBuf, inputAt = {}, GetTime(); return end
    if not inputBuf or GetTime() - inputAt > 3 then inputBuf = nil; return end
    if sym == "E" then
        local digits = table.concat(inputBuf)
        inputBuf = nil
        if #digits > 0 and #digits <= 16 then ns.RunCommand(digits) end
        return
    end
    if #inputBuf < 16 then inputBuf[#inputBuf + 1] = sym end
end
function ns.BindInput()
    if ns.inputReady then return true end
    if InCombatLockdown() then return false end      -- bindings can't change in combat; retried after combat
    local keys = { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12" }
    for i, key in ipairs(keys) do
        local sym = i <= 10 and tostring(i - 1) or (i == 11 and "B" or "E")
        local name = "EdgeLinkInput" .. sym
        local b = _G[name] or CreateFrame("Button", name, UIParent)
        b:Hide()
        b:SetScript("OnClick", function() OnInputKey(sym) end)
        SetOverrideBindingClick(inputOwner, true, "CTRL-SHIFT-" .. key, name)
    end
    ns.inputReady = true
    dirty = true
    return true
end

SLASH_EDGELINK1 = "/edgelink"
SlashCmdList.EDGELINK = function(msg)
    msg = (msg or ""):lower()
    local cmd, arg = msg:match("^(%S*)%s*(.-)$")
    if cmd == "on" or cmd == "off" then
        EdgeLinkDB.enabled = (cmd == "on")
        strip:SetShown(EdgeLinkDB.enabled)
        dirty = true
    elseif cmd == "rate" and tonumber(arg) then
        EdgeLinkDB.pageInterval = math.max(0.03, tonumber(arg))
    elseif cmd == "refresh" then
        for k in pairs(ns.areaCache) do ns.areaCache[k] = nil end
        dirty, sinceRebuild = true, 99
    elseif cmd == "cmd" then
        -- run a widget command by hand, e.g. /edgelink cmd 1<questID>
        if arg:match("^%d+$") then ns.RunCommand(arg) else say("usage: /edgelink cmd <digits>") end
        return
    elseif cmd == "recipes" then
        local okR, profName, list, how = pcall(ns.ReadRecipes)
        if not okR then say("recipe read failed: " .. tostring(profName)); return end
        say(string.format("profession window: %s, %d recipes read via %s.", tostring(profName or "none open"), #(list or {}), tostring(how)))
        local n = ns.ScanRecipes(EdgeLinkDB)
        if n > 0 then dirty = true; say("saved " .. n .. " recipes.") else say("nothing saved. Open a profession window first, then run this again.") end
        local mine = ns.recipeDB and ns.recipeDB[ns.CharKey()] or {}
        for name, l in pairs(mine) do print("  " .. name .. ": " .. #l .. " recipes stored") end
        return
    elseif cmd == "casts" then
        print("|cff7fd4ffEdgeLink|r recent casts on named targets (newest first):")
        if #ns.castLog == 0 then print("  none yet - gather something, then try again") end
        for _, line in ipairs(ns.castLog) do print("  " .. line) end
        return
    elseif cmd == "nodes" then
        local mapID = C_Map and C_Map.GetBestMapForUnit and C_Map.GetBestMapForUnit("player")
        if arg == "clear" and mapID then
            ns.nodeDB[mapID] = nil
            dirty = true
            print("|cff7fd4ffEdgeLink|r gathering nodes for this map cleared.")
            return
        end
        local counts, total = { H = 0, M = 0, F = 0, T = 0 }, 0
        for _, list in pairs(ns.nodeDB) do total = total + #list end
        for _, n in ipairs(mapID and ns.nodeDB[mapID] or {}) do counts[n[1]] = (counts[n[1]] or 0) + 1 end
        print(string.format("|cff7fd4ffEdgeLink|r nodes here: %d herb, %d ore, %d fishing, %d treasure (%d on all maps). '/edgelink nodes clear' forgets this map.",
            counts.H, counts.M, counts.F, counts.T, total))
        return
    elseif cmd == "areas" then
        local mapID = C_Map and C_Map.GetBestMapForUnit and C_Map.GetBestMapForUnit("player")
        local c, total, shaped = mapID and ns.areaCache[mapID] or {}, 0, 0
        for _, runs in pairs(c) do total = total + 1; if runs ~= "" then shaped = shaped + 1 end end
        print(string.format("|cff7fd4ffEdgeLink|r quest areas: %s. Map %s: %d quest(s) sampled, %d with an area, %d waiting.",
            ns.areaSupport == nil and "not tried yet" or (ns.areaSupport and "supported" or "NOT supported by this client"),
            tostring(mapID), total, shaped, #areaQueue))
        return
    end
    local _, physH = GetPhysicalScreenSize()
    print(string.format("|cff7fd4ffEdgeLink|r %s - snapshot %d, %d page(s), %d bytes, page every %.2fs, screen height %d px. Commands: /edgelink on|off|refresh|areas|nodes|casts|recipes|rate <sec>",
        EdgeLinkDB.enabled and "ON" or "OFF", snapshotId, chunks and #chunks or 0,
        lastPayload and #lastPayload or 0, EdgeLinkDB.pageInterval, physH or 0))
end
