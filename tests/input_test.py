# Exercises the addon's key-burst decoder and command runner with stubbed WoW APIs.
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
from roundtrip import STUBS
from lupa.lua51 import LuaRuntime

lua = LuaRuntime()
lua.execute(STUBS.encode())
lua.execute(b"""
UIParent = {}; SlashCmdList = {}
local Frame = {}
Frame.__index = function(t, k)
  if k == "SetScript" then return function(self, n, f) rawset(self, "_s_" .. n, f) end end
  if k == "GetScript" then return function(self, n) return rawget(self, "_s_" .. n) end end
  return function() end
end
function CreateFrame(kind, name)
  local f = setmetatable({}, Frame)
  if name then _G[name] = f end
  return f
end
_bindings = {}; _log = {}; _now = 100
function SetOverrideBindingClick(owner, prio, key, name) _bindings[key] = name end
function InCombatLockdown() return false end
function GetTime() return _now end
function print(s) _log[#_log + 1] = s end
C_QuestLog = C_QuestLog or {}
function C_QuestLog.GetLogIndexForQuestID(id) if id == 783 then return 4 end end
function C_QuestLog.GetTitleForQuestID(id) return "A Threat Within" end
_watched = nil; function C_QuestLog.AddQuestWatch(id) _watched = id end
C_SuperTrack = { SetSuperTrackedQuestID = function(id) _super = id end, SetSuperTrackedUserWaypoint = function(v) _superWP = v end }
UiMapPoint = { CreateFromCoordinates = function(m, x, y) return { m = m, x = x, y = y } end }
C_Map = C_Map or {}
function C_Map.SetUserWaypoint(p) _wp = p end
""")
with open(os.path.join(os.path.dirname(__file__), "..", "addon", "EdgeLink", "EdgeLink.lua"), "rb") as f:
    lua.execute(f.read())
ns = lua.globals().EdgeLink
g = lua.globals()
assert ns.BindInput() is True
b = g._bindings
assert b["CTRL-SHIFT-F1"] == "EdgeLinkInput0" and b["CTRL-SHIFT-F10"] == "EdgeLinkInput9"
assert b["CTRL-SHIFT-F11"] == "EdgeLinkInputB" and b["CTRL-SHIFT-F12"] == "EdgeLinkInputE"

def press(seq):
    for sym in seq:
        lua.execute(("_G['EdgeLinkInput%s']:GetScript('OnClick')()" % sym).encode())

