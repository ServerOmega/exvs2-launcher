using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.Net;

namespace Exvs2Launcher.Core.Tests;

public class WebUiUrlTests
{
    [Fact]
    public void Builds_http_url_from_host_and_port()
    {
        Assert.True(WebUiUrl.TryBuild("127.0.0.1", 28080, out var url, out var err));
        Assert.Null(err);
        Assert.Equal("http://127.0.0.1:28080/", url);
        Assert.Equal("http://192.168.1.69:28090/Cards", WebUiUrl.BuildOrNull("192.168.1.69", 28090, WebUiUrl.CardsPath));
    }

    [Fact]
    public void Unspecified_bind_address_opens_as_localhost()
    {
        Assert.Equal("http://127.0.0.1:28080/", WebUiUrl.BuildOrNull("0.0.0.0", 28080));
        Assert.Equal("http://127.0.0.1:28080/", WebUiUrl.BuildOrNull("*", 28080));
        Assert.True(WebUiUrl.TryBuild("::", 28080, out var url, out _));
        Assert.Equal("http://[::1]:28080/", url);
    }

    [Fact]
    public void Ipv6_host_is_bracketed()
    {
        Assert.Equal("http://[2001:db8::1]:28080/", WebUiUrl.BuildOrNull("2001:db8::1", 28080));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Rejects_bad_port(int port)
    {
        Assert.False(WebUiUrl.TryBuild("127.0.0.1", port, out var url, out var err));
        Assert.Null(url);
        Assert.Contains("port", err, StringComparison.OrdinalIgnoreCase);
    }
}

public class GameWindowTitleTests
{
    [Fact]
    public void Folder_label_is_the_directory_name()
    {
        Assert.Equal("EXVS2IB_V29", GameWindowTitle.FolderLabel(@"E:\Games\pack\EXVS2IB_V29"));
        Assert.Equal("EXVS2IB_V29_P2", GameWindowTitle.FolderLabel(@"E:\Games\pack\EXVS2IB_V29_P2\"));
        Assert.Null(GameWindowTitle.FolderLabel(""));
    }

    [Fact]
    public void Describe_explains_pcb_window_names()
    {
        var running = GameWindowTitle.Describe("EXVS2IB_V29", "PCB_03");
        Assert.Contains("EXVS2IB_V29", running);
        Assert.Contains("PCB_03", running);
        var idle = GameWindowTitle.Describe("EXVS2IB_V29", null);
        Assert.Contains("EXVS2IB_V29", idle);
        Assert.Contains("PCB_xx", idle);
    }
}
