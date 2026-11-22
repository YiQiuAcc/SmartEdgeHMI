using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Tests.Common;

public class ConnectionSettingsTests
{
    [Fact]
    public void Protocol_DefaultValue_IsJson()
    {
        // 旧 settings.json 缺 Protocol 键时依赖该默认值反序列化, 语义必须是现状默认 JSON
        Assert.Equal("JSON", new ConnectionSettings().Protocol);
    }
}
