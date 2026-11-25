// 一次性种子工具：将百度凭据以 DPAPI 加密写入 %APPDATA%\SnapTranslate\config.json
// 用法：dotnet run --project tools/SeedConfig -- <appid> <secret>
// 独立小项目不划算，直接用 PowerShell + 当前 exe 环境 C# 编译也麻烦——
// 简化方案：以参数形式由主程序接收（见下）。
using System.Security.Cryptography;

if (args.Length < 2)
{
    Console.Error.WriteLine("用法: SeedConfig <appid> <secret>");
    return 1;
}

var appId = args[0];
var secret = args[1];

var dir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapTranslate");
Directory.CreateDirectory(dir);

var secretEnc = Convert.ToBase64String(
    ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

// 幂等：读取-合并现有配置（保留其他键）
var configPath = Path.Combine(dir, "config.json");
var json = File.Exists(configPath) ? File.ReadAllText(configPath) : "{}";
// 极简 JSON 拼接（避免引依赖）：正则替换或插入 baidu 节点
if (json.Contains("\"baidu\""))
{
    // 已有 baidu 节点：提示手动处理，避免破坏
    Console.Error.WriteLine("config.json 已包含 baidu 节点，如需更新请先删除该文件。");
    return 1;
}

var baiduNode = $"\"baidu\": {{ \"appid\": \"{appId}\", \"secretEnc\": \"{secretEnc}\" }}";
json = json.Trim();
json = json == "{}" ? $"{{\n  {baiduNode},\n  \"version\": 1\n}}\n" : json[..^1] + $",\n  {baiduNode}\n}}\n";

File.WriteAllText(configPath, json);
Console.WriteLine($"已写入 {configPath}");
return 0;
