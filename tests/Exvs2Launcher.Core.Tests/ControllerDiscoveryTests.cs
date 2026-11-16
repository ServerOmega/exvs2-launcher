using Exvs2Launcher.Core.Input;

namespace Exvs2Launcher.Core.Tests;

public class ControllerDiscoveryTests
{
    [Theory]
    [InlineData("XInput pad 0")]
    [InlineData("XInput Controller 1")]
    [InlineData("Microsoft PC-joystick driver")]
    [InlineData("HID-compliant game controller")]
    [InlineData("USB Input Device")]
    [InlineData("Joystick 2")]
    [InlineData("")]
    [InlineData(null)]
    public void Generic_names_are_rejected(string? name) =>
        Assert.True(ControllerNameResolver.IsGenericName(name));

    [Theory]
    [InlineData("Razer Panthera")]
    [InlineData("Razer Panthera PS4")]
    [InlineData("Controller (Xbox One For Windows)")]
    [InlineData("Controller (XBOX 360 For Windows)")]
    public void Product_names_are_kept(string name) =>
        Assert.False(ControllerNameResolver.IsGenericName(name));

    [Fact]
    public void Hid_short_name_is_shown_but_winmm_name_is_stored_for_the_game()
    {
        var names = ControllerNameResolver.Resolve(new ControllerNameSources
        {
            HidProduct = "Razer Panthera",
            WinmmName = "Razer Panthera PS4",
        });
        Assert.Equal("Razer Panthera", names.DisplayName);
        Assert.Equal("Razer Panthera PS4", names.StoredName);
    }

    [Fact]
    public void Xbox_winmm_name_is_stored_instead_of_a_generic_hid_label()
    {
        var names = ControllerNameResolver.Resolve(new ControllerNameSources
        {
            HidProduct = "HID-compliant game controller",
            FriendlyName = "USB Input Device",
            WinmmName = "Controller (Xbox One For Windows)",
        });
        Assert.Equal("Controller (Xbox One For Windows)", names.DisplayName);
        Assert.Equal("Controller (Xbox One For Windows)", names.StoredName);
    }

    [Fact]
    public void Bus_reported_description_is_used_when_hid_product_is_missing()
    {
        var names = ControllerNameResolver.Resolve(new ControllerNameSources
        {
            BusReportedDescription = "Razer Panthera",
            DeviceDescription = "HID-compliant game controller",
        });
        Assert.Equal("Razer Panthera", names.DisplayName);
        Assert.Equal("Razer Panthera", names.StoredName);
    }

    [Fact]
    public void Names_that_share_a_product_string_are_treated_as_the_same_device()
    {
        Assert.True(ControllerNameResolver.NamesLikelySame("Razer Panthera", "Razer Panthera PS4"));
        Assert.False(ControllerNameResolver.NamesLikelySame("Razer Panthera", "Controller (Xbox One For Windows)"));
    }

    [Fact]
    public void Merge_attaches_winmm_and_keeps_the_ps4_stored_name()
    {
        var list = ControllerInventory.Merge(
            new[]
            {
                new HidControllerInfo
                {
                    Product = "Razer Panthera",
                    Path = @"\\?\HID#VID_1532&PID_0401#1",
                    ButtonCount = 14,
                },
            },
            new[] { new WinmmControllerInfo { Id = 0, Name = "Razer Panthera PS4", ButtonCount = 14 } },
            Array.Empty<XInputSlotInfo>());

        var one = Assert.Single(list);
        Assert.Equal("Razer Panthera", one.Name);
        Assert.Equal("Razer Panthera PS4", one.IniName);
        Assert.Equal(0, one.WinmmId);
        Assert.Null(one.XInputIndex);
        Assert.Contains("INI name: Razer Panthera PS4", one.Display);
    }

    [Fact]
    public void Merge_pairs_an_xinput_slot_with_an_IG_hid_device()
    {
        var list = ControllerInventory.Merge(
            new[]
            {
                new HidControllerInfo
                {
                    Product = "Xbox One Controller",
                    Path = @"\\?\HID#VID_045E&PID_02EA&IG_00#2",
                    IsXInputFilter = true,
                    XInputInterfaceIndex = 0,
                    VendorId = 0x045E,
                    ProductId = 0x02EA,
                    ButtonCount = 10,
                },
            },
            new[] { new WinmmControllerInfo { Id = 0, Name = "Controller (Xbox One For Windows)", ButtonCount = 10 } },
            new[] { new XInputSlotInfo { Slot = 0, ButtonCount = 10 } });

        var one = Assert.Single(list);
        Assert.Equal(0, one.XInputIndex);
        Assert.Equal(0, one.WinmmId);
        Assert.Equal("Controller (Xbox One For Windows)", one.IniName);
        Assert.Contains("XInput slot 0", one.Source);
    }

    [Fact]
    public void Merge_does_not_duplicate_a_pad_that_appears_in_all_three_apis()
    {
        var list = ControllerInventory.Merge(
            new[]
            {
                new HidControllerInfo
                {
                    Product = "Controller (XBOX 360 For Windows)",
                    Path = @"\\?\HID#VID_045E&PID_028E&IG_00",
                    IsXInputFilter = true,
                    XInputInterfaceIndex = 0,
                },
            },
            new[] { new WinmmControllerInfo { Id = 0, Name = "Controller (XBOX 360 For Windows)", ButtonCount = 10 } },
            new[] { new XInputSlotInfo { Slot = 0, ButtonCount = 10 } });
        Assert.Single(list);
    }

    [Fact]
    public void Merge_keeps_a_directinput_stick_and_an_unmatched_xinput_slot_separate()
    {
        var list = ControllerInventory.Merge(
            Array.Empty<HidControllerInfo>(),
            new[] { new WinmmControllerInfo { Id = 1, Name = "Razer Panthera PS4", ButtonCount = 14 } },
            new[] { new XInputSlotInfo { Slot = 0, ButtonCount = 10 } });
        Assert.Equal(2, list.Count);
        Assert.Contains(list, c => c.IniName == "Razer Panthera PS4" && c.WinmmId == 1 && c.XInputIndex is null);
        Assert.Contains(list, c => c.XInputIndex == 0);
    }

    [Fact]
    public void Connected_controller_display_prefers_product_name_over_xinput_slot_label()
    {
        var c = new ConnectedController(
            Name: "Razer Panthera",
            Source: "HID, DirectInput / WinMM id 0",
            XInputIndex: null,
            WinmmId: 0,
            ButtonCount: 14,
            StoredName: "Razer Panthera PS4");
        Assert.DoesNotContain("XInput Controller", c.Display);
        Assert.StartsWith("Razer Panthera", c.Display);
        Assert.Equal("Razer Panthera PS4", c.IniName);
    }
}
