using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using SocketIOClient;

namespace CNC1MqttScript
{
    public class CNCLiveDataPublisher
    {
        private readonly ILogger<CNCLiveDataPublisher> _logger;
        private readonly IManagedMqttClient _mqttClient;
        private readonly SocketIOClient.SocketIO _socket;

        public CNCLiveDataPublisher(
            ILogger<CNCLiveDataPublisher> logger,
            IManagedMqttClient mqttClient,
            SocketIOClient.SocketIO socket)
        {
            _logger = logger;
            _mqttClient = mqttClient;
            _socket = socket;
        }

        public void Start()
        {
            _socket.OnAny((string eventName, SocketIOResponse response) =>
            {
                _logger.LogInformation($"[DEBUG OnAny] Event: {eventName}, Data: {response}");
                // Process each event asynchronously.
                Task.Run(() => HandleGenericEvent(eventName, response));
            });

            _logger.LogInformation("📡 Universal CNC event listener is active.");
        }

        private async Task HandleGenericEvent(string eventName, SocketIOResponse response)
        {
            try
            {
                switch (eventName)
                {
                    case "workflow:state":
                        await HandleWorkflowState(response, eventName);
                        break;
                    case "sender:status":
                        await HandleSenderStatus(response, eventName);
                        break;
                    case "controller:state":
                        await HandleControllerState(response, eventName);
                        break;
                    default:
                        string rawJson = response.ToString();
                        await PublishBulk(eventName, rawJson);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
        }

        private async Task PublishBulk(string eventName, string rawJson)
        {
            try
            {
                string topic = $"CNCS/CNC1/Data/Bulk/{eventName}";
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(rawJson)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce)
                    .WithRetainFlag()
                    .Build();

                await _mqttClient.EnqueueAsync(message);
                _logger.LogInformation($"📤 [Bulk] Published '{eventName}' to topic {topic}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error publishing Bulk event '{eventName}': {ex.Message}");
            }
        }

        // --------------------------------------
        // Workflow State (unchanged)
        // --------------------------------------
        private async Task HandleWorkflowState(SocketIOResponse response, string eventName)
        {
            try
            {
                var json = response.GetValue<JsonElement>();
                string state = "unknown";

                _logger.LogInformation($"🔍 Parsing workflow state: {json}");

                // 1️⃣ If the JSON itself is a string, use it directly.
                if (json.ValueKind == JsonValueKind.String)
                {
                    state = json.GetString() ?? "unknown";
                    _logger.LogInformation($"✅ Found workflow state as string: {state}");
                }
                // 2️⃣ If JSON is an array, extract the first element if it is a string.
                else if (json.ValueKind == JsonValueKind.Array && json.GetArrayLength() > 0)
                {
                    JsonElement firstElement = json[0];
                    if (firstElement.ValueKind == JsonValueKind.String)
                    {
                        state = firstElement.GetString() ?? "unknown";
                        _logger.LogInformation($"✅ Found workflow state in array: {state}");
                    }
                }
                // 3️⃣ If JSON is an object, try dictionary deserialization.
                else if (json.ValueKind == JsonValueKind.Object)
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json.GetRawText());
                    if (dict != null && dict.TryGetValue("workflow:state", out JsonElement stateElement))
                    {
                        state = stateElement.GetString() ?? "unknown";
                        _logger.LogInformation($"✅ Found workflow state in dictionary: {state}");
                    }
                }

                // 4️⃣ Fallback: Use recursive search if still unknown.
                if (state == "unknown" && RecursiveSearch(json, "workflow:state", out JsonElement foundState))
                {
                    state = foundState.GetString() ?? "unknown";
                    _logger.LogInformation($"✅ Found workflow state using recursive search: {state}");
                }

                if (state == "unknown")
                {
                    _logger.LogWarning("⚠️ Workflow state was not found, returning 'unknown'.");
                }

                await PublishGuiData("WorkflowState", state);
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError($"❌ JSON Error parsing 'workflow:state': {jsonEx.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error parsing 'workflow:state': {ex.Message}");
            }
        }