press("B1783E")
assert g._watched == 783 and g._super == 783, (g._watched, g._super)
press("B300012250500E")             # waypoint map 12 at 0.25, 0.5
assert g._wp.m == 12 and abs(g._wp.x - 0.25) < 1e-9 and abs(g._wp.y - 0.5) < 1e-9
g._watched = None
press("1783E")                      # no begin marker -> ignored
assert g._watched is None
lua.execute(b"_now = 100")
press("B1")
lua.execute(b"_now = 110")          # stale burst -> dropped
press("783E")
assert g._watched is None
press("B1999E")                     # quest not in log
assert "not in your quest log" in g._log[len(g._log)]
assert ns.GatherKind("Herbalism") == "H" and ns.GatherKind("Herb Gathering") == "H" and ns.GatherKind("Mining") == "M"
assert ns.GatherKind("Opening") == "T" and ns.GatherKind("Fireball") is None
assert ns.LootKind(7, 9) == "H" and ns.LootKind(7, 7) == "M" and ns.LootKind(2, 7) is None
# --- Polestar walking path: command 7 asks the (stubbed) router and the answer comes back as an L record
lua.execute(b"""
CreateVector2D = function(x, y) return { x = x, y = y, GetXY = function(self) return self.x, self.y end } end
function C_Map.GetWorldPosFromMapPos(mapID, v) return 0, CreateVector2D(1000 - v.y * 1000, 2000 - v.x * 2000) end
function UnitPosition() return 900, 1800, 5, 0 end           -- map (0.1, 0.1)
function UnitFactionGroup() return "Alliance" end
_finds = 0
Polestar_NavEngine = {
  Available = function(map) return map == 0 end,
  Cancel = function() end,
  Find = function(map, from, to, cb, opts)
    _finds = _finds + 1; _from, _to, _opts = from, to, opts
    cb({ { map = 0, x = from.x, y = from.y }, { map = 0, x = 700, y = 1800 }, { map = 0, x = 700, y = 1000 }, { map = 0, x = to.x, y = to.y } }, 123, { yards = 1100 })
    return {}
  end,
}
""")
press("B7014320500" + "0500E")      # map 1432 at 0.05, 0.05 -> wrong length is ignored below; this one is 13 digits
assert g._finds == 1, g._finds
assert abs(g._to.x - 950) < 1e-6 and abs(g._to.y - 1900) < 1e-6 and g._opts.profile == "Alliance"
assert ns.nav.status == "ok" and ns.nav.yards == 1100 and len(ns.nav.path) == 4
out = lua.eval("(function() local o = {}; local ok, e = pcall(function() return EdgeLink.BuildSnapshot() end); return ok and e or ('ERR ' .. tostring(e)) end)()")
line = [l for l in str(out).split("\n") if l.startswith("L\t")]
assert line, str(out)[:300]
f = line[0].split("\t")
assert f[1] == "ok" and f[2] == "1100" and f[3] == "1432", f
pts = [tuple(map(float, q.split(","))) for q in f[6].split(";")]
assert abs(pts[0][0] - 0.1) < 1e-3 and abs(pts[0][1] - 0.1) < 1e-3 and abs(pts[1][1] - 0.3) < 1e-3, pts
press("B70E")
assert ns.nav.target is None
# --- Polestar's own route (its arrow and map lines) is published as a D record, in zone coordinates
lua.execute(b"""
_navTo = nil; _cancelled = nil
Polestar = { Nav = {
  GetTarget = function() return _ptarget end,
  GetTargetWorld = function() if _ptarget then return 0, 700, 1000 end end,
  GetRouteGeometry = function() if _ptarget then return { legs = {
      { kind = "walk", current = true, pts = { {0, 900, 1800}, {0, 700, 1800} } },
      { kind = "flight", pts = { {0, 700, 1800}, {0, 700, 1000}, {1, 5, 5} } } } } end end,
  API = {
    Navigate = function(owner, map, x, y, title) _navTo = { owner = owner, map = map, x = x, y = y, title = title }; _ptarget = { label = title }; return true end,
    Cancel = function(owner) _cancelled = owner; _ptarget = nil; return true end,
  },
} }
_ptarget = { label = "Go to Thelsamar" }
""")
out = str(lua.eval("EdgeLink.BuildSnapshot()"))
d = [l for l in out.split("\n") if l.startswith("D\t")]
assert d, out[:400]
f = d[0].split("\t")
assert f[1] == "Go to Thelsamar" and abs(float(f[2]) - 0.5) < 1e-3 and abs(float(f[3]) - 0.3) < 1e-3, f
legs = f[4].split("|")
assert legs[0].startswith("walk,1,0:") and legs[1].startswith("flight,0,0:"), legs
assert len(legs[1].split(":")[1].split(";")) == 2, legs[1]       # the point on another continent is left out
press("B7014320500" + "0500E")          # Route here now hands the spot to Polestar instead of asking its router
assert g._navTo.owner == "EdgeLink" and g._navTo.map == 1432 and abs(g._navTo.x - 0.05) < 1e-6, g._navTo
assert g._finds == 1 and ns.nav.target is None
press("B70E")
assert g._cancelled == "EdgeLink"
out = str(lua.eval("EdgeLink.BuildSnapshot()"))
assert not [l for l in out.split("\n") if l.startswith("D\t")]
# --- TomTom waypoints on the shown maps come out as W pins (state 0); others are left out
lua.execute(b"""
TomTom = { waypoints = { [1429] = { a = { 1429, 0.25, 0.75, title = "Dig site" } }, [9999] = { b = { 9999, 0.5, 0.5 } } } }
""")
out = str(lua.eval("EdgeLink.BuildSnapshot()"))
w = [l for l in out.split("\n") if "Dig site" in l]
assert w and w[0].startswith("N\t1429\tW\t"), [l for l in out.split("\n") if l.startswith("N") or l.startswith("M")][:8]
z = [l for l in out.split("\n") if l.startswith("Z\t")][0]
assert "T" in z.split("\t")[1].split(","), z
assert not [l for l in out.split("\n") if l.startswith(w[0].split("\t")[0] + "\t9999\t")]
# --- Auctioneer as the auction source when Auctionator is absent
lua.execute(b"""
AucAdvanced = { API = { GetMarketValue = function(id) if id == 2447 then return 1234.6 end end } }
""")
assert ns.AuctionPrice(2447) == 1234 and ns.AuctionPrice(1) is None and ns.HasAuctionSource()
# --- open game windows ride in the live flag byte
ns.WindowEvent("MERCHANT_SHOW"); ns.WindowEvent("TRADE_SKILL_SHOW"); ns.WindowEvent("MERCHANT_SHOW")
assert ns.openWindows == 8 + 32, ns.openWindows
ns.WindowEvent("MERCHANT_CLOSED"); ns.WindowEvent("MERCHANT_CLOSED")
assert ns.openWindows == 32
ns.WindowEvent("TRADE_SKILL_CLOSE")
assert ns.openWindows == 0
# --- header status record: hearth ready time (absolute), mail, repair cost
lua.execute(b"""
function GetBindLocation() return "Thelsamar" end
function GetItemCount(id) return id == 6948 and 1 or 0 end
function GetItemCooldown(id) return 90, 1800, 1 end      -- started at GetTime()=90 for 30 min
_now = 100
time = function() return 1000000 end
function HasNewMail() return true end
C_TooltipInfo = { GetInventoryItem = function(unit, slot) if slot == 5 then return { repairCost = 1530 } end return {} end }
""")
ns.hearthReadyAt = None
out = str(lua.eval("EdgeLink.BuildSnapshot()"))
i = [l for l in out.split("\n") if l.startswith("I\t")]
assert i, out[-400:]
f = i[0].split("\t")
assert f[1] == "Thelsamar" and f[2] == str(1000000 + 1790) and f[3] == "1" and f[4] == "1530", f
# --- trainers and services from (stubbed) Polestar's NPC database
lua.execute(b"""
function UnitClass() return "Warrior", "WARRIOR" end
function UnitFactionGroup() return "Alliance" end
local roles = { ["class:WARRIOR"] = { 10 }, ["trainer:171"] = { 11, 13 }, vendor = { 12, 14 }, repair = { 14 } }
local npcs = {
  [10] = { name = "Bilban", subName = "Warrior Trainer", roles = { "class:WARRIOR" }, spawns = { { map = 1429, x = 25, y = 75 } } },
  [11] = { name = "Ghak", subName = "Alchemy Trainer", roles = { "trainer:171" }, spawns = { { map = 1429, x = 50, y = 50 }, { map = 1429, x = 50.1, y = 50.1 } } },
  [12] = { name = "Kreg", subName = "Trade Supplies", roles = { "vendor" }, faction = "A", spawns = { { map = 1429, x = 60, y = 40 } } },
  [13] = { name = "Horde Guy", subName = "Alchemy Trainer", roles = { "trainer:171" }, faction = "H", spawns = { { map = 1429, x = 10, y = 10 } } },
  [14] = { name = "Smith", subName = "Armorer", roles = { "vendor", "repair" }, spawns = { { map = 1429, x = 70, y = 30 }, { map = 9999, x = 1, y = 1 } } },
}
Polestar.Compat = { MapToWorld = function(map, x, y) if map == 1429 then return 0, 1000 - y * 10, 2000 - x * 20 end end }
Polestar.Codex = {
  IsLoaded = function() return true end,
  WithRole = function(r) return roles[r] or {} end,
  Npc = function(id) return npcs[id] end,
  Item = function(id) if id == 765 then return { sources = { { type = "vendor", npc = 12 } } } end return { sources = {} } end,
}
EdgeLink.recipeDB = { [EdgeLink.CharKey()] = { Alchemy = { { 0, "Minor Healing Potion", 3, 118, 1, { { 2447, 1, "Peacebloom" }, { 765, 1, "Silverleaf" } } } } } }
""")
assert ns.ServicesAvailable()
world = ns.BuildServiceWorld()
cats = sorted(set(world[i].cat for i in range(1, len(world) + 1)))
assert cats == ["class", "prof:171", "repair", "vendor"], cats
ns.svc.world = world
out = str(lua.eval("EdgeLink.BuildSnapshot()"))
a = [l.split("\t") for l in out.split("\n") if l.startswith("A\t")]
assert a and a[0][1] == "-", a
rows = {r[4]: r for r in a[1:]}
assert set(rows) == {"Bilban", "Ghak", "Kreg", "Smith"}, rows.keys()          # Horde trainer left out, Ghak's twin spawn merged
assert rows["Bilban"][1] == "class" and abs(float(rows["Bilban"][2]) - 0.25) < 1e-3 and abs(float(rows["Bilban"][3]) - 0.75) < 1e-3
assert rows["Smith"][1] == "repair" and rows["Kreg"][6] == "765", rows["Kreg"]
assert "S" in [l for l in out.split("\n") if l.startswith("Z\t")][0].split("\t")[1].split(",")
lua.execute(b"EdgeLinkDB = EdgeLinkDB or {}")
assert ns.RunCommand("81") and lua.eval("EdgeLinkDB.services") is True
# --- widget version constant matches the manifest
import json, re
root = os.path.join(os.path.dirname(__file__), "..")
mv = json.load(open(os.path.join(root, "widget", "manifest.json")))["version"]
wv = re.search(r'WIDGET_VERSION = "([^"]+)"', open(os.path.join(root, "widget", "index.html"), encoding="utf-8").read()).group(1)
assert mv == wv, (mv, wv)
print("INPUT OK")
