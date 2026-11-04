-- Written by Portalkeeper while "Send Lua errors to the realm" is on in its Settings, and deleted by
-- it when the option is off. Changes made here are overwritten.
--
-- Records the game's Lua errors and blocked actions in PortalkeeperErrorsDB. After the game closes
-- Portalkeeper sends them to the realm and deletes the file. The folder starts with "!" so this loads
-- before other addons and sees the errors they raise while loading.
--
-- What is kept is small on purpose: each distinct error once with a count, a short stack and the top
-- level of the failing function's locals, without table contents or the text of strings (they can hold
-- chat and other players' names).
--
-- The game's own error window can feed itself: setting its text runs ScrollingEdit_OnUpdate, which fails
-- on a nil cursorOffset when no cursor event came first, and that error asks the window to update again,
-- until the C stack overflows. So the window only gets the first sighting of an error, a few per login
-- and never an error it raised itself. Only frame scripts have locals: the client gives debuglocals none
-- for errors passed on through xpcall (Ace3 and the like).

local ADDON = "!PortalkeeperErrors"
local FORMAT = 1
local MAX_ERRORS = 100        -- distinct errors kept until they're sent
local MAX_SESSIONS = 20       -- logins kept; later ones share the last
local MAX_MESSAGE = 1000      -- bytes
local MAX_STACK = 1500        -- bytes
local MAX_LOCALS = 1500       -- bytes
local MAX_LOCAL_LINES = 30
local MAX_LOCAL_LINE = 160    -- bytes
local MAX_SHOWN = 10          -- errors passed on to the game's own error window, per login
local MAX_PER_SECOND = 20     -- errors looked at per second; past it they're only counted
local BUSY_SECONDS = 5        -- how long one error being handled can hold others off

local find, gmatch, gsub, match, sub = string.find, string.gmatch, string.gsub, string.match, string.sub
local concat = table.concat
local floor = math.floor
local debugstack, debuglocals = debugstack, debuglocals
local pcall, type, tostring, time, GetTime = pcall, type, tostring, time, GetTime

local db = { format = FORMAT, errors = {}, sessions = {}, dropped = 0, suppressed = 0 }
local index = {}              -- what Key() makes => the error's entry in db.errors
local seen = {}               -- what Key() makes => true, for errors met since this UI loaded
local session                 -- this login's entry in db.sessions
local sessionFull = false     -- past MAX_SESSIONS: the last entry is another login's, left as it is
local shown = 0

-- Addresses differ between two sightings of one error.
local function Normalize(text)
    text = gsub(text, "(%a+: )%x%x%x%x%x%x+", "%1?")
    return (gsub(text, "0x%x+", "0x?"))
end

local function Key(message, stack)
    local top = match(stack, "^[^\n]*\n?[^\n]*\n?[^\n]*") or ""
    return Normalize(message) .. "\n" .. Normalize(top)
end

