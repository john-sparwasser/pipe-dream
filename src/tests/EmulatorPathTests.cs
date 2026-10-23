using Xunit;

namespace PipeDream.Tests;

/// <summary>Run in emulator's program path: a macOS app bundle is the program inside it.</summary>
public class EmulatorPathTests
{
    [Fact]
    public void an_app_bundle_is_the_program_inside_it()
    {
        string dir = Directory.CreateTempSubdirectory("pd-app-").FullName;
        string app = Path.Combine(dir, "Mesen.app"), macos = Path.Combine(app, "Contents", "MacOS");
        Directory.CreateDirectory(macos);
        File.WriteAllText(Path.Combine(macos, "Mesen"), "");
        File.WriteAllText(Path.Combine(macos, "libSkiaSharp.dylib"), "");
        Assert.Equal(Path.Combine(macos, "Mesen"), Services.EditorSession.AppProgram(app));
        Assert.Equal(Path.Combine(macos, "Mesen"), Services.EditorSession.AppProgram(app + "/"));
        Assert.Equal("/usr/bin/true", Services.EditorSession.AppProgram("/usr/bin/true"));      // not a bundle: as it is
    }
}
