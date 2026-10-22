# AddmusicK differential tools

Black-box tools for checking our MML compiler against AddmusicK (reference/AMK_PORT.md). They run `addmusick -norom`
from a copy of the folder `PIPEDREAM_AMK` names (a folder laid out like AMK's own `test/`) and our compiler through
`dotnet src/bin/Debug/net10.0/PipeDream.dll --mml` (override with `PIPEDREAM_DLL`). Python 3, no packages.

| tool | what it does |
|---|---|
| `amk_probe.py 'mml' ...` | compile snippets with AMK, print its song bytes (or its error) |
| `amk_diff.py` | `diff(list)`: AMK against ours for each snippet; reports accept/reject and byte differences |
| `fuzz.py START N` | N random songs from the command set, through `amk_diff` |
| `reduce.py 'song'` | shrink a song that differs to the tokens that matter |
| `showdiff.py 'song'` | the first differing bytes of one song, with context |
| `corpus_diff.py [file...]` | AMK's own test songs, ours against theirs |

AMK 1.0.5 hangs on a few inputs (nested loops, a loop count past the octave range); the probe gives each run 20 seconds.
Nobody writing the compiler or the engine reads AMK's source; these tools are how its behaviour is learned.
