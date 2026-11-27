local name, ns = ...
local frame = CreateFrame("Frame", "EdgeLinkNativeDashboard", UIParent)
frame:SetSize(1000, 280)
frame:SetPoint("BOTTOM", UIParent, "BOTTOM", 0, 80)
frame:SetClampedToScreen(true)
frame:SetMovable(true)
frame:EnableMouse(true)
frame:RegisterForDrag("LeftButton")
frame:SetScript("OnDragStart", function(self) if not InCombatLockdown() then self:StartMoving() end end)
frame:SetScript("OnDragStop", function(self) self:StopMovingOrSizing() end)
local bg = frame:CreateTexture(nil, "BACKGROUND")
bg:SetAllPoints()
bg:SetColorTexture(0.05, 0.08, 0.12, 0.97)
local title = frame:CreateFontString(nil, "OVERLAY", "GameFontNormalLarge")
title:SetPoint("TOPLEFT", 20, -16)
title:SetText("EdgeLink Native | Preview")
local status = frame:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
status:SetPoint("TOPLEFT", 20, -58)
local content = frame:CreateFontString(nil, "OVERLAY", "GameFontHighlightLarge")
content:SetPoint("TOPLEFT", 20, -90)
content:SetPoint("BOTTOMRIGHT", -20, 20)
content:SetJustifyH("LEFT")
content:SetJustifyV("TOP")
local selected = "Session"
local function safe(fn)
    if type(fn) ~= "function" then return nil end
    local ok, value = pcall(fn)
    if not ok then return nil end
    if issecretvalue and issecretvalue(value) then return nil end
    return value
end
local function refresh()
    if InCombatLockdown() then status:SetText("Paused during combat"); return end
    status:SetText("Native UI | No external telemetry")
    if selected == "Session" then
        local fps = safe(GetFramerate)
        local money = safe(GetMoney)
        local xp = safe(function() return UnitXP("player") end)
        local max = safe(function() return UnitXPMax("player") end)
        content:SetText(string.format("FPS: %s\nGold: %s\nExperience: %s / %s",
            fps and tostring(math.floor(fps)) or "Unavailable",
            money and tostring(math.floor(money / 10000)) or "Unavailable",
            xp and tostring(xp) or "Unavailable", max and tostring(max) or "Unavailable"))
    elseif selected == "Map" then
        local zone = safe(GetRealZoneText)
        content:SetText("Zone: " .. (zone or "Unavailable") .. "\nMap canvas pending API validation.")
    else
        content:SetText("Quest panel pending API validation.")
    end
end
for i, label in ipairs({"Map", "Quests", "Session"}) do
    local tab = label
    local button = CreateFrame("Button", nil, frame, "UIPanelButtonTemplate")
    button:SetSize(110, 30)
    button:SetPoint("TOPRIGHT", -20 - (3-i)*116, -12)
    button:SetText(tab)
    button:SetScript("OnClick", function() selected = tab; refresh() end)
end
local elapsed = 0
frame:SetScript("OnUpdate", function(_, dt)
    elapsed = elapsed + dt
    if elapsed >= 1 then elapsed = 0; refresh() end
end)
SLASH_EDGELINKNATIVE1 = "/edgenative"
SlashCmdList.EDGELINKNATIVE = function()
    if frame:IsShown() then frame:Hide() else frame:Show(); refresh() end
end
frame:Hide()
