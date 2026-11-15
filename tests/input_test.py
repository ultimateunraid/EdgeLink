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
print("INPUT OK")
