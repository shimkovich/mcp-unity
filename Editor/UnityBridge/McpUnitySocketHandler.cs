using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebSocketSharp;
using WebSocketSharp.Server;
using McpUnity.Tools;
using McpUnity.Resources;
using Unity.EditorCoroutines.Editor;
using System.Collections;
using System.Collections.Specialized;
using McpUnity.Utils;
using System.IO;

namespace McpUnity.Unity
{
    /// <summary>
    /// WebSocket handler for MCP Unity communications
    /// </summary>
    public class McpUnitySocketHandler : WebSocketBehavior
    {
        private readonly McpUnityServer _server;
        
        /// <summary>
        /// Default constructor required by WebSocketSharp
        /// </summary>
        public McpUnitySocketHandler(McpUnityServer server)
        {
            _server = server;
        }
        
        /// <summary>
        /// Create a standardized error response
        /// </summary>
        /// <param name="message">Error message</param>
        /// <param name="errorType">Type of error</param>
        /// <returns>A JObject containing the error information</returns>
        public static JObject CreateErrorResponse(string message, string errorType)
        {
            return new JObject
            {
                ["error"] = new JObject
                {
                    ["type"] = errorType,
                    ["message"] = message
                }
            };
        }
        
        /// <summary>
        /// File path for buffering responses that failed to send due to domain reload.
        /// Uses Library/ so it survives domain reload but is ignored by version control.
        /// </summary>
        internal static readonly string PendingResponsesPath =
            Path.Combine("Library", "McpPendingResponses.json");

        private static readonly object _bufferLock = new object();