        // --------------------------------------
        // Sender Status (unchanged except for "long" -> "double" if needed)
        // --------------------------------------
        private async Task HandleSenderStatus(SocketIOResponse response, string eventName)
        {
            try
            {
                var json = response.GetValue<JsonElement>();

                string gcodeFileName = "";
                double total = 0, sent = 0, received = 0; 
                // Changed these to double, in case the CNC sends "650.0" or similar
                string elapsedTime = "", remainingTime = "";

                // (The rest of your existing logic is fine; 
                // just replaced "long" with "double" 
                // and used double.TryParse(...) in your parse code.)

                // 1️⃣ If JSON is an object, try dictionary extraction.
                if (json.ValueKind == JsonValueKind.Object)
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json.GetRawText());
                    if (dict != null)
                    {
                        if (dict.TryGetValue("name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String)
                            gcodeFileName = nameEl.GetString() ?? "";

                        if (dict.TryGetValue("total", out JsonElement totalEl))
                            total = ConvertToDouble(totalEl);

                        if (dict.TryGetValue("sent", out JsonElement sentEl))
                            sent = ConvertToDouble(sentEl);

                        if (dict.TryGetValue("received", out JsonElement receivedEl))
                            received = ConvertToDouble(receivedEl);

                        // For time fields, allow numeric values.
                        if (dict.TryGetValue("elapsedTime", out JsonElement elapsedEl))
                            elapsedTime = ConvertToString(elapsedEl);

                        if (dict.TryGetValue("remainingTime", out JsonElement remainingEl))
                            remainingTime = ConvertToString(remainingEl);
                    }
                }
                // 2️⃣ If JSON is an array, iterate through items.
                else if (json.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in json.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.GetRawText());
                            if (dict != null)
                            {
                                if (string.IsNullOrEmpty(gcodeFileName) && 
                                    dict.TryGetValue("name", out JsonElement nameEl) && 
                                    nameEl.ValueKind == JsonValueKind.String)
                                    gcodeFileName = nameEl.GetString() ?? "";

                                if (Math.Abs(total) < 0.00001 && dict.TryGetValue("total", out JsonElement totalEl))
                                    total = ConvertToDouble(totalEl);

                                if (Math.Abs(sent) < 0.00001 && dict.TryGetValue("sent", out JsonElement sentEl))
                                    sent = ConvertToDouble(sentEl);

                                if (Math.Abs(received) < 0.00001 && dict.TryGetValue("received", out JsonElement receivedEl))
                                    received = ConvertToDouble(receivedEl);

                                if (string.IsNullOrEmpty(elapsedTime) && 
                                    dict.TryGetValue("elapsedTime", out JsonElement elapsedEl))
                                    elapsedTime = ConvertToString(elapsedEl);

                                if (string.IsNullOrEmpty(remainingTime) && 
                                    dict.TryGetValue("remainingTime", out JsonElement remainingEl))
                                    remainingTime = ConvertToString(remainingEl);
                            }
                        }
                    }
                }

                // 3️⃣ Fallback: Use recursive search if any field is still missing.
                if (string.IsNullOrEmpty(gcodeFileName) && RecursiveSearch(json, "name", out JsonElement foundName))
                    gcodeFileName = foundName.GetString() ?? "";

                if (Math.Abs(total) < 0.00001 && RecursiveSearch(json, "total", out JsonElement foundTotal))
                    total = ConvertToDouble(foundTotal);

                if (Math.Abs(sent) < 0.00001 && RecursiveSearch(json, "sent", out JsonElement foundSent))
                    sent = ConvertToDouble(foundSent);

                if (Math.Abs(received) < 0.00001 && RecursiveSearch(json, "received", out JsonElement foundReceived))
                    received = ConvertToDouble(foundReceived);

                if (string.IsNullOrEmpty(elapsedTime) && RecursiveSearch(json, "elapsedTime", out JsonElement foundElapsed))
                    elapsedTime = ConvertToString(foundElapsed);

                if (string.IsNullOrEmpty(remainingTime) && RecursiveSearch(json, "remainingTime", out JsonElement foundRemaining))
                    remainingTime = ConvertToString(foundRemaining);

                // Publish the extracted data.
                await PublishGuiData("SelectedGcodeFile", gcodeFileName);
                await PublishGuiData("TotalGcodeCommands", total);
                await PublishGuiData("SentGcodeCommands", sent);
                await PublishGuiData("ProccessedGcodeCommands", received);
                await PublishGuiData("ElapsedTime", elapsedTime);
                await PublishGuiData("RemainingTime", remainingTime);
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error parsing 'senderStatus': {ex.Message}");
            }
        }

        // --------------------------------------
        // Controller State Handler w/ Debug Logs
        // --------------------------------------
        private async Task HandleControllerState(SocketIOResponse response, string eventName)
        {
            try
            {
                // 1) First argument is a string, e.g. "Grbl"
                string firstArg = response.GetValue<string>(0);   
                // 2) Second argument is the object with "status" and "parserstate"
                JsonElement secondArg = response.GetValue<JsonElement>(1);

                _logger.LogInformation($"[ControllerState] firstArg = {firstArg}");
                _logger.LogInformation($"[ControllerState] secondArg = {secondArg}");

                // Now parse the secondArg for 'status' / 'feedrate' / etc.
                double feedrate = 0;
                double spindleSpeed = 0;
                string activeState = "unknown";

                if (secondArg.ValueKind == JsonValueKind.Object &&
                    secondArg.TryGetProperty("status", out JsonElement status) &&
                    status.ValueKind == JsonValueKind.Object)
                {
                    // activeState
                    if (status.TryGetProperty("activeState", out JsonElement activeStateEl) &&
                        activeStateEl.ValueKind == JsonValueKind.String)
                    {
                        activeState = activeStateEl.GetString() ?? "unknown";
                    }

                    // feedrate
                    if (status.TryGetProperty("feedrate", out JsonElement feedEl))
                    {
                        feedrate = ConvertToDouble(feedEl);
                    }

                    // spindle
                    if (status.TryGetProperty("spindle", out JsonElement spindleEl))
                    {
                        spindleSpeed = ConvertToDouble(spindleEl);
                    }
                }
                else
                {
                    _logger.LogInformation("Could not find 'status' property in secondArg.");
                }

                // If needed, do fallback recursive search
                if (Math.Abs(feedrate) < 0.00001 && RecursiveSearch(secondArg, "feedrate", out JsonElement foundFeedrate))
                    feedrate = ConvertToDouble(foundFeedrate);

                if (Math.Abs(spindleSpeed) < 0.00001 && RecursiveSearch(secondArg, "spindle", out JsonElement foundSpindle))
                    spindleSpeed = ConvertToDouble(foundSpindle);

                if (activeState == "unknown" &&
                    RecursiveSearch(secondArg, "activeState", out JsonElement foundActiveState) &&
                    foundActiveState.ValueKind == JsonValueKind.String)
                {
                    activeState = foundActiveState.GetString() ?? "unknown";
                }

                _logger.LogInformation($"[ControllerState] Final => feedrate={feedrate}, spindleSpeed={spindleSpeed}, activeState={activeState}");

                await PublishGuiData("FeedRate", feedrate);
                await PublishGuiData("SpindleSpeed", spindleSpeed);
                await PublishGuiData("ActiveState", activeState);
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error parsing JSON in controller state: {ex.Message}");
            }
        }



        // --------------------------------------
        // Helper: Convert a JsonElement to double
        // --------------------------------------
        private double ConvertToDouble(JsonElement element)
        {
            try
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Number:
                        // If the number is something like 650.0, we can parse it as double
                        if (element.TryGetDouble(out double dblVal))
                            return dblVal;
                        break;
                    case JsonValueKind.String:
                        // Attempt to parse as double from the string
                        if (double.TryParse(element.GetString(), out double dblParse))
                            return dblParse;
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[ConvertToDouble] Could not parse element '{element}' => {ex.Message}");
            }
            return 0; // default if parse fails
        }

        // --------------------------------------
        // Helper: Convert a JsonElement to string
        // --------------------------------------
        private string ConvertToString(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString() ?? "";
                case JsonValueKind.Number:
                    // e.g., "650.0" or "11000.0"
                    return element.GetRawText(); 
                default:
                    return "";
            }
        }

        // --------------------------------------
        // Recursive search function
        // --------------------------------------
        private static bool RecursiveSearch(JsonElement element, string key, out JsonElement foundElement)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty(key, out foundElement))
                    return true;

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (RecursiveSearch(property.Value, key, out foundElement))
                        return true;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in element.EnumerateArray())
                {
                    if (RecursiveSearch(child, key, out foundElement))
                        return true;
                }
            }
            foundElement = default;
            return false;
        }

        // --------------------------------------
        // PublishGuiData
        // --------------------------------------
        private async Task PublishGuiData(string eventName, object data)
        {
            try
            {
                string topic = $"CNCS/CNC1/Data/GUI/{eventName}";
                string payload = JsonSerializer.Serialize(data);
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce)
                    .WithRetainFlag()
                    .Build();
                await _mqttClient.EnqueueAsync(message);
                _logger.LogInformation($"📤 [GUI] Published updated GUI data to {data}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error publishing GUI data: {ex.Message}");
            }
        }
    }
}
