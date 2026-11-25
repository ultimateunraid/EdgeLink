"""Round-trip test: Lua encoder (stubbed WoW API) -> pixel strip PNGs -> Python decoder.
Also writes test PNGs for the companion's -TestImage mode."""
import sys, os
from lupa.lua51 import LuaRuntime
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
LUA = os.path.join(HERE, "..", "addon", "EdgeLink", "EdgeLink.lua")
OUT = os.path.join(HERE, "out")
os.makedirs(OUT, exist_ok=True)

STUBS = r"""
NUM_BAG_SLOTS = 4
local bags = { [0] = 16, [1] = 10, [2] = 0, [3] = 6, [4] = 0 }
C_Container = {
  GetContainerNumSlots = function(b) return bags[b] or 0 end,
  GetBagName = function(b) return "Small Brown Pouch #" .. b end,
  GetContainerItemInfo = function(b, s)
    if (b + s) % 3 == 0 then return nil end
    return { itemID = 2500 + b * 100 + s, stackCount = s, quality = (b + s) % 6,
             hyperlink = "|cffffffff|Hitem:" .. (2500 + b*100 + s) .. "::|h[Test Item " .. b .. "-" .. s .. " \226\128\148 Ünïcode]|h|r",
             iconFileID = 133000 + s }
  end,
}
function GetInventoryItemLink(u, slot) if slot % 2 == 1 then return "|Hitem:" .. (19000+slot) .. "|h[Gear Piece " .. slot .. "]|h" end end
function GetInventoryItemID(u, slot) return 19000 + slot end
function GetInventoryItemQuality(u, slot) return 4 end
function GetInventoryItemDurability(slot) return slot * 3, 100 end
function GetInventoryItemTexture(u, slot) return 135000 + slot end
function GetNumQuestLogEntries() return 4 end
local q = {
  { "Elwynn Forest", 1, nil, true },
  { "A Threat Within", 5, nil, false, false, nil, nil, 783 },
  { "Kobold Camp Cleanup", 7, nil, false, false, 1, nil, 7 },
  { "Westfall", 1, nil, true },
}
function GetQuestLogTitle(i) local t = q[i]; return t[1], t[2], t[3], t[4], t[5], t[6], t[7], t[8] end
function GetNumQuestLeaderBoards(i) if i == 3 then return 2 end return 0 end
function GetQuestLogLeaderBoard(j, i) if j == 1 then return "Kobold Vermin slain: 10/10", "monster", true end return "Tab\there: 0/1", "item", false end
C_Map = {
  GetBestMapForUnit = function() return 1429 end,
  GetPlayerMapPosition = function() return { GetXY = function() return 0.4231, 0.6512 end } end,
  GetMapInfo = function(id)
    local t = { [1429] = { name = "Elwynn Forest", mapType = 3, parentMapID = 1415 },
                [1415] = { name = "Eastern Kingdoms", mapType = 2, parentMapID = 947 },
                [947]  = { name = "Azeroth", mapType = 1, parentMapID = 946 } }
    return t[id]
  end,
  GetMapRectOnMap = function(child, parent)
    if parent == 1415 then return 0.4, 0.5, 0.75, 0.82 end
    return 0.7, 0.75, 0.6, 0.65
  end,
}
function GetPlayerFacing() return math.pi / 2 end
function UnitFactionGroup() return "Alliance", "Alliance" end
C_TaxiMap = { GetTaxiNodesForMap = function(m)
  if m == 1429 then return { { name = "Goldshire", position = { x = 0.42, y = 0.66 }, state = 2, faction = 2 },
                              { name = "Off map", position = { x = 1.5, y = 0.2 }, state = 1, faction = 0 } } end
  return { { name = "Stormwind, Elwynn", position = { GetXY = function() return 0.42, 0.72 end }, state = 1, faction = 2 },
           { name = "Ironforge, Dun Morogh", position = { x = 0.47, y = 0.58 }, state = 1, faction = 2 } } end }
C_AreaPoiInfo = { GetAreaPOIForMap = function(m) return { 7 } end,
                  GetAreaPOIInfo = function(m, id) return { name = "Lion's Pride Inn", position = { x = 0.43, y = 0.65 } } end }
C_QuestLog_GetQuestsOnMap = nil
function UnitHealthMax() return 411 end
function UnitPowerType() return 1, "RAGE" end
RAGE = "Rage"
function UnitPowerMax() return 100 end
function GetUnitSpeed() return 0, 7, 7, 4.72 end
SPELL_STAT1_NAME, SPELL_STAT2_NAME, SPELL_STAT3_NAME, SPELL_STAT4_NAME, SPELL_STAT5_NAME = "Strength", "Agility", "Stamina", "Intellect", "Spirit"
local stats = { {44, 44, 3, 0}, {31, 31, 0, 0}, {44, 44, 2, 0}, {24, 24, 0, 0}, {27, 27, 0, 0} }
function UnitStat(u, i) local t = stats[i]; return t[1], t[2], t[3], t[4] end
function UnitDamage() return 36.2, 51.4, 0, 0, 0, 0, 1 end
function UnitAttackSpeed() return 2.9 end
function UnitAttackPower() return 100, 16, 0 end
function GetCritChance() return 5.1234 end
function UnitRangedDamage() return 0, 0, 0 end
function UnitRangedAttackPower() return 58, 0, 0 end
function GetRangedCritChance() return 0 end
function GetSpellBonusDamage() return 0 end
function GetSpellBonusHealing() return 0 end
function UnitArmor() return 300, 342 end
function GetDodgeChance() return 4.2 end
function GetParryChance() return 5 end
function GetBlockChance() return 0 end
function UnitResistance(u, i) return 0, i == 2 and 10 or 0, 0, 0 end
function GetPhysicalScreenSize() return 3440, 1440 end
TRADE_SKILLS, SECONDARY_SKILLS = "Professions", "Secondary Skills"
local skills = { {"Professions", true}, {"Herbalism", false, 75, 150}, {"Mining", false, 40, 75},
                 {"Secondary Skills", true}, {"First Aid", false, 20, 75}, {"Fishing", false, 1, 75},
                 {"Weapon Skills", true}, {"Swords", false, 80, 80} }
function GetNumSkillLines() return #skills end
function GetSkillLineInfo(i) local t = skills[i]; return t[1], t[2], true, t[3], 0, 0, t[4] end
-- bag items: odd slots are herbs (7/9), even slots ore (7/7), slot 4 is not a trade good
function GetItemInfoInstant(id)
  local slot = id % 100
  if slot == 4 then return id, "Armor", "Cloth", "INVTYPE_CHEST", 0, 4, 1 end
  if slot % 2 == 1 then return id, "Trade Goods", "Herb", "", 0, 7, 9 end
  return id, "Trade Goods", "Metal & Stone", "", 0, 7, 7
end
local shown = function() return true end
CharacterFrame = { IsVisible = shown }
CharacterModelScene = { IsVisible = shown, GetLeft = function() return 100 end, GetTop = function() return 700 end,
  GetWidth = function() return 230 end, GetHeight = function() return 300 end, GetEffectiveScale = function() return 1.0 end }
-- Fake QuestPOIFrame: quest 7 has a round area centred at (0.30, 0.40), radius 0.15 (in map units)
FakePOI = { drawn = {} }
function FakePOI:SetMapID(m) self.map = m end
function FakePOI:DrawNone() self.drawn = {} end
function FakePOI:DrawBlob(q, d) self.drawn[q] = d or nil end
function FakePOI:UpdateMouseOverTooltip(x, y)
  if self.drawn[7] and ((x - 0.30) ^ 2 + ((y - 0.40) * (2/3)) ^ 2) <= 0.15 ^ 2 then return 7, 1 end
  return nil
end
function UnitClass() return "Warrior", "WARRIOR" end
function UnitRace() return "Human" end
function UnitName() return "Testchar" end
function GetRealmName() return "Forever-Realm" end
function UnitLevel() return 12 end
function GetMoney() return 123456 end
function GetZoneText() return "Elwynn Forest" end
function GetSubZoneText() return "Goldshire" end
function GetServerTime() return 1763060000 end
"""