        /// <summary>
        /// Buffer a response string to a file for delivery after reconnection.
        /// Thread-safe: can be called from WebSocket threads.
        /// </summary>
        internal static void BufferResponse(string responseStr)
        {
            lock (_bufferLock)
            {
                try
                {
                    var json = File.Exists(PendingResponsesPath)
                        ? File.ReadAllText(PendingResponsesPath)
                        : "[]";
                    var arr = JArray.Parse(json);
                    arr.Add(responseStr);
                    File.WriteAllText(PendingResponsesPath, arr.ToString(Formatting.None));
                    McpLogger.LogInfo("Buffered response for delivery after reconnection");
                }
                catch (Exception ex)
                {
                    McpLogger.LogError($"Failed to buffer response: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Drain all buffered responses from file and return them.
        /// Thread-safe: can be called from WebSocket threads.
        /// </summary>
        internal static List<string> DrainBufferedResponses()
        {
            lock (_bufferLock)
            {
                var list = new List<string>();
                try
                {
                    if (!File.Exists(PendingResponsesPath))
                    {
                        return list;
                    }
                    var json = File.ReadAllText(PendingResponsesPath);
                    File.Delete(PendingResponsesPath);
                    var arr = JArray.Parse(json);
                    foreach (var item in arr)
                    {
                        list.Add(item.ToString());
                    }
                }
                catch (Exception ex)
                {
                    McpLogger.LogError($"Failed to drain buffered responses: {ex.Message}");
                }
                return list;
            }
        }

        /// <summary>
        /// Handle incoming messages from WebSocket clients
        /// </summary>
        protected override async void OnMessage(MessageEventArgs e)
        {
            try
            {
                McpLogger.LogInfo($"WebSocket message received: {e.Data}");
                JObject requestJson;
                try
                {
                    requestJson = JObject.Parse(e.Data);
                }
                catch (JsonReaderException jre)
                {
                    McpLogger.LogError($"Invalid JSON received: {jre.Message}. Data: {e.Data}");
                    // Attempt to send a parse error response. No requestId is available yet.
                    Send(CreateResponse(null, CreateErrorResponse($"Invalid JSON format: {jre.Message}", "invalid_json")).ToString(Formatting.None));
                    return;
                }

                var method = requestJson["method"]?.ToString();
                var parameters = requestJson["params"] as JObject ?? new JObject();
                var requestId = requestJson["id"]?.ToString();
                // We need to dispatch to Unity's main thread and wait for completion
                var tcs = new TaskCompletionSource<JObject>();
                
                if (string.IsNullOrEmpty(method))
                {
                    tcs.SetResult(CreateErrorResponse("Missing method in request", "invalid_request"));
                }
                else if (_server.TryGetTool(method, out var tool))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(ExecuteTool(tool, parameters, tcs));
                }
                else if (_server.TryGetResource(method, out var resource))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(FetchResourceCoroutine(resource, parameters, tcs));
                }
                else
                {
                    tcs.SetResult(CreateErrorResponse($"Unknown method: {method}", "unknown_method"));
                }
                
                JObject responseJson = await tcs.Task;
                JObject jsonRpcResponse = CreateResponse(requestId, responseJson);
                string responseStr = jsonRpcResponse.ToString(Formatting.None);
                
                McpLogger.LogInfo($"WebSocket message response for request ID '{requestId}': {responseStr}");

                // Send the response back to the client.
                // websocket-sharp's Send() is asynchronous and does not throw on failure —
                // it fires OnError instead. So we must check connection state before sending.
                // If the WebSocket is not alive (e.g. domain reload killed it), buffer the response
                // so it can be flushed to the next client connection.
                if (Context.WebSocket?.IsAlive == true)
                {
                    Send(responseStr);
                }
                else
                {
                    McpLogger.LogWarning($"WebSocket not alive for '{requestId}', buffering for reconnection");
                    BufferResponse(responseStr);
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Error processing message: {ex.Message}");
                // Don't try to Send() here — connection may be dead during domain reload
            }
        }
        
        /// <summary>
        /// Handle WebSocket connection open.
        /// Closes any stale connections first to prevent file descriptor accumulation.
        /// websocket-sharp uses Mono's IOSelector/select(), which crashes when FD
        /// values exceed ~1024. Limiting to one active connection keeps FD usage bounded.
        /// See: https://github.com/CoderGamester/mcp-unity/issues/110
        /// </summary>
        protected override void OnOpen()
        {
            // Close any existing connections — MCP Unity is designed for one client at a time.
            // This prevents file descriptor accumulation from reconnection cycles.
            var staleIds = _server.Clients.Keys
                .Where(id => id != ID)
                .ToList();

            if (staleIds.Count > 0)
            {
                foreach (var oldId in staleIds)
                {
                    try
                    {
                        Sessions.CloseSession(oldId, CloseStatusCode.Normal, "Replaced by new connection");
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogWarning($"Error closing stale session {oldId}: {ex.Message}");
                    }
                }
                McpLogger.LogInfo($"Closed {staleIds.Count} stale connection(s) to accept new client");
            }

            // Extract client name from the X-Client-Name header (if available)
            string clientName = "";
            NameValueCollection headers = Context.Headers;
            if (headers != null && headers.Contains("X-Client-Name"))
            {
                clientName = headers["X-Client-Name"];
            }

            // Always add the client to the server's tracking dictionary
            _server.Clients[ID] = clientName;

            McpLogger.LogInfo($"WebSocket client connected (ID: {ID}, Name: {(string.IsNullOrEmpty(clientName) ? "Unknown" : clientName)})");

            // Flush any responses that were buffered during domain reload
            var buffered = DrainBufferedResponses();
            if (buffered.Count > 0)
            {
                McpLogger.LogInfo($"Flushing {buffered.Count} buffered response(s) to reconnected client");
                foreach (var resp in buffered)
                {
                    try
                    {
                        Send(resp);
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogWarning($"Failed to flush buffered response: {ex.Message}");
                    }
                }
            }
        }
        
        /// <summary>
        /// Handle WebSocket connection close
        /// </summary>
        protected override void OnClose(CloseEventArgs e)
        {
            _server.Clients.TryGetValue(ID, out string clientName);
            
            // Remove the client from the server
            _server.Clients.Remove(ID);
            
            McpLogger.LogInfo($"WebSocket client '{clientName}' disconnected: {e.Reason}");
        }
        
        /// <summary>
        /// Handle WebSocket errors
        /// </summary>
        protected override void OnError(WebSocketSharp.ErrorEventArgs e)
        {
            McpLogger.LogError($"WebSocket error: {e.Message}");
        }
        
        /// <summary>
        /// Execute a tool with the provided parameters
        /// </summary>
        private IEnumerator ExecuteTool(McpToolBase tool, JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            try
            {
                if (tool.IsAsync)
                {
                    tool.ExecuteAsync(parameters, tcs);
                }
                else
                {
                    var result = tool.Execute(parameters);
                    tcs.SetResult(result);
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Error executing tool {tool.Name}: {ex.Message}\n{ex.StackTrace}");
                tcs.SetResult(CreateErrorResponse(
                    $"Failed to execute tool {tool.Name}: {ex.Message}",
                    "tool_execution_error"
                ));
            }
            
            yield return null;
        }
        
        /// <summary>
        /// Fetch a resource with the provided parameters
        /// </summary>
        private IEnumerator FetchResourceCoroutine(McpResourceBase resource, JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            try
            {
                if (resource.IsAsync)
                {
                    resource.FetchAsync(parameters, tcs);
                }
                else
                {
                    var result = resource.Fetch(parameters);
                    tcs.SetResult(result);
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Error fetching resource {resource.Name}: {ex.Message}\n{ex.StackTrace}");
                tcs.SetResult(CreateErrorResponse(
                    $"Failed to fetch resource {resource.Name}: {ex.Message}",
                    "resource_fetch_error"
                ));
            }
            yield return null;
        }
        
        /// <summary>
        /// Create a JSON-RPC 2.0 response
        /// </summary>
        /// <param name="requestId">Request ID</param>
        /// <param name="result">Result object</param>
        /// <returns>JSON-RPC 2.0 response</returns>
        private JObject CreateResponse(string requestId, JObject result)
        {
            // Format as JSON-RPC 2.0 response
            JObject jsonRpcResponse = new JObject
            {
                ["id"] = requestId
            };
            
            // Add result or error
            if (result.TryGetValue("error", out var errorObj))
            {
                jsonRpcResponse["error"] = errorObj;
            }
            else
            {
                jsonRpcResponse["result"] = result;
            }
            
            return jsonRpcResponse;
        }
    }
}
