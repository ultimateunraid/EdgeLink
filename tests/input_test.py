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
print("INPUT OK")
