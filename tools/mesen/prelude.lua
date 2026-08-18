-- Shared helpers for headless Mesen probes. New-MesenProbe.ps1 pastes this ahead of the
-- probe body, so a probe is just the interesting part.
--
-- The sandbox has no io/os and emu.log goes nowhere in /testrunner mode, so everything a
-- probe wants to say comes back as the process exit code via pass()/fail()/report().

local M = {}

M.frame = 0

-- SNES memory reads go through snesMemory (the CPU bus view), so $7E:xxxx WRAM addresses
-- are written the same way they appear in a disassembly or in the Mesen memory viewer.
function M.rb(addr) return emu.read(addr, emu.memType.snesMemory, false) end
function M.rw(addr) return M.rb(addr) + M.rb(addr + 1) * 256 end
function M.vram(addr) return emu.read(addr, emu.memType.snesVideoRam, false) end

function M.pass() emu.stop(0) end
function M.fail(code) emu.stop(code) end        -- 1..99, meaning is the probe's to define
function M.report(v) emu.stop(v % 256) end      -- observation channel: one byte per run

-- Hold a set of buttons for this frame, e.g. M.hold{ start = true }. SMW needs Start
-- pressed and RELEASED to advance a menu, so callers pulse rather than hold.
-- Argument order is (input, port) — (port, input) throws, and a throwing frame callback
-- is invisible here: the run simply never reaches emu.stop and dies on the timeout.
function M.hold(buttons) emu.setInput(buttons or {}, 0) end

-- Run `fn(frame)` every frame. `fn` decides when to stop; if it never does, Mesen's
-- /timeout kills the run and the runner reports -1.
function M.each(fn)
    emu.addEventCallback(function()
        M.frame = M.frame + 1
        fn(M.frame)
    end, emu.eventType.startFrame)
end

-- Boot far enough to be in a level: pulse Start to clear the title screen and the file
-- select. Pulsing (rather than holding) matters — SMW's menus edge-trigger.
-- Returns true once the game has left the boot modes; see reference/MESEN.md for the
-- game-mode values this keys on.
function M.bootPulse(frame)
    local phase = frame % 32
    M.hold(phase < 4 and { start = true } or {})
end

return M
