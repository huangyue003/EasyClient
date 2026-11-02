# EasyClient

面向仪器和上位机的 .NET 10 通信库，统一提供 TCP、UDP 和串口的发送、请求响应及主动数据回调。

## 从这里开始

- [项目结构与使用指南](PROJECT-GUIDE.md)：架构、配置、示例、异常、扩展与排障。
- [测试覆盖率评估](TEST-COVERAGE.md)：实测数据、剩余缺口和补测建议。

需要 .NET 10 SDK。主库与测试均为 `net10.0`；本轮没有同时升级 NuGet 依赖。

```powershell
dotnet restore IFactory.Interaction.EasyClient.slnx
dotnet build IFactory.Interaction.EasyClient.slnx -c Release -warnaserror
dotnet test IFactory.Interaction.EasyClient.slnx -c Release --filter 'Category!=SerialIntegration'
```

使用项目引用：

```powershell
dotnet add <你的项目.csproj> reference IFactory.Interaction.EasyClient/IFactory.Interaction.EasyClient.csproj
```

串口实测需提前在 VSPD 创建相连的 COM1/COM2，并关闭占用端口的调试工具：

```powershell
$env:EASYCLIENT_SERIAL_TESTS = '1'
dotnet test IFactory.Interaction.EasyClient.slnx -c Release
```

默认不启用串口集成测试。当前完整测试 68 项，其中 6 项为串口集成测试。详细结果见覆盖率评估。
