#!/usr/bin/env python3
"""amk_events.py SECONDS 'mml' ... : compile each song with AMK, run its .spc in our SPC700 interpreter, print the key-on events."""
import sys, os, subprocess, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import amk_probe as P
DLL = os.environ.get('PIPEDREAM_DLL', os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'src', 'bin', 'Debug', 'net10.0', 'PipeDream.dll'))
def events(spc_bytes, seconds):
    f = tempfile.NamedTemporaryFile(suffix='.spc', delete=False); f.write(spc_bytes); f.close()
    try: return subprocess.run(['dotnet', DLL, '--spcevents', f.name, str(seconds)], capture_output=True, text=True, timeout=300).stdout
    finally: os.unlink(f.name)
if __name__ == '__main__':
    secs = float(sys.argv[1])
    for t, r in zip(sys.argv[2:], P.probe([a.replace('\\n', '\n') for a in sys.argv[2:]])):
        print('##', repr(t))
        print(events(r['spc'], secs) if r['ok'] else 'FAIL ' + ' / '.join(r['msg'])[:100])