local function AddonList()
    local list = {}
    for i = 1, GetNumAddOns() do
        if IsAddOnLoaded(i) then
            local name = GetAddOnInfo(i)
            local version = GetAddOnMetadata(i, "Version")
            list[#list + 1] = version and (name .. " " .. tostring(version)) or name
        end
    end
    return list
end

local function FillSession(entry)
    entry.character = UnitName("player")
    entry.realm = GetRealmName()
    local version, build = GetBuildInfo()
    entry.build = tostring(version) .. "." .. tostring(build)
    entry.locale = GetLocale()
    entry.addons = AddonList()
end

-- This login's place in db.sessions, made at its first error. A login like the one before it (a reload)
-- shares its entry, and past the limit every login shares the last.
local function SessionIndex()
    if session then
        for i = #db.sessions, 1, -1 do
            if db.sessions[i] == session then return i end
        end
    end
    local entry = { started = time() }
    FillSession(entry)
    local count = #db.sessions
    local last = db.sessions[count]
    if last and count >= MAX_SESSIONS then
        session, sessionFull = last, true
        return count
    end
    if last and last.character == entry.character and last.realm == entry.realm
        and type(last.addons) == "table" and concat(last.addons, "\n") == concat(entry.addons, "\n") then
        session = last
        return count
    end
    db.sessions[count + 1] = entry
    session = entry
    return count + 1
end

-- The stack below this file and the level of the function that failed, seen from here. Going by the
-- lines instead of a fixed level means another pcall on the way here can't shift them. In the client a
-- frame script's error comes here through an unnamed C function ("[C]: ?"), and a failing C function
-- (error, assert) or a tail call sits above the function that matters: those are stepped over.
local function ReadStack()
    local stack = debugstack(1, 16, 2) or ""
    local lines, level = {}, 0
    local inFile, above = true, true
    for line in gmatch(stack, "[^\n]+") do
        if inFile and (find(line, "PortalkeeperErrors", 1, true) or find(line, "^%[C%]: in function .pcall.")) then
            level = level + 1
        else
            inFile = false
            local isC = sub(line, 1, 4) == "[C]:"
            if above then level = level + 1 end
            if above and not isC and sub(line, 1, 11) ~= "(tail call)" then above = false end
            if not above or (isC and find(line, "in function", 1, true)) then lines[#lines + 1] = line end
        end
    end
    return sub(concat(lines, "\n"), 1, MAX_STACK), level
end

-- The top level of the failing function's locals, one line each. Table contents are indented and closed
-- by a line starting with "}": those lines are skipped, so a table is one line whatever it holds. A
-- string becomes <string>, along with any further lines it runs over.
local function ReadLocals(level)
    if type(debuglocals) ~= "function" then return "" end
    local text = debuglocals(level)
    if type(text) ~= "string" then return "" end
    local lines, size, inString = {}, 0, false
    for line in gmatch(text, "[^\n]+") do
        if inString then
            inString = sub(line, -1) ~= '"'
        elseif not find(line, "^[%s}]") then
            local name, value = match(line, "^(.-) = (.*)$")
            if name then
                if sub(value, 1, 1) == '"' then
                    -- The client cuts a line at 1024 bytes, closing quote and all.
                    inString = (#value == 1 or sub(value, -1) ~= '"') and #line < 1000
                    value = "<string>"
                else
                    value = gsub(value, "%s*{%s*$", "")
                end
                local kept = sub(name .. " = " .. value, 1, MAX_LOCAL_LINE)
                size = size + #kept + 1
                if #lines >= MAX_LOCAL_LINES or size > MAX_LOCALS then
                    lines[#lines + 1] = "..."
                    break
                end
                lines[#lines + 1] = kept
            end
        end
    end
    return concat(lines, "\n")
end

-- Adds to the error already kept for this key, or keeps a new one.
local function Record(kind, message, stack, key, locals)
    local now = time()
    local entry = index[key]
    if entry then
        entry.count = entry.count + 1
        entry.last = now
        return
    end
    if #db.errors >= MAX_ERRORS then
        db.dropped = db.dropped + 1
        return
    end
    entry = { kind = kind, message = message, stack = stack, locals = locals and locals(), count = 1,
        first = now, last = now, session = SessionIndex() }
    db.errors[#db.errors + 1] = entry
    index[key] = entry
end

-- Whether the error window may have this one: the first since the UI loaded, and not its own.
local function Capture(message)
    message = sub(tostring(message), 1, MAX_MESSAGE)
    local stack, level = ReadStack()
    local key = Key(message, stack)
    -- ReadLocals runs two levels further down than ReadStack did: inside Record's call of this function.
    Record("error", message, stack, key, function()
        local locals = ReadLocals(level + 2)
        return locals
    end)
    local first = not seen[key]
    seen[key] = true
    return first and not (find(stack, "Blizzard_DebugTools", 1, true) or find(message, "Blizzard_DebugTools", 1, true)
        or find(message, "cursorOffset", 1, true))
end

local stock = geterrorhandler()
local previous = stock
local before                  -- what previous was until the last addon replaced it
local busyAt                  -- GetTime when the error being handled came in
local handling                -- its message
local windowOwed = false      -- the error being handled may still go to the error window
local windowReady = false
local second, inSecond = 0, 0

-- The window reads locals at a fixed level from its own function; the two frames between the handler it
-- expects and the failing function here are this file's pcall and Handler. Only its own addon reads that.
local function PrepareWindow()
    if windowReady then return end
    windowReady = true
    pcall(LoadAddOn, "Blizzard_DebugTools")
    if type(DEBUGLOCALS_LEVEL) == "number" then DEBUGLOCALS_LEVEL = DEBUGLOCALS_LEVEL + 2 end
end

local function Handler(message)
    local now = GetTime()
    if busyAt and now - busyAt >= 0 and now - busyAt < BUSY_SECONDS then
        -- Called while an error is being handled: by an addon that chained to this handler the usual way
        -- (orig = geterrorhandler() before seterrorhandler), passing the error on or adding to its text,
        -- which is where the window's turn comes, or by a new error, which stops here whoever raised it.
        local chained = message == handling or (type(message) == "string" and type(handling) == "string"
            and find(message, handling, 1, true))
        if not chained then
            db.suppressed = db.suppressed + 1
        elseif windowOwed then
            windowOwed = false
            shown = shown + 1
            PrepareWindow()
            pcall(stock, message)
        end
        return message
    end
    local whole = floor(now)
    if whole ~= second then second, inSecond = whole, 0 end
    inSecond = inSecond + 1
    if inSecond > MAX_PER_SECOND then
        db.suppressed = db.suppressed + 1
        return message
    end

    busyAt, handling = now, message
    local ok, first = pcall(Capture, message)
    windowOwed = (first or not ok) and shown < MAX_SHOWN
    if previous ~= stock then
        -- A handler some other addon set expects every error and has its own limits.
        pcall(previous, message)
    elseif windowOwed then
        windowOwed = false
        shown = shown + 1
        PrepareWindow()
        pcall(stock, message)
    end
    windowOwed = false
    busyAt, handling = nil, nil
    return message
end

local setHandler = seterrorhandler
setHandler(Handler)
-- Stay first: a handler another addon sets is called from here. geterrorhandler stays the game's own
-- function, because secure code calls it.
seterrorhandler = function(handler)
    if handler == Handler then
        -- Putting back what it found, which is this handler: the handler before that addon's takes over.
        previous = before or stock
        before = nil
    elseif type(handler) == "function" then
        before = previous
        previous = handler
    end
end

-- What earlier game runs left, with this run's errors (from before the file was read) added.
local function Merge(saved)
    local earlier = db
    db = saved
    index = {}
    db.dropped = (tonumber(db.dropped) or 0) + earlier.dropped
    db.suppressed = (tonumber(db.suppressed) or 0) + earlier.suppressed
    db.sessions = type(db.sessions) == "table" and db.sessions or {}
    db.errors = type(db.errors) == "table" and db.errors or {}
    local kept = {}
    for _, entry in ipairs(db.errors) do
        if type(entry) == "table" and type(entry.message) == "string" and type(entry.stack) == "string"
            and type(entry.count) == "number" then
            kept[#kept + 1] = entry
            index[Key(entry.message, entry.stack)] = entry
        end
    end
    db.errors = kept

    local offset = #db.sessions
    for _, entry in ipairs(earlier.sessions) do db.sessions[#db.sessions + 1] = entry end
    for _, entry in ipairs(earlier.errors) do
        local key = Key(entry.message, entry.stack)
        if index[key] then
            index[key].count = index[key].count + entry.count
            index[key].last = entry.last
        elseif #db.errors >= MAX_ERRORS then
            db.dropped = db.dropped + 1
        else
            entry.session = entry.session and entry.session + offset
            db.errors[#db.errors + 1] = entry
            index[key] = entry
        end
    end
end

local frame = CreateFrame("Frame")
frame:RegisterEvent("ADDON_LOADED")
frame:RegisterEvent("PLAYER_LOGIN")
frame:RegisterEvent("PLAYER_LOGOUT")
frame:RegisterEvent("ADDON_ACTION_BLOCKED")
frame:RegisterEvent("ADDON_ACTION_FORBIDDEN")
frame:SetScript("OnEvent", function(_, event, name, action)
    if event == "ADDON_LOADED" then
        if name == ADDON then
            local saved = PortalkeeperErrorsDB
            if type(saved) == "table" and saved.format == FORMAT then Merge(saved) end
            PortalkeeperErrorsDB = db
        end
    elseif event == "PLAYER_LOGIN" then
        -- An addon that kept the game's seterrorhandler from before this one loaded may have used it.
        local current = geterrorhandler()
        if current ~= Handler then
            previous = current
            setHandler(Handler)
        end
        -- Errors while loading came before the character was known.
        if session and not sessionFull then FillSession(session) end
    elseif event == "PLAYER_LOGOUT" then
        -- With the addons that loaded on demand since.
        if session and not sessionFull then FillSession(session) end
    else
        local text = tostring(name) .. " tried to call the protected function '" .. tostring(action) .. "'."
        text = sub(text, 1, MAX_MESSAGE)
        Record("blocked", text, "", Key(text, ""))
    end
end)
