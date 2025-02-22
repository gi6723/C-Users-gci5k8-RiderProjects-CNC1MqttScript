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
        private static bool _authenticationCompleted = false; // ✅ Step 6 - Prevent multiple auth attempts

        public static async Task Main(string[] args)
        {
            // 1️⃣ Build configuration (INI + environment variables)
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddIniFile("appsettings.ini", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // 2️⃣ Create a DI container
            var services = new ServiceCollection()
                .AddLogging(builder =>
                {
                    builder.AddConsole();
                    builder.SetMinimumLevel(LogLevel.Debug);
                })
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton<CNCAuthService>()
                .AddSingleton<CNCLiveDataPublisher>()
                .BuildServiceProvider();

            // 3️⃣ Retrieve loggers
            var programLogger = services.GetRequiredService<ILogger<Program>>();
            var cncAuthLogger = services.GetRequiredService<ILogger<CNCAuthService>>();
            var cncLiveLogger = services.GetRequiredService<ILogger<CNCLiveDataPublisher>>();

            programLogger.LogInformation("Starting application...");

            // 4️⃣ Initialize MQTT client
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

            programLogger.LogInformation("MQTT client started and connected.");

            // 5️⃣ Initialize CNCAuthService with typed logger + MQTT client
            var cncAuthService = new CNCAuthService(cncAuthLogger, mqttClient);
            await cncAuthService.SubscribeToMqttTokenAsync(); // Ensure we subscribe to MqttToken topic

            // 6️⃣ Subscribe to CNC server credentials
            await mqttClient.SubscribeAsync("CNCS/CNC1/Server/Host");
            await mqttClient.SubscribeAsync("CNCS/CNC1/Server/Username");
            await mqttClient.SubscribeAsync("CNCS/CNC1/Server/Password");

            // 7️⃣ Handle receiving CNC server credentials from MQTT
            mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                if (_authenticationCompleted) return; // ✅ Step 6 - Ensure authentication happens once

                string topic = e.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload).Trim();

                if (topic == "CNCS/CNC1/Server/Host") cncAuthService.Host = payload;
                if (topic == "CNCS/CNC1/Server/Username") cncAuthService.Username = payload;
                if (topic == "CNCS/CNC1/Server/Password") cncAuthService.Password = payload;

                // ✅ Step 6 - Proceed only when all credentials are received
                if (!string.IsNullOrEmpty(cncAuthService.Host) &&
                    !string.IsNullOrEmpty(cncAuthService.Username) &&
                    !string.IsNullOrEmpty(cncAuthService.Password))
                {
                    _authenticationCompleted = true;  // ✅ Prevent duplicate auth attempts
                    programLogger.LogInformation("[MQTT] CNC Credentials Received. Authenticating...");

                    var token = await cncAuthService.GetAuthTokenAsync();
                    if (!string.IsNullOrEmpty(token))
                    {
                        await cncAuthService.PublishTokenAsync(token);
                        programLogger.LogInformation("[MQTT] CNC Token Published.");

                        // 8️⃣ Establish Socket.IO connection
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

                        // 9️⃣ Handle Socket.IO errors & auto-reconnect
                        socket.OnError += (sender, e) =>
                        {
                            _ = Task.Run(async () =>
                            {
                                var err = e ?? string.Empty;
                                programLogger.LogError($"[Socket.IO Error] {err}");

                                if (err.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    err.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    programLogger.LogInformation("[Socket.IO] Re-authenticating...");
                                    var newToken = await cncAuthService.GetAuthTokenAsync();
                                    if (!string.IsNullOrEmpty(newToken))
                                    {
                                        await cncAuthService.PublishTokenAsync(newToken);
                                        socket.Options.ExtraHeaders["Authorization"] = $"Bearer {newToken}";

                                        try
                                        {
                                            await socket.DisconnectAsync(); // Ensure a clean slate
                                            await socket.ConnectAsync();
                                            programLogger.LogInformation("[Socket.IO] Reconnected after re-auth.");
                                        }
                                        catch (Exception ex)
                                        {
                                            programLogger.LogError($"[Socket.IO] Reconnect failed: {ex.Message}");
                                        }
                                    }
                                }
                            });
                        };

                        // 🔟 Connect the socket
                        try
                        {
                            await socket.ConnectAsync();
                            programLogger.LogInformation("Socket.IO connected successfully.");
                        }
                        catch (Exception ex)
                        {
                            programLogger.LogError($"[Socket.IO] Connection failed: {ex.Message}");
                        }

                        // 🔟 Start CNCLiveDataPublisher
                        var liveDataPublisher = new CNCLiveDataPublisher(cncLiveLogger, mqttClient, socket);
                        await liveDataPublisher.StartAsync();

                        programLogger.LogInformation("Application running. Press Ctrl+C to exit.");
                        await Task.Delay(-1); // Block indefinitely
                    }
                }
            };
        }
    }
}
