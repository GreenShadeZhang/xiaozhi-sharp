using System.Text;
using System.Text.Json;
using XiaoZhiSharp.Utils;

namespace XiaoZhiSharp.Services
{
    public class OtaService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed = false;

        public string? OTA_VERSION_URL { get; set; } = "https://api.tenclass.net/xiaozhi/ota/";
        public JsonDocument? OTA_INFO { get; set; }
        public string? MAC_ADDR { get; set; }

        public OtaService(string url, string mac)
        {
            OTA_VERSION_URL = url;
            MAC_ADDR = mac;
            if (string.IsNullOrEmpty(MAC_ADDR))
                MAC_ADDR = SystemInfo.GetMacAddress();

            _httpClient = new HttpClient();

            // 启动后台线程定期检查OTA信息
            Thread _otaThread = new Thread(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await GetOtaInfoAsync();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                    }

                    try
                    {
                        await Task.Delay(1000 * 60, _cts.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            });
            _otaThread.IsBackground = true;
            _otaThread.Start();
        }

        /// <summary>
        /// 获取配置
        /// </summary>
        private async Task GetOtaInfoAsync()
        {
            try
            {
                // 设置请求头
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("Device-Id", MAC_ADDR);

                // 准备请求数据
                var postData = new
                {
                    application = new
                    {
                        name = "XiaoZhiSharp",
                        version = "1.0.1"
                    }
                };

                // 发送POST请求
                var content = new StringContent(
                    JsonSerializer.Serialize(postData),
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.PostAsync(OTA_VERSION_URL, content);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine("获取OTA版本信息失败!");
                    return;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrEmpty(responseContent))
                {
                    try
                    {
                        OTA_INFO = JsonDocument.Parse(responseContent);

                        // 使用TryGetProperty进行安全的属性访问
                        if (OTA_INFO.RootElement.TryGetProperty("activation", out var activationProperty) &&
                            activationProperty.TryGetProperty("code", out var codeProperty))
                        {
                            string activationCode = codeProperty.GetString() ?? "未知";
                            Console.WriteLine($"请先登录xiaozhi.me,绑定Code：{activationCode}");
                        }
                    }
                    catch (JsonException ex)
                    {
                        Console.WriteLine($"解析OTA响应时出错: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取OTA版本信息时发生异常: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _cts.Cancel();
                    _cts.Dispose();
                    _httpClient.Dispose();
                }

                _disposed = true;
            }
        }
    }
}
