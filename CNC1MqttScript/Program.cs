using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using SocketIO.Core;
using SocketIOClient;
using SocketIOClient.Transport;

namespace CNC1MqttScript
{
    public class Program
    {
        private static bool _authenticationCompleted = false; // ✅ Prevents duplicate authentication

        public static async Task Main(string[] args)
        {
            // 1️⃣ Load Configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddIniFile("appsettings.ini", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // 2️⃣ Setup Dependency Injection
            var services = new ServiceCollection()
                .AddLogging(builder =>
                {
                    builder.AddConsole();
                    builder.SetMinimumLevel(LogLevel.Information); // ✅ Reduce log verbosity
                })
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton<CNCAuthService>()
                .AddSingleton<CNCLiveDataPublisher>()
                .BuildServiceProvider();

            // 3️⃣ Retrieve Loggers
            var programLogger = services.GetRequiredService<ILogger<Program>>();
            var cncAuthLogger = services.GetRequiredService<ILogger<CNCAuthService>>();
            var cncLiveLogger = services.GetRequiredService<ILogger<CNCLiveDataPublisher>>();

            programLogger.LogInformation("🚀 CNC1 MQTT Script starting...");

            // 4️⃣ Setup MQTT Client
            string mqttHost = configuration["MQTT:Host"]?.Replace("mqtt://", "") ?? "152.70.157.193";
            int mqttPort = int.TryParse(configuration["MQTT:Port"], out var port) ? port : 1883;
            string mqttUser = configuration["MQTT:Username"] ?? "Truman";
            string mqttPass = configuration["MQTT:Password"] ?? "Gotigers";

            var mqttClientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(mqttHost, mqttPort)
                .WithCredentials(mqttUser, mqttPass)
                .WithCleanSession()
                .Build();

            var managedOptions = new ManagedMqttClientOptionsBuilder()
                .WithClientOptions(mqttClientOptions)
                .Build();

            var mqttClient = new MqttFactory().CreateManagedMqttClient();
            await mqttClient.StartAsync(managedOptions);
            programLogger.LogInformation("✅ MQTT client connected.");

            // 5️⃣ Initialize CNCAuthService
            var cncAuthService = new CNCAuthService(cncAuthLogger, mqttClient)
            {
                Host = null,
                Username = null,
                Password = null,
                Token = null
            };

            await cncAuthService.SubscribeToMqttTokenAsync(); // Subscribe to token topic

            // 6️⃣ Subscribe to CNC Server Credentials
            await mqttClient.SubscribeAsync("CNCS/CNC1/Server/Host");
            await mqttClient.SubscribeAsync("CNCS/CNC1/Server/Username");
            await mqttClient.SubscribeAsync("CNCS/CNC1/Server/Password");

            programLogger.LogInformation("📡 Listening for CNC server credentials...");

            // 7️⃣ Handle Receiving CNC Credentials via MQTT
            mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                if (_authenticationCompleted) return; // ✅ Avoid duplicate authentication

                string topic = e.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.ToArray()).Trim();

                if (string.IsNullOrWhiteSpace(payload))
                {
                    programLogger.LogWarning($"⚠️ Received empty payload for topic: {topic}");
                    return;
                }

                switch (topic)
                {
                    case "CNCS/CNC1/Server/Host":
                        cncAuthService.Host = payload;
                        break;
                    case "CNCS/CNC1/Server/Username":
                        cncAuthService.Username = payload;
                        break;
                    case "CNCS/CNC1/Server/Password":
                        cncAuthService.Password = payload;
                        break;
                }

                // Proceed when all credentials are received
                if (!string.IsNullOrEmpty(cncAuthService.Host) &&
                    !string.IsNullOrEmpty(cncAuthService.Username) &&
                    !string.IsNullOrEmpty(cncAuthService.Password))
                {
                    _authenticationCompleted = true;
                    programLogger.LogInformation("🔑 CNC Credentials received. Authenticating...");

                    var token = await cncAuthService.GetAuthTokenAsync();
                    if (!string.IsNullOrEmpty(token))
                    {
                        await cncAuthService.PublishTokenAsync(token);
                        programLogger.LogInformation("📨 CNC Token published.");

                        // 8️⃣ Establish Socket.IO Connection
                        var socket = new SocketIOClient.SocketIO(cncAuthService.Host, new SocketIOClient.SocketIOOptions
                        {
                            Path = "/socket.io/",
                            EIO = EngineIO.V3,
                            AutoUpgrade = false,
                            Transport = TransportProtocol.WebSocket,
                            ExtraHeaders = new Dictionary<string, string>
                            {
                                { "Authorization", $"Bearer {token}" }
                            }
                        });

                        // 9️⃣ Handle Socket.IO Errors & Reconnection
                        socket.OnError += async (sender, e) =>
                        {
                            string err = e ?? "Unknown Error";
                            programLogger.LogError($"❌ [Socket.IO] Error: {err}");

                            if (err.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                                err.Contains("401", StringComparison.OrdinalIgnoreCase))
                            {
                                programLogger.LogWarning("🔄 Re-authenticating Socket.IO...");
                                var newToken = await cncAuthService.GetAuthTokenAsync();
                                if (!string.IsNullOrEmpty(newToken))
                                {
                                    await cncAuthService.PublishTokenAsync(newToken);
                                    socket.Options.ExtraHeaders["Authorization"] = $"Bearer {newToken}";

                                    try
                                    {
                                        await socket.DisconnectAsync(); // Clean reconnect
                                        await socket.ConnectAsync();
                                        programLogger.LogInformation("🔗 Socket.IO reconnected.");
                                    }
                                    catch (Exception ex)
                                    {
                                        programLogger.LogError($"❌ [Socket.IO] Reconnect failed: {ex.Message}");
                                    }
                                }
                            }
                        };

                        // 🔟 Connect Socket.IO
                        try
                        {
                            await socket.ConnectAsync();
                            programLogger.LogInformation("✅ Socket.IO connected.");
                        }
                        catch (Exception ex)
                        {
                            programLogger.LogError($"❌ [Socket.IO] Connection failed: {ex.Message}");
                        }

                        // 🔟 Start CNCLiveDataPublisher
                        var liveDataPublisher = new CNCLiveDataPublisher(cncLiveLogger, mqttClient, socket);
                        await liveDataPublisher.StartAsync();

                        programLogger.LogInformation("🏁 Application running. Press Ctrl+C to exit.");
                        await Task.Delay(-1); // Keep running
                    }
                }
            };
        }
    }
}
