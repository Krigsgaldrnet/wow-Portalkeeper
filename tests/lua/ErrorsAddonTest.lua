-- Checks for the error-recording addon (src/Portalkeeper/Addons/PortalkeeperErrors/Errors.lua) against
-- stand-ins for the client's functions. Run from the repo root with Lua 5.1 or LuaJIT:
--   luajit tests/lua/ErrorsAddonTest.lua

local failures, checks = 0, 0
local function check(name, ok)
    checks = checks + 1
    if not ok then
        failures = failures + 1
        print("FAIL " .. name)
    end
end

-- The client ------------------------------------------------------------------------------------------
local clock = 100
local handlerNow
local stockCalls, stockRaises, stockRecurses = {}, false, false
local function stock(message)
    stockCalls[#stockCalls + 1] = message
    if stockRecurses then
        -- A new error while the window opens: it goes straight back to the handler.
        geterrorhandler()("raised inside the window")
    end
    if stockRaises then
        -- The game's error window failing inside the handler: the 'cursorOffset' bug.
        error("Interface\\AddOns\\Blizzard_DebugTools\\Blizzard_DebugTools.lua:9: 'cursorOffset' nil")
    end
    return message
end
local currentLocals = "self = <table> {\n 0 = <userdata>\n}\ncount = 3\n"

function geterrorhandler() return handlerNow or stock end
local function realSetHandler(h) handlerNow = h end
-- The client's GetTime reads the clock on every call, also during a frame.
function GetTime()
    clock = clock + 0.0001
    return clock
end
time = function() return 1800000000 + clock end
function debugstack(start)
    return (debug.traceback("", (start or 1) + 1):gsub("^\nstack traceback:\n", ""):gsub("\t", ""))
end
function debuglocals(level)
    if currentLocals == "first local" then
        return ((debug.getlocal(level + 1, 1)) or "none") .. " = 1"
    end
    return currentLocals
end
function GetNumAddOns() return 2 end
function IsAddOnLoaded(i) return i <= 2 end
function LoadAddOn() DEBUGLOCALS_LEVEL = DEBUGLOCALS_LEVEL or 4 end
function GetAddOnInfo(i) return i == 1 and "!PortalkeeperErrors" or "DragonUI" end
function GetAddOnMetadata(i) return i == 1 and "1.0" or nil end
function UnitName() return "Alice" end
function GetRealmName() return "Test Realm" end
function GetBuildInfo() return "3.3.5", "12340", "Jun 24 2010", 30300 end
function GetLocale() return "enUS" end
local events, onEvent = {}, nil
function CreateFrame()
    return {
        RegisterEvent = function(_, e) events[e] = true end,
        SetScript = function(_, _, f) onEvent = f end,
    }
end

local function load_addon()
    handlerNow, stockCalls, stockRaises = nil, {}, false
    seterrorhandler = realSetHandler -- the addon replaced it
    DEBUGLOCALS_LEVEL = nil
    PortalkeeperErrorsDB = nil
    dofile("src/Portalkeeper/Addons/PortalkeeperErrors/Errors.lua")
end
local function loaded(saved)
    PortalkeeperErrorsDB = saved
    onEvent(nil, "ADDON_LOADED", "!PortalkeeperErrors")
    return PortalkeeperErrorsDB
end
local function raise(message)
    -- A failing frame script: the client calls the error handler with the message.
    return xpcall(function() error(message, 0) end, geterrorhandler())
end

-- Installing ------------------------------------------------------------------------------------------
load_addon()
local handler = geterrorhandler()
check("the handler is installed while the file loads", handler ~= stock)

-- Errors before the saved variables are read aren't lost
raise("early error")
local db = loaded({ format = 1, errors = { { kind = "error", message = "from last time", stack = "s", count = 2, first = 1, last = 2, session = 1 } },
    sessions = { { character = "Old", realm = "R", addons = {} } }, dropped = 1, suppressed = 0 })
check("the last run's errors are kept", db.errors[1].message == "from last time" and db.errors[1].count == 2)
check("an error from before the file was read is added", db.errors[2] and db.errors[2].message == "early error")
check("its login moved along with it", db.errors[2].session == 2 and db.sessions[2].character == "Alice")
check("counters add up", db.dropped == 1)

-- De-duplicating ------------------------------------------------------------------------------------
load_addon()
db = loaded(nil)
check("no file: a new table", db.format == 1 and #db.errors == 0)
local function failing() raise("Interface\\AddOns\\Foo\\Foo.lua:12: attempt to index a nil value") end
for _ = 1, 5 do clock = clock + 1; failing() end
check("one error, counted", #db.errors == 1 and db.errors[1].count == 5)
check("first and last are kept", db.errors[1].first < db.errors[1].last)
check("the file's own frames are left out of the stack", not db.errors[1].stack:find("Errors.lua", 1, true))
check("the addon list has versions", db.sessions[1].addons[1] == "!PortalkeeperErrors 1.0" and db.sessions[1].addons[2] == "DragonUI")
check("build and character", db.sessions[1].build == "3.3.5.12340" and db.sessions[1].character == "Alice")
check("only the first sighting reaches the game's window", #stockCalls == 1)
clock = clock + 1
raise("table: 1F8A3B20 broke")
clock = clock + 1
raise("table: 2C9D4E30 broke")
check("addresses don't make it a new error", #db.errors == 2 and db.errors[2].count == 2)

-- Locals ----------------------------------------------------------------------------------------------
currentLocals = "_G = <table> {\n  g1 = <function> defined @x.lua:1\n  g2 = 2\n}\nafter = 1"
clock = clock + 1
raise("dump error")
local entry = db.errors[#db.errors]
check("a table is one line", entry.locals == "_G = <table>\nafter = 1")
currentLocals = 'whisper = "meet me at the bank"\nname = ""\nother = 2\nlong = "first\nsecret = line\nlast"\nend = true\n(*temporary) = "the message"'
clock = clock + 1
raise("strings")
entry = db.errors[#db.errors]
check("the text of a string isn't kept", entry.locals == "whisper = <string>\nname = <string>\nother = 2\nlong = <string>\nend = true\n(*temporary) = <string>")
currentLocals = 'huge = "' .. string.rep("x", 1020) .. '\nnext = 2'
clock = clock + 1
raise("a string the client cut")
entry = db.errors[#db.errors]
check("a line the client cut doesn't hide the locals after it", entry.locals == "huge = <string>\nnext = 2")
local many = {}
for i = 1, 500 do many[i] = "local" .. i .. " = <function> defined @" .. string.rep("x", 400) end
currentLocals = table.concat(many, "\n")
clock = clock + 1
raise("many locals")
entry = db.errors[#db.errors]
check("locals are capped", #entry.locals <= 1510 and entry.locals:sub(-3) == "...")
check("a long local is cut", #entry.locals:match("^[^\n]*") == 160)
currentLocals = ""
clock = clock + 1
raise(string.rep("m", 5000))
entry = db.errors[#db.errors]
check("a long message is cut", #entry.message == 1000)

-- The locals come from the function that failed, whether it failed by itself or called error().
currentLocals = "first local"
clock = clock + 1
xpcall(function()
    local failedHere = nil
    return failedHere.field
end, geterrorhandler())
check("the failing function's locals", db.errors[#db.errors].locals == "failedHere = 1")
clock = clock + 1
xpcall(function()
    local calledError = 1
    error("from error() " .. calledError)
end, geterrorhandler())
check("the locals of whoever called error()", db.errors[#db.errors].locals == "calledError = 1")
currentLocals = ""

-- The client's real path: a frame script's error reaches the handler through an unnamed C function.
do
    local asked
    local realStack, realLocals = debugstack, debuglocals
    local own = "Interface\\AddOns\\!PortalkeeperErrors\\Errors.lua:1: in function <Interface\\AddOns\\!PortalkeeperErrors\\Errors.lua:1>\n"
    local function frames(rest)
        return own .. own .. "[C]: in function `pcall'\n" .. own .. "[C]: ?\n" .. rest
    end
    debuglocals = function(level) asked = level; return "x = 1" end
    load_addon() -- picks the stand-ins up as locals
    db = loaded(nil)
    debugstack = function() return frames("Interface\\AddOns\\Foo\\Foo.lua:12: in function <Foo.lua:10>\nFoo.lua:3: in main chunk\n") end
    load_addon()
    db = loaded(nil)
    clock = clock + 1
    raise("a runtime error")
    check("the unnamed C frame isn't kept", db.errors[1].stack:sub(1, 28) == "Interface\\AddOns\\Foo\\Foo.lua")
    -- ReadStack sees ReadStack, Capture, pcall, Handler, [C], the function: level 6; ReadLocals is two further.
    check("locals are read from the failing function", asked == 8)
    debugstack = function() return frames("[C]: in function `error'\nFoo.lua:12: in function <Foo.lua:10>\n") end
    load_addon()
    db = loaded(nil)
    clock = clock + 1
    raise("from error()")
    check("a named C function stays in the stack", db.errors[1].stack:sub(1, 24) == "[C]: in function `error'")
    check("locals step over it to its caller", asked == 9)
    debugstack = function() return frames("(tail call): ?\n[C]: in function `error'\nFoo.lua:12: in function <Foo.lua:10>\n") end
    load_addon()
    db = loaded(nil)
    clock = clock + 1
    raise("from a tail call")
    check("a tail call isn't kept", not db.errors[1].stack:find("tail call", 1, true))
    check("locals step over the tail call too", asked == 10)
    debugstack, debuglocals = realStack, realLocals
    load_addon()
    db = loaded(nil)
end

-- Storms and recursion ------------------------------------------------------------------------------
load_addon()
db = loaded(nil)
clock = math.floor(clock) + 2
for _ = 1, 1000 do raise("storm") end
check("one second's storm is capped", db.errors[1].count == 20 and db.suppressed == 980)
check("the window was opened once and read its locals below the handler", #stockCalls == 1 and DEBUGLOCALS_LEVEL == 6)
clock = clock + 2
stockCalls = {}
stockRaises = true
local ok = pcall(function() raise("window fails") end)
check("the game's window failing doesn't escape", ok)
check("the window's failure is contained", #stockCalls == 1)
check("the handler still works afterwards", (function()
    clock = clock + 2
    raise("after the failure")
    return db.errors[#db.errors].message == "after the failure"
end)())
stockRaises = false
stockCalls = {}
clock = clock + 2
raise("Interface\\FrameXML\\UIPanelTemplates.lua:365: attempt to perform arithmetic on field 'cursorOffset' (a nil value)")
check("an error from the window itself isn't shown in the window", #stockCalls == 0)
check("but it is recorded", db.errors[#db.errors].message:find("cursorOffset", 1, true) ~= nil)
stockCalls = {}
clock = clock + 2
raise("Interface\\AddOns\\Blizzard_DebugTools\\Blizzard_DebugTools.lua:9: anything")
check("nor is anything else raised in its addon", #stockCalls == 0)

-- Re-entry: an error raised while the window opens comes back to the handler, on a later clock tick.
load_addon()
db = loaded(nil)
clock = clock + 2
stockRecurses = true
raise("first")
stockRecurses = false
check("errors raised while handling one stop at the guard", #stockCalls == 1 and db.suppressed == 1 and #db.errors == 1)
clock = clock + 2
raise("later")
check("and the handler works again after", db.errors[2] and db.errors[2].message == "later")

-- An addon chaining the usual way: orig = geterrorhandler(), seterrorhandler(mine), and mine calls orig.
load_addon()
db = loaded(nil)
local orig = geterrorhandler()
local chainCalls = 0
seterrorhandler(function(message)
    chainCalls = chainCalls + 1
    return orig(message)
end)
clock = clock + 2
raise("chained")
clock = clock + 2
raise("chained")
check("the chain reaches the game's window for the first sighting", #stockCalls == 1 and stockCalls[1] == "chained")
check("the chain's addon gets every error", chainCalls == 2)
check("nothing is counted twice or suppressed", db.errors[1].count == 2 and db.suppressed == 0)
-- Putting back what it found
seterrorhandler(orig)
stockCalls = {}
clock = clock + 2
raise("back again")
check("putting the handler back brings the game's window back", #stockCalls == 1 and chainCalls == 2)

-- A new error while the chained addon runs doesn't take the window's turn from the real one.
load_addon()
db = loaded(nil)
orig = geterrorhandler()
seterrorhandler(function(message)
    orig("a new error in that addon")
    return orig("[chain] " .. message)
end)
clock = clock + 2
raise("the real one")
check("the window shows the real error, as the addon passed it on", #stockCalls == 1 and stockCalls[1] == "[chain] the real one")
check("the new one is counted", db.suppressed == 1 and #db.errors == 1)

-- An addon that took over and doesn't chain: an error in its own handler doesn't reach the window.
load_addon()
db = loaded(nil)
orig = geterrorhandler()
seterrorhandler(function() orig("its own failure") end)
clock = clock + 2
raise("to that addon")
check("its failure doesn't open the window", #stockCalls == 0 and db.suppressed == 1)

-- A temporary swap over a handler that stays.
load_addon()
db = loaded(nil)
local staying = 0
seterrorhandler(function() staying = staying + 1 end)
orig = geterrorhandler()
seterrorhandler(function() end)
seterrorhandler(orig)
clock = clock + 2
raise("after the swap")
check("the handler from before the swap takes over again", staying == 1 and #stockCalls == 0)

-- After /reload the file has last time's errors, and the window hasn't seen them.
load_addon()
db = loaded({ format = 1, errors = { { kind = "error", message = "seen before", stack = "", count = 2, first = 1, last = 2 } }, sessions = {}, dropped = 0, suppressed = 0 })
clock = clock + 2
do
    -- As if raised with the same stack as the saved entry: no frames.
    local realStack = debugstack
    debugstack = function() return "" end
    load_addon()
    db = loaded({ format = 1, errors = { { kind = "error", message = "seen before", stack = "", count = 2, first = 1, last = 2 } }, sessions = {}, dropped = 0, suppressed = 0 })
    raise("seen before")
    debugstack = realStack
end
check("an error from last time counts on", #db.errors == 1 and db.errors[1].count == 3)
check("and still reaches the window", #stockCalls == 1)

-- The window's limit --------------------------------------------------------------------------------
load_addon()
db = loaded(nil)
for i = 1, 15 do
    clock = clock + 2
    raise("distinct " .. i)
end
check("at most 10 errors a login reach the game's window", #stockCalls == 10)
check("all were recorded", #db.errors == 15)

-- The limit on errors kept ---------------------------------------------------------------------------
for i = 1, 200 do
    clock = clock + 2
    raise("more " .. i)
end
check("at most 100 errors are kept", #db.errors == 100)
check("the rest are counted", db.dropped == 115)

-- Past the limit on logins the last entry stays whose it was.
load_addon()
do
    local sessions = {}
    for i = 1, 20 do sessions[i] = { character = "Old" .. i, realm = "R", addons = {} } end
    db = loaded({ format = 1, errors = {}, sessions = sessions, dropped = 0, suppressed = 0 })
end
clock = clock + 2
raise("past the limit")
onEvent(nil, "PLAYER_LOGOUT")
check("the last login's entry isn't overwritten", #db.sessions == 20 and db.errors[1].session == 20 and db.sessions[20].character == "Old20")

-- Other addons' handlers ------------------------------------------------------------------------------
load_addon()
db = loaded(nil)
local bugSackCalls = 0
seterrorhandler(function() bugSackCalls = bugSackCalls + 1 end)
check("the addon stays the handler", geterrorhandler() == handlerNow and handlerNow ~= nil)
clock = clock + 2
raise("to the other addon")
clock = clock + 2
raise("to the other addon")
check("another addon's handler gets every error", bugSackCalls == 2 and db.errors[1].count == 2)

-- Taken over by an addon that loaded first and kept the real function.
load_addon()
db = loaded(nil)
local kept = 0
handlerNow = function() kept = kept + 1 end
onEvent(nil, "PLAYER_LOGIN")
check("login puts the handler back", (function()
    clock = clock + 2
    raise("after login")
    return db.errors[1] and db.errors[1].message == "after login" and kept == 1
end)())

-- Blocked actions ------------------------------------------------------------------------------------
load_addon()
db = loaded(nil)
onEvent(nil, "ADDON_ACTION_FORBIDDEN", "DragonUI", "CastSpellByName()")
onEvent(nil, "ADDON_ACTION_BLOCKED", "DragonUI", "CastSpellByName()")
check("a blocked action is one entry", #db.errors == 1 and db.errors[1].count == 2 and db.errors[1].kind == "blocked")
check("and says who and what", db.errors[1].message == "DragonUI tried to call the protected function 'CastSpellByName()'.")

-- A file from another format is ignored
load_addon()
db = loaded({ format = 99, errors = "nonsense" })
check("another format starts over", db.format == 1 and #db.errors == 0)
load_addon()
db = loaded({ format = 1, errors = { "bad", { message = 1 } }, sessions = 7 })
check("bad entries are left out", #db.errors == 0 and #db.sessions == 0)

print(checks .. " checks, " .. failures .. " failed")
os.exit(failures == 0 and 0 or 1)