def decode_strip(img, block_px, blocks):
    """Mirror of the companion's v2 decoder. Returns (sid, pageIdx, count, payload, pos)."""
    px = img.load()
    data = bytearray()
    c = block_px // 2
    for i in range(blocks):
        r, g, b = px[i * block_px + c, c][:3]
        data += bytes((r, g, b))
    if data[0] != 0xE7 or data[1] != 0x1D:
        raise ValueError("bad magic")
    if data[2] != 2:
        raise ValueError("bad version")
    sid, pidx, cnt = data[3], data[4], data[5]
    plen = data[6] * 256 + data[7]
    want = data[8] * 256 + data[9]
    s1 = s2 = 0
    for x in data[10:18 + plen]:
        s1 = (s1 + x) % 255
        s2 = (s2 + s1) % 255
    if s2 * 256 + s1 != want:
        raise ValueError("checksum")
    pos = dict(mapID=data[10]*256+data[11], x=(data[12]*256+data[13])/65535, y=(data[14]*256+data[15])/65535,
               facing=data[16], flags=data[17])
    return sid, pidx, cnt, bytes(data[18:18 + plen]), pos

def render(page, blocks, bpx, path):
    img = Image.new("RGB", (blocks * bpx + 40, bpx + 20), (30, 60, 90))
    pxs = img.load()
    for i in range(blocks):
        rgb = (page[i*3+1], page[i*3+2], page[i*3+3])
        for dx in range(bpx):
            for dy in range(bpx):
                pxs[i*bpx+dx, dy] = rgb
    img.save(path)

