using Exvs2Launcher.Core.Input;

namespace Exvs2Launcher.Core.Tests;

public class HidButtonMapTests
{
    /// <summary>Observed SnowLauncher / config.ini ids for a Razer Panthera in PS4 mode.</summary>
    private static readonly ushort[] PantheraUsages = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 };

    private static HidButtonMap PantheraMap() => new(PantheraUsages);

    [Theory]
    [InlineData(1, 1)]   // Square  -> ButtonA
    [InlineData(2, 2)]   // Cross   -> ButtonC
    [InlineData(3, 3)]   // Circle  -> ButtonD
    [InlineData(4, 4)]   // Triangle -> ButtonB
    [InlineData(6, 6)]   // R1 -> ButtonAB
    [InlineData(7, 7)]   // L2 -> ButtonAC
    [InlineData(8, 8)]   // R2 -> ButtonBC
    [InlineData(9, 9)]   // Share -> Test
    [InlineData(10, 10)] // Options -> Start
    [InlineData(11, 11)] // L3 -> Start (second id)
    [InlineData(12, 12)] // R3 -> ButtonABC
    [InlineData(13, 13)] // Home -> Kill
    [InlineData(14, 14)] // Trackpad -> Coin / Card
    public void Panthera_ps4_usages_match_the_game_button_ids(int usage, int gameId)
    {
        Assert.Equal(gameId, PantheraMap().ToGameId((ushort)usage));
    }

    [Fact]
    public void Panthera_face_buttons_match_observed_config_ini()
    {
        var map = PantheraMap();
        Assert.Equal(new[] { 1 }, map.ToGameIds(new ushort[] { 1 }));
        Assert.Equal(new[] { 4 }, map.ToGameIds(new ushort[] { 4 }));
        Assert.Equal(new[] { 2 }, map.ToGameIds(new ushort[] { 2 }));
        Assert.Equal(new[] { 3 }, map.ToGameIds(new ushort[] { 3 }));
        Assert.Equal(new[] { 10, 11 }, map.ToGameIds(new ushort[] { 11, 10 }));
        Assert.Equal(new[] { 14 }, map.ToGameIds(new ushort[] { 14 }));
    }

    [Fact]
    public void Fallback_uses_the_usage_number_as_the_game_id()
    {
        Assert.Equal(new[] { 1, 4, 14 }, HidButtonMap.FallbackGameIds(new ushort[] { 14, 1, 4, 0 }));
    }

    [Fact]
    public void Caps_are_ordered_by_data_index_not_usage_value()
    {
        var map = HidButtonMap.FromCaps(new[]
        {
            new HidButtonCap(4, 4, DataIndexMin: 2, IsRange: false),
            new HidButtonCap(1, 2, DataIndexMin: 0, IsRange: true),
        });
        Assert.Equal(new ushort[] { 1, 2, 4 }, map.UsagesInGameOrder.ToArray());
        Assert.Equal(1, map.ToGameId(1));
        Assert.Equal(2, map.ToGameId(2));
        Assert.Equal(3, map.ToGameId(4));
        Assert.Equal(new[] { 3 }, map.ToGameIds(new ushort[] { 4 }));
        Assert.Null(map.ToGameId(99));
    }

    [Fact]
    public void Empty_presses_and_unknown_usages_yield_nothing()
    {
        Assert.Empty(PantheraMap().ToGameIds(Array.Empty<ushort>()));
        Assert.Empty(PantheraMap().ToGameIds(new ushort[] { 99 }));
        Assert.Empty(HidButtonMap.FallbackGameIds(Array.Empty<ushort>()));
    }

    [Fact]
    public void Device_paths_compare_across_raw_input_and_setupapi_prefixes()
    {
        const string setup = @"\\?\HID#VID_1532&PID_0401#7&abc#{4d1e55ab-0000-0000-0000-000000000000}";
        const string raw = @"\??\HID#VID_1532&PID_0401#7&abc#{4d1e55ab-1111-2222-3333-444444444444}";
        Assert.True(DevicePathComparer.Equals(setup, raw));
        Assert.False(DevicePathComparer.Equals(setup, @"\\?\HID#VID_045E&PID_02EA#1"));
        Assert.False(DevicePathComparer.Equals("", @"\\?\HID#VID_1532&PID_0401"));
    }
}
