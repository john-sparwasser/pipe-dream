using PipeDream.Services;
using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// The update check's decisions, none of which touch the network. What can go wrong here is
/// version comparison and asset matching, and both fail QUIETLY — an updater that offers the
/// running build to itself, or downloads the wrong platform's file, looks like it works.
/// </summary>
public class UpdateCheckTests
{
    [Theory]
    [InlineData("v0.1.42", "0.1.42")]
    [InlineData("0.1.42", "0.1.42")]
    [InlineData("V1.0.0", "1.0.0")]
    [InlineData("0.2", "0.2.0")]              // two-part tag: build defaults rather than -1
    public void tags_parse_with_or_without_the_v(string tag, string expected)
        => Assert.Equal(expected, UpdateCheck.ParseTag(tag)!.ToString(3));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData("v")]
    [InlineData("release-2026")]
    public void junk_tags_are_not_versions(string? tag)
        => Assert.Null(UpdateCheck.ParseTag(tag));

    /// <summary>
    /// The bug this exists to prevent: Version leaves unspecified parts at -1, so the tag
    /// "0.1.42" (0.1.42) sorts BELOW the assembly's own 0.1.42.0. Unnormalised, the app would
    /// offer every user an update to the build they are already running, forever.
    /// </summary>
    [Fact]
    public void a_release_matching_the_running_build_is_not_newer()
    {
        var running = UpdateCheck.Normalise(new Version(0, 1, 42, 0))!;
        var released = UpdateCheck.ParseTag("v0.1.42")!;
        Assert.Equal(running, released);
        Assert.False(released > running);
    }

    [Fact]
    public void ordering_survives_normalising()
    {
        var running = UpdateCheck.Normalise(new Version(0, 1, 42, 0))!;
        Assert.True(UpdateCheck.ParseTag("v0.1.43") > running);
        Assert.True(UpdateCheck.ParseTag("v0.2.0") > running);
        Assert.True(UpdateCheck.ParseTag("v1.0.0") > running);
        Assert.False(UpdateCheck.ParseTag("v0.1.41") > running);
        Assert.False(UpdateCheck.ParseTag("v0.0.99") > running);
    }

    [Theory]
    // Windows wants the installer, and only the installer — versionless (what CI ships now)
    // or versioned (older releases).
    [InlineData("PipeDream-Setup.exe", true, true)]
    [InlineData("PipeDream-Setup-0.1.42.exe", true, true)]
    [InlineData("PipeDream-linux-x64", true, false)]
    [InlineData("PipeDream.exe", true, false)]
    [InlineData("source.zip", true, false)]
    // Linux wants the linux binary, and must never take a .exe.
    [InlineData("PipeDream-linux-x64", false, true)]
    [InlineData("PipeDream-Setup-0.1.42.exe", false, false)]
    [InlineData("PipeDream-win-x64.exe", false, false)]
    public void each_platform_takes_only_its_own_asset(string name, bool windows, bool wanted)
        => Assert.Equal(wanted, UpdateCheck.WantsAsset(name, windows));

    [Fact]
    public void asking_explicitly_always_checks()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        // Even with checks off, and even one second after the last one.
        Assert.True(UpdateCheck.Due(userAsked: true, enabled: false, now.AddSeconds(-1), now));
    }

    [Fact]
    public void automatic_checks_are_daily_and_respect_the_setting()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(UpdateCheck.Due(false, enabled: false, null, now));            // turned off
        Assert.True(UpdateCheck.Due(false, true, null, now));                       // never checked
        Assert.True(UpdateCheck.Due(false, true, now.AddHours(-25), now));
        Assert.True(UpdateCheck.Due(false, true, now.AddHours(-24), now));          // exactly due
        Assert.False(UpdateCheck.Due(false, true, now.AddHours(-23), now));
        Assert.False(UpdateCheck.Due(false, true, now, now));
    }

    /// <summary>A clock that has gone backwards must not lock the check out forever.</summary>
    [Fact]
    public void a_future_last_check_does_not_wedge_it()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(UpdateCheck.Due(false, true, now.AddDays(30), now));
        Assert.True(UpdateCheck.Due(userAsked: true, true, now.AddDays(30), now));
    }

    [Fact]
    public void the_display_string_is_what_a_skip_records()
    {
        var u = new UpdateInfo(UpdateCheck.ParseTag("v0.1.42")!, "PipeDream-Setup-0.1.42.exe",
                               "https://example.invalid/x", 0, null);
        Assert.Equal("0.1.42", u.Display);
        // Round-trips: a skip is stored as this string and compared back through ParseTag.
        Assert.Equal(u.Version, UpdateCheck.ParseTag(u.Display));
    }

    [Fact]
    public void the_running_build_reports_a_real_version()
        => Assert.True(UpdateCheck.Current >= new Version(0, 0, 0));
}