def main():
    lua = LuaRuntime(unpack_returned_tuples=True, encoding=None)
    lua.execute(STUBS.encode())
    with open(LUA, "rb") as f:
        lua.execute(f.read())
    ns = lua.globals().EdgeLink
    P = ns.Protocol
    blocks, bpx = P.BLOCKS, P.BLOCK_PX
    snap = ns.BuildSnapshot()
    assert isinstance(snap, bytes)
    chunks = ns.SplitPayload(snap)
    n = len(chunks)
    pos = ns.UpdateLivePos(False)
    print(f"snapshot {len(snap)} bytes -> {n} page(s); live pos map={pos.mapID} x={pos.x} y={pos.y} flags={pos.flags}")
    got = {}
    for p in range(1, n + 1):
        page = ns.EncodePage(chunks[p], 42, p - 1, n, pos)
        path = os.path.join(OUT, f"page{p-1}.png")
        render(page, blocks, bpx, path)
        sid, pidx, cnt, payload, dpos = decode_strip(Image.open(path).convert("RGB"), bpx, blocks)
        assert sid == 42 and cnt == n and pidx == p - 1
        assert dpos["mapID"] == 1429 and abs(dpos["x"] - 0.4231) < 1e-4 and abs(dpos["y"] - 0.6512) < 1e-4
        assert dpos["flags"] == 1 and dpos["facing"] == 63, dpos   # pi/2 -> 63/255
        got[pidx] = payload
    joined = b"".join(got[i] for i in range(n))
    assert joined == snap, "payload mismatch"
    text = joined.decode("utf-8")
    kinds = {}
    for line in text.split("\n"):
        kinds[line.split("\t")[0]] = kinds.get(line.split("\t")[0], 0) + 1
    print("records:", kinds)
    qline = [l for l in text.split("\n") if l.startswith("Q\t7\t")][0]
    objs = qline.split("\t")[5].split("\x1f")
    assert objs == ["1Kobold Vermin slain: 10/10", "0Tab here: 0/1"], objs
    mlines = [l.split("\t") for l in text.split("\n") if l.startswith("M\t")]
    assert [m[1] for m in mlines] == ["1429", "1415", "947"], mlines
    assert mlines[1][6:10] == ["0.40000", "0.50000", "0.75000", "0.82000"], mlines[1]
    print("map hierarchy ok:", [(m[1], m[4], m[5]) for m in mlines])
    nlines = [l.split("\t") for l in text.split("\n") if l.startswith("N\t")]
    print("pins:", [(n[1], n[2], n[5]) for n in nlines])
    assert ("1429", "F", "Goldshire") in [(n[1], n[2], n[5]) for n in nlines]
    assert not any(n[5] == "Off map" for n in nlines), "off-map pin must be dropped"
    assert sum(1 for n in nlines if n[2] == "A") == 2  # zone + continent
    zline = [l for l in text.split("\n") if l.startswith("Z\t")][0]
    assert zline == "Z\tF,A", zline   # no EncounterJournal / GetQuestsOnMap in this client
    pline = [l for l in text.split("\n") if l.startswith("P\t")][0].split("\t")
    assert pline[9] == "Alliance", pline
    print("caps:", zline.split("\t")[1], " faction:", pline[9])
    tl = [l.split("\t") for l in text.split("\n") if l.startswith("T\t")]
    tmap = {(t[1], t[2]): (t[3], t[4]) for t in tl}
    assert tmap[("General", "Health")] == ("411", "") and tmap[("General", "Rage")] == ("100", "")
    assert tmap[("General", "Movement Speed")][0] == "100%"
    assert tmap[("Primary Attributes", "Strength")] == ("44", "g") and tmap[("Primary Attributes", "Agility")] == ("31", "")
    assert tmap[("Melee", "Damage")][0] == "36 - 51" and tmap[("Melee", "Attack Power")] == ("116", "g")
    assert tmap[("Melee", "Crit Chance")][0] == "5.12%" and tmap[("Defense", "Armor")][0] == "342"
    assert ("Spell", "Bonus Damage") not in tmap, "no spell stats for a rage user without spell power"
    assert ("Defense", "Block") not in tmap and tmap[("Defense", "Parry")][0] == "5.00%"
    assert ("Ranged", "Attack Power") in tmap and ("Ranged", "Crit Chance") not in tmap
    print("stats:", len(tl), "rows ok")
    assert not any(l.startswith("C\t") for l in text.split("\n")), "character capture was removed"
    kl = [l.split("\t") for l in text.split("\n") if l.startswith("K\t")]
    assert [k[1] for k in kl] == ["Herbalism", "Mining", "First Aid", "Fishing"], kl   # no weapon skills
    assert kl[0][2:4] == ["75", "150"]
    bl = [l.split("\t") for l in text.split("\n") if l.startswith("B\t")]
    assert all(len(b) == 12 for b in bl), bl[0]
    assert any(b[8:10] == ["7", "9"] for b in bl) and any(b[8:10] == ["7", "7"] for b in bl)
    print("skills + material classes ok:", [(k[1], k[2], k[3]) for k in kl])
    # Gathering nodes
    assert ns.GatherKind(b"Herb Gathering") == b"H" and ns.GatherKind(b"Mining") == b"M"
    assert ns.GatherKind(b"Opening") == b"T" and ns.GatherKind(b"Fireball") is None
    db = lua.eval("{}")
    rg = ns.RecordGather
    rg(db, b"H", b"Peacebloom", 1429, 0.400, 0.500)
    rg(db, b"H", b"Peacebloom", 1429, 0.404, 0.503)    # same spot (within 0.01) -> merged
    rg(db, b"H", b"Peacebloom", 1429, 0.600, 0.300)    # different spot
    rg(db, b"H", b"Silverleaf", 1429, 0.401, 0.501)    # different herb at same spot -> separate
    rg(db, b"M", b"Copper Vein", 1429, 0.700, 0.700)
    rg(db, b"F", b"Fishing spot", 1429, 0.300, 0.300)
    rg(db, b"F", b"Fishing spot", 1429, 0.315, 0.300)  # fishing merges within 0.02
    lst = db[1429]
    assert len(lst) == 5, len(lst)
    assert lst[1][5] == 2 and abs(lst[1][3] - 0.402) < 1e-9, (lst[1][3], lst[1][5])
    assert lst[5][5] == 2
    lua.globals().EdgeLink.nodeDB = db
    t3 = ns.BuildSnapshot().decode()
    wl = [l.split("\t") for l in t3.split("\n") if l.startswith("W\t")]
    assert [(w[1], w[2]) for w in wl] == [("F", "Fishing spot"), ("H", "Peacebloom"), ("H", "Silverleaf"), ("M", "Copper Vein")], wl
    assert wl[1][3] == "0.402,0.502,2;0.600,0.300,1", wl[1]
    lua.globals().EdgeLink.nodeDB = lua.eval("{}")
    print("gathering nodes ok:", [(w[1], w[2], w[3]) for w in wl])
    # Quest area sampling against the fake QuestPOIFrame
    poi = lua.globals().FakePOI
    runs = ns.SampleArea(poi, 7, 1429)
    assert runs and runs != "", runs
    nums = [int(v) for v in runs.decode().split(".")]
    assert sum(nums) == ns.AREA_W * ns.AREA_H, sum(nums)
    mask, val = [], 0
    for n in nums:
        mask += [val] * n; val ^= 1
    W = ns.AREA_W
    cells = sum(mask)
    cx = sum((i % W + 0.5) / W for i, v in enumerate(mask) if v) / cells
    cy = sum((i // W + 0.5) / ns.AREA_H for i, v in enumerate(mask) if v) / cells
    assert abs(cx - 0.30) < 0.02 and abs(cy - 0.40) < 0.02, (cx, cy)
    assert ns.SampleArea(poi, 783, 1429) == b"", "quest without an area must give an empty mask"
    assert not poi.drawn[7], "blob must be cleared after sampling"
    print(f"area sampling ok: {cells} cells, centre ({cx:.3f}, {cy:.3f}), runs '{runs.decode()[:40]}...'")
    ac = ns.areaCache
    lua.execute(b"EdgeLink.areaCache[1429] = { [783] = '', [7] = '" + runs + b"', [5] = '0.1.1535' }")
    t2 = ns.BuildSnapshot().decode()
    rl = [l.split("\t") for l in t2.split("\n") if l.startswith("R\t")]
    assert [r[1] for r in rl] == ["5", "7"], rl            # sorted, empty masks omitted
    assert rl[1][2:5] == ["1429", "48", "32"]
    assert ns.BuildSnapshot() == ns.BuildSnapshot(), "payload must be deterministic"
    print("R records ok:", [(r[1], r[2]) for r in rl])
    # Combat freezes position and sets flag 2
    cpos = ns.UpdateLivePos(True)
    assert cpos.flags == 3, cpos.flags
    # Corruption anywhere (including position bytes) must be detected
    for bad_block in (4, 30):
        img = Image.open(os.path.join(OUT, "page0.png")).convert("RGB")
        img.putpixel((bad_block * bpx + 2, 2), (1, 2, 3))
        try:
            decode_strip(img, bpx, blocks)
            print("ERROR: corruption not detected"); sys.exit(1)
        except ValueError as e:
            print(f"corruption in block {bad_block} detected:", e)
    big = snap * 6
    bc = ns.SplitPayload(big)
    assert b"".join(bc[i] for i in range(1, len(bc) + 1)) == big
    print(f"multi-page ok: {len(big)} bytes in {len(bc)} pages")
    # Friends and group (F records)
    assert not any(l.startswith("F\t") for l in text.split("\n")), "no friends API in the base stubs -> no F records"
    lua.execute(b"""
        local realGBM, realGPMP = C_Map.GetBestMapForUnit, C_Map.GetPlayerMapPosition
        local spots = { party1 = { 1429, 0.5, 0.25 }, party2 = { 1426, 0.1, 0.9 } }   -- party3 in a dungeon: no spot
        C_Map.GetBestMapForUnit = function(unit)
            if unit == "player" then return realGBM(unit) end
            return spots[unit] and spots[unit][1] or nil
        end
        C_Map.GetPlayerMapPosition = function(mapID, unit)
            if unit == "player" then return realGPMP(mapID, unit) end
            local s = spots[unit]
            if not s or s[1] ~= mapID then return nil end
            return { GetXY = function() return s[2], s[3] end }
        end
        IsInRaid = function() return false end
        IsInGroup = function() return true end
        local members = { party1 = { "Brax", nil, "WARRIOR", 30 }, party2 = { "Lyra", "Otherrealm", "MAGE", 31 },
                          party3 = { "Tank", nil, "PALADIN", 29 } }
        UnitExists = function(u) return members[u] ~= nil end
        UnitIsUnit = function() return false end
        UnitIsConnected = function(u) return u ~= "party3" end
        local realUnitName, realUnitClass, realUnitLevel = UnitName, UnitClass, UnitLevel
        UnitName = function(u) local m = members[u]; if m then return m[1], m[2] end; return realUnitName(u) end
        UnitClass = function(u) local m = members[u]; if m then return m[3], m[3] end; return realUnitClass(u) end
        UnitLevel = function(u) local m = members[u]; if m then return m[4] end; return realUnitLevel(u) end
        LOCALIZED_CLASS_NAMES_MALE = { DRUID = "Druid", HUNTER = "Hunter" }
        C_FriendList = { GetNumFriends = function() return 3 end, GetFriendInfoByIndex = function(i)
            return ({ { name = "zed", connected = true, className = "Druid", level = 40, area = "Westfall", afk = true },
                      { name = "Offline", connected = false },
                      { name = "Brax", connected = true, className = "Warrior", level = 30, area = "Elwynn Forest" } })[i] end }
        BNET_CLIENT_WOW = "WoW"; WOW_PROJECT_ID = 5
        BNGetNumFriends = function() return 3 end
        C_BattleNet = { GetFriendAccountInfo = function(i)
            return ({ { battleTag = "Pal#1234", accountName = "Real Name", gameAccountInfo = { isOnline = true, clientProgram = "WoW",
                          characterName = "Arwen", realmName = "", areaName = "Duskwood", className = "Hunter", characterLevel = 25, wowProjectID = 5 } },
                      { battleTag = "Other#9", gameAccountInfo = { isOnline = true, clientProgram = "WoW", characterName = "Retailguy",
                          realmName = "Area 52", areaName = "Dornogal", className = "Evoker", characterLevel = 80, wowProjectID = 1 } },
                      { battleTag = "App#1", gameAccountInfo = { isOnline = true, clientProgram = "App" } } })[i] end }
    """)
    tf = ns.BuildSnapshot().decode()
    fl = [l.split("\t") for l in tf.split("\n") if l.startswith("F\t")]
    assert all(len(r) == 11 for r in fl), fl
    names = [(r[1], r[2]) for r in fl]
    assert names == [("G", "Brax"), ("G", "Lyra-Otherrealm"), ("G", "Tank"), ("B", "Arwen"), ("B", "Retailguy-Area 52"), ("R", "zed")], names
    brax, lyra, tank, arwen, retail, zed = fl
    assert brax[3:8] == ["WARRIOR", "30", "1429", "0.500", "0.250"], brax
    assert lyra[5:8] == ["1426", "0.100", "0.900"], lyra          # their own zone, not ours
    assert tank[5:8] == ["", "", ""] and tank[9] == "o", tank         # offline: no spot
    assert arwen[3] == "HUNTER" and arwen[8] == "Duskwood" and arwen[10] == "Pal", arwen
    assert "Real Name" not in tf, "Real ID account names must never be read"
    assert retail[9] == "x", retail                                  # other WoW version
    assert zed[3] == "DRUID" and zed[8] == "Westfall" and zed[9] == "a", zed
    assert not any(r[2] == "Offline" for r in fl)
    assert "G" in [l for l in tf.split("\n") if l.startswith("Z\t")][0].split("\t")[1].split(",")
    assert ns.BuildSnapshot().decode() == tf, "friends must not break determinism"
    sig1 = ns.GroupSignature()
    lua.execute(b"EdgeLinkDB = EdgeLinkDB or {}; EdgeLinkDB.friends = false")
    assert not any(l.startswith("F\t") for l in ns.BuildSnapshot().decode().split("\n")), "/edgelink friends off"
    lua.execute(b"EdgeLinkDB.friends = nil")
    assert sig1 and b"1429" in sig1, sig1
    print("friends ok:", [(r[1], r[2], r[6], r[8]) for r in fl])
    with open(os.path.join(OUT, "snapshot.txt"), "wb") as f:
        f.write(snap)
    print("ALL OK")

if __name__ == "__main__":
    main()

def preview():
    """Preview pages for the widget: same stub data plus two quest areas and area support."""
    import math
    lua = LuaRuntime(unpack_returned_tuples=True, encoding=None)
    lua.execute(STUBS.encode())
    with open(LUA, "rb") as f:
        lua.execute(f.read())
    ns = lua.globals().EdgeLink
    W, H = ns.AREA_W, ns.AREA_H
    def runs_for(shapes):
        bits = []
        for gy in range(H):
            for gx in range(W):
                x, y = (gx + .5) / W, (gy + .5) / H
                bits.append(1 if any(((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2 <= 1 for cx, cy, rx, ry in shapes) else 0)
        out, cur, n = [], 0, 0
        for b in bits:
            if b == cur: n += 1
            else: out.append(n); cur = b; n = 1
        out.append(n)
        return ".".join(map(str, out))
    kobold = runs_for([(0.62, 0.42, 0.07, 0.09), (0.70, 0.55, 0.05, 0.06), (0.36, 0.50, 0.04, 0.05)])
    threat = runs_for([(0.47, 0.70, 0.03, 0.04)])
    lua.execute(("EdgeLink.areaSupport = true; EdgeLink.areaCache[1429] = { [7] = '%s', [783] = '%s' }" % (kobold, threat)).encode())
    lua.execute(b"""
      local db, rg = {}, EdgeLink.RecordGather
      for _, p in ipairs({ {0.30,0.55}, {0.52,0.40}, {0.61,0.62}, {0.70,0.48}, {0.45,0.75}, {0.38,0.33} }) do rg(db, "H", "Peacebloom", 1429, p[1], p[2]) end
      for _, p in ipairs({ {0.33,0.60}, {0.58,0.70} }) do rg(db, "H", "Silverleaf", 1429, p[1], p[2]) end
      rg(db, "H", "Peacebloom", 1429, 0.302, 0.551); rg(db, "H", "Peacebloom", 1429, 0.301, 0.549)
      for _, p in ipairs({ {0.80,0.35}, {0.85,0.62}, {0.24,0.82} }) do rg(db, "M", "Copper Vein", 1429, p[1], p[2]) end
      rg(db, "F", "Fishing spot", 1429, 0.47, 0.64); rg(db, "T", "Battered Chest", 1429, 0.66, 0.22)
      EdgeLink.nodeDB = db""")
    snap = ns.BuildSnapshot()
    chunks = ns.SplitPayload(snap)
    pos = ns.UpdateLivePos(False)
    for p in range(1, len(chunks) + 1):
        render(ns.EncodePage(chunks[p], 77, p - 1, len(chunks), pos), ns.Protocol.BLOCKS, ns.Protocol.BLOCK_PX, os.path.join(OUT, f"preview{p-1}.png"))
    print(f"preview: {len(chunks)} pages written ({len(snap)} bytes)")

if __name__ == "__main__":
    preview()
