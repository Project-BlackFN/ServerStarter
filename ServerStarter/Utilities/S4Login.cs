using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ServerStarter.Utilities
{
    public class S4Login
    {
        private const string HostProtocol = "https";
        private const string HostIp = "ols.blackfn.ghost143.de";
        private const int HostPort = 443;

        private const string LocalIp = "127.0.0.1";
        private const int LocalPort = 8010;

        private static HttpListener _listener;
        private static readonly object _lock = new object();

        public static async Task<Dictionary<string, object>> StartServerAsync()
        {
            try
            {
                lock (_lock)
                {
                    if (_listener != null && _listener.IsListening)
                    {
                        return new Dictionary<string, object> { { "success", false }, { "error", "Server already running" } };
                    }

                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://{LocalIp}:{LocalPort}/");
                    _listener.Start();
                }

                _ = Task.Run(async () =>
                {
                    while (_listener != null && _listener.IsListening)
                    {
                        try
                        {
                            var context = await _listener.GetContextAsync();
                            _ = Task.Run(() => HandleRequestAsync(context));
                        }
                        catch (HttpListenerException)
                        {
                            break;
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Listener error: {ex.Message}");
                        }
                    }
                });

                return new Dictionary<string, object> { { "success", true } };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "success", false }, { "error", ex.Message } };
            }
        }

        public static async Task<Dictionary<string, object>> StopServerAsync()
        {
            try
            {
                lock (_lock)
                {
                    if (_listener == null)
                    {
                        return new Dictionary<string, object> { { "success", false }, { "error", "Server not running" } };
                    }

                    _listener.Stop();
                    _listener.Close();
                    _listener = null;
                }

                await Task.Delay(100);
                return new Dictionary<string, object> { { "success", true } };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "success", false }, { "error", ex.Message } };
            }
        }

        private static async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                var targetUrl = $"{HostProtocol}://{HostIp}:{HostPort}{request.Url.PathAndQuery}";
                var proxyRequest = (HttpWebRequest)WebRequest.Create(targetUrl);
                proxyRequest.Method = request.HttpMethod;
                proxyRequest.Timeout = 30000;
                proxyRequest.ReadWriteTimeout = 30000;
                proxyRequest.AllowAutoRedirect = true;

                CopyRequestHeaders(request, proxyRequest);

                if (request.ContentLength64 > 0)
                {
                    proxyRequest.ContentLength = request.ContentLength64;
                    using (var requestStream = await proxyRequest.GetRequestStreamAsync())
                    {
                        await request.InputStream.CopyToAsync(requestStream);
                    }
                }

                using (var proxyResponse = (HttpWebResponse)await proxyRequest.GetResponseAsync())
                {
                    response.StatusCode = (int)proxyResponse.StatusCode;
                    CopyResponseHeaders(proxyResponse, response);

                    using (var proxyStream = proxyResponse.GetResponseStream())
                    {
                        await proxyStream.CopyToAsync(response.OutputStream);
                    }
                }
            }
            catch (WebException ex)
            {
                await HandleWebExceptionAsync(ex, response);
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                await HandleSocketExceptionAsync(ex, response);
            }
            catch (Exception ex)
            {
                await HandleGeneralExceptionAsync(ex, response);
            }
            finally
            {
                try
                {
                    response.Close();
                }
                catch { }
            }
        }

        private static void CopyRequestHeaders(HttpListenerRequest request, HttpWebRequest proxyRequest)
        {
            foreach (string headerName in request.Headers.AllKeys)
            {
                if (headerName.Equals("host", StringComparison.OrdinalIgnoreCase) ||
                    headerName.Equals("connection", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    switch (headerName.ToLowerInvariant())
                    {
                        case "content-type":
                            proxyRequest.ContentType = request.Headers[headerName];
                            break;
                        case "accept":
                            proxyRequest.Accept = request.Headers[headerName];
                            break;
                        case "user-agent":
                            proxyRequest.UserAgent = request.Headers[headerName];
                            break;
                        case "referer":
                            proxyRequest.Referer = request.Headers[headerName];
                            break;
                        case "content-length":
                            break;
                        default:
                            proxyRequest.Headers[headerName] = request.Headers[headerName];
                            break;
                    }
                }
                catch { }
            }
        }

        private static void CopyResponseHeaders(HttpWebResponse proxyResponse, HttpListenerResponse response)
        {
            foreach (string headerName in proxyResponse.Headers.AllKeys)
            {
                if (headerName.Equals("connection", StringComparison.OrdinalIgnoreCase) ||
                    headerName.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase) ||
                    headerName.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    response.Headers[headerName] = proxyResponse.Headers[headerName];
                }
                catch { }
            }
        }

        private static async Task HandleWebExceptionAsync(WebException ex, HttpListenerResponse response)
        {
            if (ex.Response != null)
            {
                try
                {
                    using (var errorResponse = (HttpWebResponse)ex.Response)
                    {
                        response.StatusCode = (int)errorResponse.StatusCode;
                        using (var errorStream = errorResponse.GetResponseStream())
                        {
                            await errorStream.CopyToAsync(response.OutputStream);
                        }
                    }
                }
                catch
                {
                    await WriteErrorResponseAsync(response, HttpStatusCode.BadGateway, "Proxy error occurred");
                }
            }
            else
            {
                await WriteEpicGamesErrorAsync(response);
            }
        }

        private static async Task HandleSocketExceptionAsync(System.Net.Sockets.SocketException ex, HttpListenerResponse response)
        {
            await WriteErrorResponseAsync(response, HttpStatusCode.BadGateway, $"Connection Error: {ex.Message}");
        }

        private static async Task HandleGeneralExceptionAsync(Exception ex, HttpListenerResponse response)
        {
            await WriteErrorResponseAsync(response, HttpStatusCode.InternalServerError, $"Server Error: {ex.Message}");
        }

        private static async Task WriteEpicGamesErrorAsync(HttpListenerResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            response.ContentType = "application/json";

            var errorObject = new
            {
                errorCode = "errors.com.epicgames.common.not_found",
                errorMessage = "Sorry, the resource you are trying to access could not be found",
                numericErrorCode = 1004,
                originatingService = "any",
                intent = "prod",
                error_description = "Sorry, the resource you are trying to access could not be found"
            };

            var errorJson = JsonSerializer.Serialize(errorObject);
            var buffer = Encoding.UTF8.GetBytes(errorJson);
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }

        private static async Task WriteErrorResponseAsync(HttpListenerResponse response, HttpStatusCode statusCode, string message)
        {
            response.StatusCode = (int)statusCode;
            response.ContentType = "text/plain";
            var buffer = Encoding.UTF8.GetBytes(message);
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }
    }
}