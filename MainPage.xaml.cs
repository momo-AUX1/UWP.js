using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel;
using Windows.Devices.Geolocation;
using Windows.Devices.PointOfService;
using Windows.Devices.Sensors;
using Windows.Graphics.Display;
using Windows.Media.Capture;
using Windows.Media.SpeechSynthesis;
using Windows.Networking.Connectivity;
using Windows.Networking.PushNotifications;
using Windows.Security.Credentials;
using Windows.Security.Credentials.UI;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.System.Power;
using Windows.System.Profile;
using Windows.System.UserProfile;
using Windows.UI;
using Windows.UI.Popups;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using System.IO.Compression;
using Windows.ApplicationModel.Core;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Core;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Xaml.Input;
using Windows.Gaming.Input;
using Windows.Foundation;

namespace UWP.js
{
    public sealed partial class MainPage : Page
    {
        private CoreWebView2 _coreWebView2;
        private HttpClient _httpClient;
        private string _webContentRoot;
        private bool _cancelDownload;
        private Dictionary<string, string> customHeaders = new Dictionary<string, string>();
        private static Gamepad _currentGamepad;
        private static bool _gamepadSubscribed;
        private Uri _pendingProtocolUri;
        private Uri _lastProtocolUri;
        private Microsoft.UI.Xaml.Controls.WebView2 _browserWebView;
        private DispatcherTimer _toastTimer;
        private DataTransferManager _dataTransferManager;
        private Dictionary<string, object> _pendingShareData;
        private bool _backButtonHandlerEnabled = true;
        public static MainPage Current { get; private set; }

        public MainPage()
        {
            this.InitializeComponent();
            Current = this;
            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "151330");
            InitializeWebView();
            Window.Current.CoreWindow.SizeChanged += CoreWindow_SizeChanged;
            Window.Current.VisibilityChanged += Window_VisibilityChanged;
            _httpClient = new HttpClient();

            SystemNavigationManager.GetForCurrentView().BackRequested += App_BackRequested;
            RegisterSystemEventForwarders();
            if (!_gamepadSubscribed)
            {
                _currentGamepad = Gamepad.Gamepads.LastOrDefault();
                Gamepad.GamepadAdded += (sender, gamepad) => { _currentGamepad = gamepad; };
                Gamepad.GamepadRemoved += (sender, gamepad) =>
                {
                    if (_currentGamepad == gamepad)
                    {
                        _currentGamepad = Gamepad.Gamepads.LastOrDefault();
                    }
                };
                _gamepadSubscribed = true;
            }
        }

        public Gamepad CurrentGamepad => _currentGamepad;

        public void SetCustomHeaders(Dictionary<string, string> headers)
        {
            customHeaders = headers;
        }

        public void ClearCustomHeaders()
        {
            customHeaders.Clear();
        }

        private void RegisterSystemEventForwarders()
        {
            try
            {
                NetworkInformation.NetworkStatusChanged += async (_) =>
                {
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        PostEvent("networkStatusChange", BuildNetworkStatus());
                    });
                };
            }
            catch { }

            try
            {
                var display = DisplayInformation.GetForCurrentView();
                display.OrientationChanged += (_, __) =>
                {
                    PostEvent("screenOrientationChange", new { type = MapOrientation(display.CurrentOrientation) });
                };
            }
            catch { }

            try
            {
                var inputPane = InputPane.GetForCurrentView();
                inputPane.Showing += (_, args) =>
                {
                    var info = new { keyboardHeight = args.OccludedRect.Height };
                    PostEvent("keyboardWillShow", info);
                    PostEvent("keyboardDidShow", info);
                };
                inputPane.Hiding += (_, __) =>
                {
                    PostEvent("keyboardWillHide", new { });
                    PostEvent("keyboardDidHide", new { });
                };
            }
            catch { }

            try
            {
                _dataTransferManager = DataTransferManager.GetForCurrentView();
                _dataTransferManager.DataRequested += DataTransferManager_DataRequested;
            }
            catch { }
        }

        private void Window_VisibilityChanged(object sender, VisibilityChangedEventArgs e)
        {
            var data = new { isActive = e.Visible };
            PostEvent("appStateChange", data);
            PostEvent(e.Visible ? "resume" : "pause", new { });
            if (!e.Visible)
            {
                var _ = RegisterConfiguredScriptRunnerAsync();
            }
        }

        private async void InitializeWebView()
        {
            try
            {
                await WebView2.EnsureCoreWebView2Async();
                _coreWebView2 = WebView2.CoreWebView2;

                var folder = await Package.Current.InstalledLocation.GetFolderAsync("Assets");
                var webContentFolder = await folder.GetFolderAsync("WP");
                _webContentRoot = Path.GetFullPath(webContentFolder.Path);
                var localFolder = ApplicationData.Current.LocalFolder;
                var needsIndex = false;

                _coreWebView2.SetVirtualHostNameToFolderMapping("localhost", webContentFolder.Path, CoreWebView2HostResourceAccessKind.Allow);
                _coreWebView2.SetVirtualHostNameToFolderMapping("localdata", localFolder.Path, CoreWebView2HostResourceAccessKind.Allow);
                _coreWebView2.SetVirtualHostNameToFolderMapping("selectedfiles", localFolder.Path, CoreWebView2HostResourceAccessKind.Allow);

                _coreWebView2.WebMessageReceived += WebView2_WebMessageReceived;
                _coreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                _coreWebView2.WebResourceRequested += WebView2_WebResourceRequested;
                _coreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                _coreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                _coreWebView2.PermissionRequested += CoreWebView2_PermissionRequested;

                var indexFile = await webContentFolder.TryGetItemAsync("index.html") as StorageFile;
                if (indexFile == null && needsIndex)
                {
                    await new MessageDialog("index.html not found in the WP folder.").ShowAsync();
                    return;
                }

                WebView2.Source = new Uri("http://localhost/index.html");
            }
            catch (Exception ex)
            {
                await new MessageDialog($"Failed to initialize WebView2: {ex.Message}").ShowAsync();
            }
        }

        private async void CoreWebView2_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            args.Handled = true;
            if (!string.IsNullOrWhiteSpace(args.Uri))
            {
                await ShowBrowserOverlayAsync(args.Uri);
            }
        }

        private void CoreWebView2_PermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
        {
            args.State = CoreWebView2PermissionState.Allow;
        }

        public async Task RunOnUiThreadAsync(Func<Task> action)
        {
            var tcs = new TaskCompletionSource<bool>();
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try
                {
                    await action();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            await tcs.Task;
        }

        public async Task ShowBrowserOverlayAsync(string url)
        {
            await RunOnUiThreadAsync(async () =>
            {
                BrowserOverlayHost.Children.Clear();
                BrowserOverlayHost.Visibility = Visibility.Visible;

                var panel = new Windows.UI.Xaml.Controls.Grid
                {
                    Width = 640,
                    Height = 640,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Colors.Black)
                };
                panel.RowDefinitions.Add(new Windows.UI.Xaml.Controls.RowDefinition { Height = new GridLength(48) });
                panel.RowDefinitions.Add(new Windows.UI.Xaml.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var topBar = new Windows.UI.Xaml.Controls.Grid
                {
                    Background = new SolidColorBrush(Color.FromArgb(255, 24, 24, 24))
                };
                var closeButton = new Windows.UI.Xaml.Controls.Button
                {
                    Content = "Close",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0)
                };
                closeButton.Click += async (_, __) => { await CloseBrowserOverlayAsync(); };
                topBar.Children.Add(closeButton);
                Windows.UI.Xaml.Controls.Grid.SetRow(topBar, 0);
                panel.Children.Add(topBar);

                _browserWebView = new Microsoft.UI.Xaml.Controls.WebView2();
                Windows.UI.Xaml.Controls.Grid.SetRow(_browserWebView, 1);
                panel.Children.Add(_browserWebView);

                BrowserOverlayHost.Children.Add(panel);

                await _browserWebView.EnsureCoreWebView2Async();
                _browserWebView.CoreWebView2.NavigationCompleted += (_, __) =>
                {
                    PostEvent("browserPageLoaded", new { url = _browserWebView.Source?.ToString() });
                };
                _browserWebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                _browserWebView.Source = new Uri(url);
            });
        }

        public async Task CloseBrowserOverlayAsync()
        {
            await RunOnUiThreadAsync(() =>
            {
                BrowserOverlayHost.Children.Clear();
                BrowserOverlayHost.Visibility = Visibility.Collapsed;
                _browserWebView = null;
                PostEvent("browserFinished", new { });
                return Task.CompletedTask;
            });
        }

        public async Task ShowInAppToastAsync(string title, string text, int durationMs = 2500)
        {
            await RunOnUiThreadAsync(() =>
            {
                _toastTimer?.Stop();
                ToastOverlayHost.Children.Clear();

                var border = new Windows.UI.Xaml.Controls.Border
                {
                    MaxWidth = 720,
                    Background = new SolidColorBrush(Color.FromArgb(235, 24, 24, 24)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(20, 14, 20, 14),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(24)
                };

                var stack = new Windows.UI.Xaml.Controls.StackPanel();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    stack.Children.Add(new Windows.UI.Xaml.Controls.TextBlock
                    {
                        Text = title,
                        Foreground = new SolidColorBrush(Colors.White),
                        FontSize = 18,
                        FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    });
                }
                stack.Children.Add(new Windows.UI.Xaml.Controls.TextBlock
                {
                    Text = text ?? "",
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap
                });

                border.Child = stack;
                ToastOverlayHost.Children.Add(border);

                _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(800, durationMs)) };
                _toastTimer.Tick += (_, __) =>
                {
                    _toastTimer.Stop();
                    ToastOverlayHost.Children.Clear();
                };
                _toastTimer.Start();
                return Task.CompletedTask;
            });
        }

        private void DataTransferManager_DataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            var data = _pendingShareData ?? new Dictionary<string, object>();
            var title = GetObjectString(data, "title") ?? "Share";
            var text = GetObjectString(data, "text");
            var url = GetObjectString(data, "url");

            args.Request.Data.Properties.Title = title;
            if (!string.IsNullOrEmpty(text))
            {
                args.Request.Data.SetText(text);
            }
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                args.Request.Data.SetWebLink(uri);
            }
        }

        public void ShowShareUi(Dictionary<string, object> data)
        {
            _pendingShareData = data;
            DataTransferManager.ShowShareUI();
        }

        private async void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                var installPath = Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
                var patcherPath = Path.Combine(installPath, "Assets", "WP", "patcher.js");

                if (File.Exists(patcherPath))
                {
                    var script = await File.ReadAllTextAsync(patcherPath);
                    await _coreWebView2.ExecuteScriptAsync(script);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("patcher.js file not found");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error running patcher.js: {ex.Message}");
            }
            finally
            {
                if (_pendingProtocolUri != null)
                {
                    PostProtocolActivatedEvent(_pendingProtocolUri);
                    _pendingProtocolUri = null;
                }
            }
        }

        private void App_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (!_backButtonHandlerEnabled)
            {
                e.Handled = false;
                return;
            }

            e.Handled = true;
            PostEvent("backButton", new { canGoBack = _coreWebView2 != null && _coreWebView2.CanGoBack });
        }

        private void CoreWindow_SizeChanged(Windows.UI.Core.CoreWindow sender, Windows.UI.Core.WindowSizeChangedEventArgs args)
        {
            WebView2.Width = Window.Current.Bounds.Width;
            WebView2.Height = Window.Current.Bounds.Height;
        }

        private void WebView2_WebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            var uri = new Uri(args.Request.Uri);

            if (customHeaders != null && customHeaders.Count > 0)
            {
                foreach (var header in customHeaders)
                {
                    args.Request.Headers.SetHeader(header.Key, header.Value);
                }
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            TryServeLocalResource(args, uri);
        }

        private void TryServeLocalResource(CoreWebView2WebResourceRequestedEventArgs args, Uri uri)
        {
            if (args.Response != null || string.IsNullOrEmpty(_webContentRoot))
            {
                return;
            }

            var relativePath = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            if (string.IsNullOrEmpty(relativePath))
            {
                relativePath = "index.html";
            }

            var sanitizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            string filePath;
            try
            {
                filePath = Path.GetFullPath(Path.Combine(_webContentRoot, sanitizedRelativePath));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Invalid resource request path '{relativePath}': {ex}");
                return;
            }

            var rootWithSeparator = _webContentRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? _webContentRoot
                : _webContentRoot + Path.DirectorySeparatorChar;

            if (!filePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
            {
                return;
            }

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var isDocument = args.ResourceContext == CoreWebView2WebResourceContext.Document;
            var isWasm = string.Equals(extension, ".wasm", StringComparison.OrdinalIgnoreCase);

            if (!isDocument && !isWasm)
            {
                return;
            }

            try
            {
                var stream = File.OpenRead(filePath).AsRandomAccessStream();
                var response = _coreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", null);

                if (isDocument)
                {
                    response.Headers.AppendHeader("Content-Type", "text/html; charset=utf-8");
                    response.Headers.AppendHeader("Cross-Origin-Opener-Policy", "same-origin");
                    response.Headers.AppendHeader("Cross-Origin-Embedder-Policy", "require-corp");
                    response.Headers.AppendHeader("Content-Security-Policy", "script-src 'self' 'unsafe-eval'; object-src 'self';");
                    response.Headers.AppendHeader("Access-Control-Allow-Origin", "*");
                    response.Headers.AppendHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    response.Headers.AppendHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");
                }
                else
                {
                    response.Headers.AppendHeader("Content-Type", "application/wasm");
                    response.Headers.AppendHeader("Cross-Origin-Resource-Policy", "same-origin");
                    response.Headers.AppendHeader("Access-Control-Allow-Origin", "*");
                    response.Headers.AppendHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    response.Headers.AppendHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");
                }

                args.Response = response;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to serve {filePath}: {ex}");
            }
        }

        private async void WebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            var message = args.TryGetWebMessageAsString();
            var request = JsonSerializer.Deserialize<Dictionary<string, object>>(message);

            if (request != null && request.ContainsKey("method") && request.ContainsKey("args"))
            {
                request.TryGetValue("id", out var idObj);
                var reqId = idObj?.ToString();
                var methodName = request["method"].ToString();
                var argsArray = ((JsonElement)request["args"]).EnumerateArray().Select(a => a.GetString()).ToArray();

                string response;
                string error = null;
                try
                {
                    response = await CallNativeMethodAsync(methodName, argsArray);
                }
                catch (Exception ex)
                {
                    response = null;
                    error = ex.Message;
                }

                var envelope = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(reqId)) envelope["id"] = reqId;
                if (error != null)
                {
                    envelope["error"] = error;
                }
                else
                {
                    envelope["result"] = response;
                }

                var responseMessage = JsonSerializer.Serialize(envelope);

                sender.PostWebMessageAsString(responseMessage);
            }
        }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is Uri uri)
            {
                HandleProtocolUri(uri);
            }
        }

        public void HandleProtocolUri(Uri uri)
        {
            _lastProtocolUri = uri;
            if (_coreWebView2 == null)
            {
                _pendingProtocolUri = uri;
                return;
            }
            PostProtocolActivatedEvent(uri);
        }

        private void PostProtocolActivatedEvent(Uri uri)
        {
            var data = new Dictionary<string, object>
            {
                ["uri"] = uri.AbsoluteUri,
                ["scheme"] = uri.Scheme,
                ["host"] = uri.Host,
                ["path"] = uri.AbsolutePath,
                ["query"] = ParseQuery(uri)
            };

            var envelope = new Dictionary<string, object>
            {
                ["event"] = "protocolActivated",
                ["data"] = data
            };

            var payload = JsonSerializer.Serialize(envelope);
            _coreWebView2.PostWebMessageAsString(payload);
        }

        public void PostEvent(string eventName, object data)
        {
            try
            {
                if (_coreWebView2 == null)
                {
                    return;
                }

                if (!Dispatcher.HasThreadAccess)
                {
                    var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => PostEvent(eventName, data));
                    return;
                }

                var envelope = new Dictionary<string, object>
                {
                    ["event"] = eventName,
                    ["data"] = data
                };
                _coreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(envelope));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PostEvent failed for {eventName}: {ex.Message}");
            }
        }

        public async Task DispatchConfiguredScriptRunnerAsync(string taskName)
        {
            var uwpNativeMethods = new UwpNativeMethods(_coreWebView2, this);
            await uwpNativeMethods.DispatchConfiguredScriptRunnerAsync(taskName);
        }

        public async Task RegisterConfiguredScriptRunnerAsync()
        {
            var uwpNativeMethods = new UwpNativeMethods(_coreWebView2, this);
            await uwpNativeMethods.RegisterSavedScriptRunnerAsync();
        }

        private static string GetObjectString(Dictionary<string, object> map, string key)
        {
            return map != null && map.TryGetValue(key, out var value) ? value?.ToString() : null;
        }

        private static string MapOrientation(DisplayOrientations orientation)
        {
            switch (orientation)
            {
                case DisplayOrientations.Portrait:
                    return "portrait-primary";
                case DisplayOrientations.PortraitFlipped:
                    return "portrait-secondary";
                case DisplayOrientations.LandscapeFlipped:
                    return "landscape-secondary";
                case DisplayOrientations.Landscape:
                default:
                    return "landscape-primary";
            }
        }

        private static object BuildNetworkStatus()
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            var connected = profile != null && profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            var type = "none";
            if (connected)
            {
                if (profile.IsWlanConnectionProfile) type = "wifi";
                else if (profile.IsWwanConnectionProfile) type = "cellular";
                else type = "unknown";
            }
            return new { connected = connected, connectionType = type };
        }

        private static Dictionary<string, string> ParseQuery(Uri uri)
        {
            var map = new Dictionary<string, string>();
            try
            {
                var q = uri.Query;
                if (!string.IsNullOrEmpty(q))
                {
                    var decoder = new Windows.Foundation.WwwFormUrlDecoder(q);
                    foreach (var pair in decoder)
                    {
                        map[pair.Name] = pair.Value;
                    }
                }
            }
            catch { }
            return map;
        }

        public class NotificationData
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string Message { get; set; }
            public string Image { get; set; }
            public string AppLogoOverride { get; set; }
            public List<NotificationButton> Buttons { get; set; }
            public string Tag { get; set; }
            public string Group { get; set; }
            public DateTime? ExpirationTime { get; set; }
        }

        public class NotificationButton
        {
            public string Content { get; set; }
            public string Action { get; set; }
            public string ArgName { get; set; }
            public string Arg { get; set; }
        }

        private Task<string> CallNativeMethodAsync(string methodName, params object[] args)
        {
            var uwpNativeMethods = new UwpNativeMethods(_coreWebView2, this);
            return uwpNativeMethods.CallNativeMethodAsync(methodName, args);
        }

        [System.Runtime.InteropServices.ComVisible(true)]
        public class UwpNativeMethods
        {
            private readonly CoreWebView2 _coreWebView2;
            private readonly MainPage _mainPage;
            private readonly Dictionary<string, MethodInfo> _methods;
            private static string downloadLocation = null;
            private bool isCursorHidden = false;
            public CoreCursor hiddenCursor;
            private const string SecureResourcePrefix = "uwp-js-secure";
            private const string ScriptRunnerSettingsPrefix = "UwpScriptRunner:";
            private const string ScriptRunnerTaskPrefix = "UwpScriptRunner.";
            private static readonly Dictionary<string, GeoWatchRegistration> GeoWatches = new Dictionary<string, GeoWatchRegistration>();
            private static Accelerometer _accelerometer;
            private static Inclinometer _inclinometer;
            private static Gyrometer _gyrometer;
            private static Compass _compass;
            private static bool _motionStarted;
            private static string _keyboardResizeMode = "native";

            private class GeoWatchRegistration
            {
                public Geolocator Locator { get; set; }
                public TypedEventHandler<Geolocator, PositionChangedEventArgs> Handler { get; set; }
            }


            public UwpNativeMethods(CoreWebView2 coreWebView2, MainPage mainPage)
            {
                _coreWebView2 = coreWebView2;
                _mainPage = mainPage;
                _methods = typeof(UwpNativeMethods).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                                   .ToDictionary(m => m.Name, m => m);
                hiddenCursor = new CoreCursor(CoreCursorType.Custom, 101);
            }

            public async Task<string> CallNativeMethodAsync(string methodName, params object[] args)
            {
                Console.WriteLine($"Call received: {methodName} with args: {string.Join(", ", args)}");

                if (_methods.TryGetValue(methodName, out var method))
                {
                    var result = method.Invoke(this, args);
                    if (result is Task<string> taskResult)
                    {
                        return await taskResult;
                    }
                    return result?.ToString();
                }
                throw new MissingMethodException($"Method {methodName} not found.");
            }

            private static string Ok(object data = null)
            {
                return JsonSerializer.Serialize(new { completed = true, data = data });
            }

            private static string Fail(string error)
            {
                return JsonSerializer.Serialize(new { completed = false, error = error });
            }

            private static Dictionary<string, JsonElement> ParseOptions(string optionsJson)
            {
                if (string.IsNullOrWhiteSpace(optionsJson) || optionsJson == "null")
                {
                    return new Dictionary<string, JsonElement>();
                }

                try
                {
                    return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(optionsJson) ?? new Dictionary<string, JsonElement>();
                }
                catch
                {
                    return new Dictionary<string, JsonElement>();
                }
            }

            private static string GetString(Dictionary<string, JsonElement> options, string key, string fallback = null)
            {
                if (!options.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
                {
                    return fallback;
                }
                if (value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
                return value.ToString();
            }

            private static bool GetBool(Dictionary<string, JsonElement> options, string key, bool fallback = false)
            {
                if (!options.TryGetValue(key, out var value))
                {
                    return fallback;
                }
                if (value.ValueKind == JsonValueKind.True) return true;
                if (value.ValueKind == JsonValueKind.False) return false;
                return bool.TryParse(value.ToString(), out var result) ? result : fallback;
            }

            private static int GetInt(Dictionary<string, JsonElement> options, string key, int fallback = 0)
            {
                if (!options.TryGetValue(key, out var value))
                {
                    return fallback;
                }
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                {
                    return number;
                }
                return int.TryParse(value.ToString(), out var result) ? result : fallback;
            }

            private static double GetDouble(Dictionary<string, JsonElement> options, string key, double fallback = 0)
            {
                if (!options.TryGetValue(key, out var value))
                {
                    return fallback;
                }
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                {
                    return number;
                }
                return double.TryParse(value.ToString(), out var result) ? result : fallback;
            }

            private static string GetRawJson(Dictionary<string, JsonElement> options, string key, string fallback = "{}")
            {
                if (!options.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
                {
                    return fallback;
                }
                return value.GetRawText();
            }

            private static bool HasApi(Dictionary<string, JsonElement> options, string api)
            {
                if (!options.TryGetValue("apis", out var value) || value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
                {
                    return true;
                }
                if (value.ValueKind == JsonValueKind.Array)
                {
                    return value.EnumerateArray().Any(item => string.Equals(item.GetString(), api, StringComparison.OrdinalIgnoreCase));
                }
                return string.Equals(value.ToString(), api, StringComparison.OrdinalIgnoreCase);
            }

            private static string MapGeoPermission(GeolocationAccessStatus status)
            {
                return status == GeolocationAccessStatus.Allowed ? "granted" : "denied";
            }

            private static string CurrentGeoPermission()
            {
                try
                {
                    var locator = new Geolocator();
                    switch (locator.LocationStatus)
                    {
                        case PositionStatus.Ready:
                        case PositionStatus.Initializing:
                        case PositionStatus.NoData:
                            return "granted";
                        case PositionStatus.Disabled:
                        case PositionStatus.NotAvailable:
                            return "denied";
                        default:
                            return "prompt";
                    }
                }
                catch
                {
                    return "prompt";
                }
            }

            private static Dictionary<string, object> ToObjectDictionary(Dictionary<string, JsonElement> options)
            {
                var result = new Dictionary<string, object>();
                foreach (var pair in options)
                {
                    switch (pair.Value.ValueKind)
                    {
                        case JsonValueKind.String:
                            result[pair.Key] = pair.Value.GetString();
                            break;
                        case JsonValueKind.Number:
                            result[pair.Key] = pair.Value.TryGetInt64(out var l) ? (object)l : pair.Value.GetDouble();
                            break;
                        case JsonValueKind.True:
                        case JsonValueKind.False:
                            result[pair.Key] = pair.Value.GetBoolean();
                            break;
                        default:
                            result[pair.Key] = pair.Value.ToString();
                            break;
                    }
                }
                return result;
            }

            private static string GetBaseFolderPath(string directory)
            {
                var normalized = (directory ?? "DATA").ToUpperInvariant();
                if (normalized == "CACHE")
                {
                    return ApplicationData.Current.LocalCacheFolder.Path;
                }
                if (normalized == "TEMPORARY" || normalized == "TEMP")
                {
                    return ApplicationData.Current.TemporaryFolder.Path;
                }
                return ApplicationData.Current.LocalFolder.Path;
            }

            private static string ResolveFsPath(Dictionary<string, JsonElement> options, string pathKey = "path")
            {
                var path = GetString(options, pathKey, "");
                if (string.IsNullOrWhiteSpace(path))
                {
                    return GetBaseFolderPath(GetString(options, "directory"));
                }
                path = path.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(path))
                {
                    return path;
                }
                return Path.Combine(GetBaseFolderPath(GetString(options, "directory")), path);
            }

            private static long ToUnixMs(DateTimeOffset date)
            {
                return date.ToUnixTimeMilliseconds();
            }

            private static string GuessMime(string path)
            {
                var ext = Path.GetExtension(path)?.ToLowerInvariant();
                switch (ext)
                {
                    case ".jpg":
                    case ".jpeg":
                        return "image/jpeg";
                    case ".png":
                        return "image/png";
                    case ".gif":
                        return "image/gif";
                    case ".mp4":
                        return "video/mp4";
                    case ".webm":
                        return "video/webm";
                    case ".pdf":
                        return "application/pdf";
                    case ".txt":
                        return "text/plain";
                    default:
                        return "application/octet-stream";
                }
            }

            private static string GetFormat(string path)
            {
                var ext = Path.GetExtension(path);
                return string.IsNullOrEmpty(ext) ? "" : ext.TrimStart('.').ToLowerInvariant();
            }

            private static async Task<StorageFile> CopyToLocalAsync(StorageFile source)
            {
                var local = ApplicationData.Current.LocalFolder;
                return await source.CopyAsync(local, source.Name, NameCollisionOption.GenerateUniqueName);
            }

            private static async Task<object> BuildPhotoResultAsync(StorageFile file, string resultType)
            {
                var format = GetFormat(file.Name);
                var normalized = (resultType ?? "uri").ToLowerInvariant();
                if (normalized == "base64")
                {
                    var buffer = await FileIO.ReadBufferAsync(file);
                    return new { base64String = Convert.ToBase64String(buffer.ToArray()), format = format, saved = true };
                }
                if (normalized == "dataurl" || normalized == "dataUrl")
                {
                    var buffer = await FileIO.ReadBufferAsync(file);
                    var mime = GuessMime(file.Name);
                    return new { dataUrl = $"data:{mime};base64,{Convert.ToBase64String(buffer.ToArray())}", format = format, saved = true };
                }
                return new
                {
                    path = file.Path,
                    webPath = $"http://localdata/{Uri.EscapeDataString(file.Name)}",
                    format = format,
                    saved = true
                };
            }

            private static async Task<object> BuildMediaResultAsync(StorageFile file)
            {
                var props = await file.GetBasicPropertiesAsync();
                return new
                {
                    path = file.Path,
                    webPath = $"http://localdata/{Uri.EscapeDataString(file.Name)}",
                    name = file.Name,
                    type = GuessMime(file.Name),
                    size = props.Size,
                    duration = 0
                };
            }

            private static object BuildPosition(Geoposition position)
            {
                var coord = position.Coordinate;
                var point = coord.Point.Position;
                return new
                {
                    timestamp = ToUnixMs(coord.Timestamp),
                    coords = new
                    {
                        latitude = point.Latitude,
                        longitude = point.Longitude,
                        accuracy = coord.Accuracy,
                        altitude = point.Altitude,
                        altitudeAccuracy = coord.AltitudeAccuracy,
                        speed = coord.Speed,
                        heading = coord.Heading
                    }
                };
            }

            public Task<string> exampleMethod(string param1, string param2)
            {
                return Task.FromResult($"Hello from UWP (XBOX) native code with params: {param1}, {param2}");
            }

            public async Task<string> pickFolder()
            {
                try
                {
                    FolderPicker folderPicker = new FolderPicker();
                    folderPicker.SuggestedStartLocation = PickerLocationId.Desktop;
                    folderPicker.FileTypeFilter.Add("*");
                    StorageFolder folder = await folderPicker.PickSingleFolderAsync();

                    if (folder == null)
                    {
                        return JsonSerializer.Serialize(new { completed = false, error = "No folder selected" });
                    }

                    _coreWebView2.SetVirtualHostNameToFolderMapping("selectedcontent", folder.Path, CoreWebView2HostResourceAccessKind.Allow);

                    return JsonSerializer.Serialize(new { completed = true, data = folder.Path });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> read(string filePathOrName, string codec = null)
            {
                try
                {
                    StorageFile file;

                    if (filePathOrName != null && (filePathOrName.Contains("\\") || filePathOrName.Contains("/")))
                    {
                        file = await StorageFile.GetFileFromPathAsync(filePathOrName);
                    }
                    else
                    {
                        var folder = ApplicationData.Current.LocalFolder;
                        file = await folder.GetFileAsync(filePathOrName);
                    }

                    if (codec == "base64")
                    {
                        var buffer = await FileIO.ReadBufferAsync(file);
                        var bytes = buffer.ToArray();
                        var base64String = Convert.ToBase64String(bytes);
                        return JsonSerializer.Serialize(new { completed = true, data = base64String, encoding = "base64" });
                    }
                    else
                    {
                        var content = await FileIO.ReadTextAsync(file);
                        return JsonSerializer.Serialize(new { completed = true, data = content, encoding = "text" });
                    }
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> write(string filePathOrName, string data)
            {
                try
                {
                    StorageFile file;

                    if (filePathOrName.Contains("\\") || filePathOrName.Contains("/"))
                    {
                        var folderPath = System.IO.Path.GetDirectoryName(filePathOrName);
                        var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
                        var fileName = System.IO.Path.GetFileName(filePathOrName);
                        file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                    }
                    else
                    {
                        var folder = ApplicationData.Current.LocalFolder;
                        file = await folder.CreateFileAsync(filePathOrName, CreationCollisionOption.ReplaceExisting);
                    }

                    await FileIO.WriteTextAsync(file, data);
                    return JsonSerializer.Serialize(new { completed = true, data = file.Path });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> readDir(string folderPath)
            {
                try
                {
                    var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
                    var files = await folder.GetFilesAsync();
                    var fileNames = files.Select(f => f.Name).ToArray();
                    return JsonSerializer.Serialize(new { completed = true, data = fileNames, path = folderPath });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> readLocalDir()
            {
                try
                {
                    var localFolder = ApplicationData.Current.LocalFolder;
                    var files = await localFolder.GetFilesAsync();
                    var folders = await localFolder.GetFoldersAsync();
                    var allItems = files.Cast<IStorageItem>()
                                        .Concat(folders.Cast<IStorageItem>())
                                        .Select(item => new { name = item.Name, path = item.Path, isFolder = item is StorageFolder })
                                        .ToArray();

                    return JsonSerializer.Serialize(new { completed = true, data = allItems, path = localFolder.Path });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }



            public async Task<string> showAlert(string title, string text)
            {
                try
                {
                    var messageDialog = new MessageDialog(text, title);
                    await messageDialog.ShowAsync();
                    return JsonSerializer.Serialize(new { completed = true, data = "shown" });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> showDialog(string title, string body, string yesButtonText = "Yes", string noButtonText = "No")
            {
                try
                {
                    var messageDialog = new MessageDialog(body, title);
                    messageDialog.Commands.Add(new UICommand(yesButtonText, null, 0));
                    messageDialog.Commands.Add(new UICommand(noButtonText, null, 1));
                    var result = await messageDialog.ShowAsync();

                    return JsonSerializer.Serialize(new { completed = true, data = result.Id.ToString(), buttonPressed = result.Label });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> showProgressDialog(string title, string cancelButtonText, Func<IProgress<int>, Task> action)
            {
                var progressDialog = new ContentDialog
                {
                    Title = title,
                    PrimaryButtonText = cancelButtonText,
                    IsPrimaryButtonEnabled = true,
                    CloseButtonText = "Close",
                    IsSecondaryButtonEnabled = false
                };

                var progressBar = new Windows.UI.Xaml.Controls.ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Width = 300,
                    IsIndeterminate = false
                };

                progressDialog.Content = progressBar;

                var progress = new Progress<int>(value =>
                {
                    progressBar.Value = value;
                    if (value >= 100)
                    {
                        progressDialog.Hide();
                    }
                });

                progressDialog.PrimaryButtonClick += (_, __) =>
                {
                    _mainPage._cancelDownload = true;
                };

                var task = action(progress);
                await progressDialog.ShowAsync();
                await task;

                bool cancelled = _mainPage._cancelDownload;
                return JsonSerializer.Serialize(new { 
                    completed = true, 
                    data = new { 
                        cancelled = cancelled,
                        result = cancelled ? "cancelled" : "completed"
                    } 
                });
            }


            public async Task<string> downloadFile(string fileUrlOrData, string encoding = "url", string name = null)
            {
                try
                {
                    string fileName;
                    long fileSize = 0;

                    if (encoding == "base64")
                    {
                        fileName = name ?? "downloadedFile";
                    }
                    else
                    {
                        Uri uri = new Uri(fileUrlOrData);
                        fileName = name ?? System.IO.Path.GetFileName(uri.LocalPath);

                        using (var response = await _mainPage._httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri)))
                        {
                            response.EnsureSuccessStatusCode();
                            fileSize = response.Content.Headers.ContentLength ?? 0;
                        }
                    }

                    string fileSizeString;
                    if (fileSize > 1024 * 1024 * 1024)
                    {
                        fileSizeString = $"{fileSize / (1024.0 * 1024 * 1024):0.##} GB";
                    }
                    else if (fileSize > 1024 * 1024)
                    {
                        fileSizeString = $"{fileSize / (1024.0 * 1024):0.##} MB";
                    }
                    else
                    {
                        fileSizeString = $"{fileSize / 1024.0:0.##} KB";
                    }

                    var dialogResult = await showDialog("UWP download", $"Are you sure you want to download {fileName}? ({fileSizeString})", "Yes", "No");
                    var dialogResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(dialogResult);

                    if (dialogResponse["data"].ToString() != "0")
                    {
                        return JsonSerializer.Serialize(new { completed = false, error = "Download cancelled by user" });
                    }

                    StorageFolder folder;
                    if (string.IsNullOrEmpty(downloadLocation))
                    {
                        folder = ApplicationData.Current.LocalFolder;
                    }
                    else
                    {
                        folder = await StorageFolder.GetFolderFromPathAsync(downloadLocation);
                    }

                    StorageFile file;
                    if (encoding == "base64")
                    {
                        byte[] data = Convert.FromBase64String(fileUrlOrData);
                        file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                        await FileIO.WriteBytesAsync(file, data);
                    }
                    else
                    {
                        Uri uri = new Uri(fileUrlOrData);
                        file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

                        if (fileSize > 25 * 1024 * 1024)
                        {
                            var result = await showProgressDialog("Downloading...", "Cancel", async (progress) =>
                            {
                                using (var response = await _mainPage._httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
                                {
                                    response.EnsureSuccessStatusCode();
                                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                                    var canReportProgress = totalBytes != -1;

                                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                                    using (var fileStream = await file.OpenStreamForWriteAsync())
                                    {
                                        var totalRead = 0L;
                                        var buffer = new byte[8192];
                                        var isMoreToRead = true;

                                        while (isMoreToRead)
                                        {
                                            if (_mainPage._cancelDownload)
                                            {
                                                break;
                                            }

                                            var read = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                                            if (read == 0)
                                            {
                                                isMoreToRead = false;
                                            }
                                            else
                                            {
                                                await fileStream.WriteAsync(buffer, 0, read);
                                                totalRead += read;

                                                if (canReportProgress)
                                                {
                                                    var progressValue = (int)((totalRead * 1d) / (totalBytes * 1d) * 100);
                                                    progress.Report(progressValue);
                                                }
                                            }
                                        }
                                    }
                                }
                            });

                            if (_mainPage._cancelDownload)
                            {
                                await file.DeleteAsync();
                                return result;
                            }
                        }
                        else
                        {
                            using (var response = await _mainPage._httpClient.GetAsync(uri))
                            {
                                response.EnsureSuccessStatusCode();
                                var data = await response.Content.ReadAsByteArrayAsync();
                                await FileIO.WriteBytesAsync(file, data);
                            }
                        }
                    }

                    return JsonSerializer.Serialize(new { completed = true, data = new { path = file.Path, fileName = fileName, size = fileSize } });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> selectFile()
            {
                try
                {
                    FileOpenPicker filePicker = new FileOpenPicker();
                    filePicker.SuggestedStartLocation = PickerLocationId.Desktop;
                    filePicker.FileTypeFilter.Add("*");
                    StorageFile file = await filePicker.PickSingleFileAsync();

                    if (file == null)
                    {
                        return JsonSerializer.Serialize(new { completed = false, error = "No file selected" });
                    }

                    _coreWebView2.SetVirtualHostNameToFolderMapping("selectedfiles", System.IO.Path.GetDirectoryName(file.Path), CoreWebView2HostResourceAccessKind.Allow);

                    return JsonSerializer.Serialize(new { completed = true, data = new { name = file.Name, path = file.Path } });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> setDownloadLocation(string path)
            {
                try
                {
                    if (string.IsNullOrEmpty(path))
                    {
                        downloadLocation = null;
                        return JsonSerializer.Serialize(new { completed = true, data = "default", message = "Download location reset to default" });
                    }

                    var folder = await StorageFolder.GetFolderFromPathAsync(path);
                    downloadLocation = folder.Path;
                    return JsonSerializer.Serialize(new { completed = true, data = downloadLocation });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> createFolder(string folderPathOrName)
            {
                try
                {
                    StorageFolder folder;

                    if (folderPathOrName.Contains("\\") || folderPathOrName.Contains("/"))
                    {
                        var parentFolderPath = System.IO.Path.GetDirectoryName(folderPathOrName);
                        var parentFolder = await StorageFolder.GetFolderFromPathAsync(parentFolderPath);
                        var folderName = System.IO.Path.GetFileName(folderPathOrName);
                        folder = await parentFolder.CreateFolderAsync(folderName, CreationCollisionOption.OpenIfExists);
                    }
                    else
                    {
                        var localFolder = ApplicationData.Current.LocalFolder;
                        folder = await localFolder.CreateFolderAsync(folderPathOrName, CreationCollisionOption.OpenIfExists);
                    }

                    return JsonSerializer.Serialize(new { completed = true, data = folder.Path });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> redirect(string url)
            {
                try
                {
                    if (string.IsNullOrEmpty(url))
                    {
                        return JsonSerializer.Serialize(new { completed = false, error = "Invalid URL" });
                    }

                    _mainPage.WebView2.Source = new Uri(url);
                    return JsonSerializer.Serialize(new { completed = true, data = url });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> HideCursor()
            {
                try
                {
                    Window.Current.CoreWindow.PointerCursor = null;

                    await _coreWebView2.ExecuteScriptAsync(@"
                        document.querySelector('body').style.cursor = 'none';
                        document.body.requestPointerLock = document.body.requestPointerLock ||
                                                   document.body.mozRequestPointerLock ||
                                                   document.body.webkitRequestPointerLock;
                        document.body.requestPointerLock();
                        navigator.gamepadInputEmulation = 'gamepad';
                        ");

                    await _mainPage.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        _mainPage.WebView2.Focus(FocusState.Programmatic);
                    });

                    isCursorHidden = true;
                    return JsonSerializer.Serialize(new { completed = true, data = "hidden" });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> ShowCursor()
            {
                try
                {
                    Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.Arrow, 0);
                    await _coreWebView2.ExecuteScriptAsync(@"
                          document.querySelector('body').style.cursor = 'default';
                          if (document.pointerLockElement) {
                            document.exitPointerLock = document.exitPointerLock ||
                                               document.mozExitPointerLock ||
                                               document.webkitExitPointerLock;
                            document.exitPointerLock();
                        }
                        navigator.gamepadInputEmulation = 'mouse';
                    ");

                    //Application.Current.RequiresPointerMode = ApplicationRequiresPointerMode.Auto;

                    await _mainPage.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        FocusManager.TryMoveFocus(FocusNavigationDirection.Next);
                    });

                    isCursorHidden = false;
                    return JsonSerializer.Serialize(new { completed = true, data = "visible" });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> zipFolder(string folderPath, string outputPath = null)
            {
                try
                {
                    if (string.IsNullOrEmpty(outputPath))
                    {
                        outputPath = Path.Combine(Path.GetDirectoryName(folderPath), Path.GetFileName(folderPath) + ".zip");
                    }

                    if (folderPath.Contains("\\") || folderPath.Contains("/"))
                    {
                        if (Directory.Exists(folderPath))
                        {
                            ZipFile.CreateFromDirectory(folderPath, outputPath);
                            return JsonSerializer.Serialize(new { completed = true, data = new { sourcePath = folderPath, zipPath = outputPath } });
                        }
                        else
                        {
                            return JsonSerializer.Serialize(new { completed = false, error = "Folder not found" });
                        }
                    }
                    else
                    {
                        var folder = await ApplicationData.Current.LocalFolder.GetFolderAsync(folderPath);
                        var folderFullPath = folder.Path;

                        if (Directory.Exists(folderFullPath))
                        {
                            outputPath = outputPath ?? Path.Combine(folderFullPath, Path.GetFileName(folderFullPath) + ".zip");
                            ZipFile.CreateFromDirectory(folderFullPath, outputPath);
                            return JsonSerializer.Serialize(new { completed = true, data = new { sourcePath = folderFullPath, zipPath = outputPath } });
                        }
                        else
                        {
                            return JsonSerializer.Serialize(new { completed = false, error = "Folder not found" });
                        }
                    }
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> unzip(string zipPath, string outputPath = null)
            {
                try
                {
                    if (string.IsNullOrEmpty(outputPath))
                    {
                        outputPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, Path.GetFileNameWithoutExtension(zipPath));
                    }
                    else if (!Path.IsPathRooted(outputPath))
                    {
                        outputPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, outputPath);
                    }

                    async Task<object> UnzipFile(string filePath)
                    {
                        try
                        {
                            var extractedFiles = new List<string>();
                            using (ZipArchive archive = ZipFile.OpenRead(filePath))
                            {
                                foreach (ZipArchiveEntry entry in archive.Entries)
                                {
                                    string destinationPath = Path.Combine(outputPath, entry.FullName);

                                    string directoryPath = Path.GetDirectoryName(destinationPath);
                                    if (!Directory.Exists(directoryPath))
                                    {
                                        Directory.CreateDirectory(directoryPath);
                                    }

                                    if (entry.Name == "")
                                    {
                                        if (Directory.Exists(destinationPath))
                                        {
                                            Directory.Delete(destinationPath, true);
                                        }
                                    }
                                    else
                                    {
                                        if (File.Exists(destinationPath))
                                        {
                                            File.Delete(destinationPath);
                                        }

                                        entry.ExtractToFile(destinationPath, true);
                                        extractedFiles.Add(destinationPath);
                                    }
                                }
                            }
                            return new { completed = true, data = new { outputPath = outputPath, extractedFiles = extractedFiles.ToArray() } };
                        }
                        catch (Exception ex)
                        {
                            return new { completed = false, error = ex.Message };
                        }
                    }

                    string resolvedZipPath = zipPath;
                    if (!Path.IsPathRooted(zipPath))
                    {
                        resolvedZipPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, zipPath);
                    }

                    if (File.Exists(resolvedZipPath))
                    {
                        var result = await UnzipFile(resolvedZipPath);
                        return JsonSerializer.Serialize(result);
                    }
                    else
                    {
                        return JsonSerializer.Serialize(new { completed = false, error = $"Zip file not found at {resolvedZipPath}" });
                    }
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }
            public async Task<string> deleteFile(string filePath)
            {
                try
                {
                    StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
                    await file.DeleteAsync();
                    return JsonSerializer.Serialize(new { completed = true, data = filePath });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> deleteFolder(string folderPath)
            {
                try
                {
                    StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
                    await folder.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    return JsonSerializer.Serialize(new { completed = true, data = folderPath });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> GetMachineStatus()
            {
                try
                {
                    string osVersion = $"{Environment.OSVersion.VersionString}";

                    int processor_count = Environment.ProcessorCount;
                    ulong totalMemory = Windows.System.MemoryManager.AppMemoryUsageLimit;
                    ulong usedMemory = Windows.System.MemoryManager.AppMemoryUsage;

                    string cpuSpeed = "N/A";

                    var drives = DriveInfo.GetDrives()
                        .Where(d => d.IsReady)
                        .Select(d => new { 
                            name = d.Name, 
                            type = d.DriveType.ToString(), 
                            freeSpaceMB = d.AvailableFreeSpace / (1024 * 1024), 
                            totalSpaceMB = d.TotalSize / (1024 * 1024) 
                        })
                        .ToArray();

                    var systemInfo = new {
                        os = osVersion,
                        ramUsedMB = usedMemory / (1024 * 1024),
                        ramTotalMB = totalMemory / (1024 * 1024),
                        cpuSpeed = cpuSpeed,
                        cpuCores = processor_count,
                        drives = drives
                    };

                    return JsonSerializer.Serialize(new { completed = true, data = systemInfo });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> setheaders(string headersJson)
            {
                try
                {
                    var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                    _mainPage.SetCustomHeaders(headers);
                    return JsonSerializer.Serialize(new { completed = true, data = headers });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> clearheaders()
            {
                try
                {
                    _mainPage.ClearCustomHeaders();
                    return JsonSerializer.Serialize(new { completed = true, data = "cleared" });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> ShowNotification(string notificationJson)
            {
                try
                {
                    var notification = JsonSerializer.Deserialize<NotificationData>(notificationJson);

                    if (notification == null)
                        return JsonSerializer.Serialize(new { completed = false, error = "Invalid notification data" });

                    var toastBuilder = new ToastContentBuilder();

                    if (!string.IsNullOrEmpty(notification.Id))
                    {
                        toastBuilder.AddArgument("id", notification.Id);
                    }

                    if (!string.IsNullOrEmpty(notification.Title))
                    {
                        toastBuilder.AddText(notification.Title);
                    }

                    if (!string.IsNullOrEmpty(notification.Message))
                    {
                        toastBuilder.AddText(notification.Message);
                    }

                    if (!string.IsNullOrEmpty(notification.Image))
                    {
                        Uri imageUri;
                        if (Uri.TryCreate(notification.Image, UriKind.Absolute, out imageUri) &&
                            (imageUri.Scheme == Uri.UriSchemeHttp || imageUri.Scheme == Uri.UriSchemeHttps))
                        {
                            toastBuilder.AddInlineImage(imageUri);
                        }
                        else
                        {
                            string relativePath = notification.Image.TrimStart('/').Replace('\\', '/');
                            string assetPath = $"ms-appx:///Assets/WP/{relativePath}";
                            toastBuilder.AddInlineImage(new Uri(assetPath));
                        }
                    }

                    if (!string.IsNullOrEmpty(notification.AppLogoOverride))
                    {
                        Uri logoUri;
                        if (Uri.TryCreate(notification.AppLogoOverride, UriKind.Absolute, out logoUri) &&
                            (logoUri.Scheme == Uri.UriSchemeHttp || logoUri.Scheme == Uri.UriSchemeHttps ||
                             logoUri.Scheme == "ms-appdata"))
                        {
                            toastBuilder.AddAppLogoOverride(logoUri, ToastGenericAppLogoCrop.Circle);
                        }
                        else
                        {
                            string relativeLogoPath = notification.AppLogoOverride.TrimStart('/').Replace('\\', '/');
                            string logoAssetPath = $"ms-appx:///Assets/WP/{relativeLogoPath}";
                            toastBuilder.AddAppLogoOverride(new Uri(logoAssetPath), ToastGenericAppLogoCrop.Circle);
                        }
                    }

                    if (notification.Buttons != null && notification.Buttons.Count > 0)
                    {
                        foreach (var button in notification.Buttons)
                        {
                            var toastButton = new ToastButton()
                                .SetContent(button.Content)
                                .AddArgument("action", button.Action);

                            if (!string.IsNullOrEmpty(button.Arg))
                            {
                                toastButton.AddArgument(button.ArgName, button.Arg);
                            }

                            toastBuilder.AddButton(toastButton);
                        }
                    }

                    toastBuilder.Show(toast => {
                        if (!string.IsNullOrEmpty(notification.Tag))
                        {
                            toast.Tag = notification.Tag;
                        }
                        if (!string.IsNullOrEmpty(notification.Group))
                        {
                            toast.Group = notification.Group;
                        }
                        if (notification.ExpirationTime.HasValue)
                        {
                            toast.ExpirationTime = notification.ExpirationTime.Value;
                        }
                    });

                    return JsonSerializer.Serialize(new { completed = true, data = new { id = notification.Id, shown = true } });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> ClearNotifications()
            {
                try
                {
                    ToastNotificationManagerCompat.History.Clear();
                    return JsonSerializer.Serialize(new { completed = true, data = "cleared" });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> showActionSheet(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var title = GetString(options, "title", "Choose");
                    var dialog = new MessageDialog(GetString(options, "message", ""), title);
                    var buttons = options.TryGetValue("options", out var opts) && opts.ValueKind == JsonValueKind.Array
                        ? opts.EnumerateArray().ToArray()
                        : Array.Empty<JsonElement>();

                    for (var i = 0; i < buttons.Length; i++)
                    {
                        var item = buttons[i];
                        var text = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("title", out var titleProp)
                            ? titleProp.GetString()
                            : item.ToString();
                        dialog.Commands.Add(new UICommand(text ?? $"Option {i + 1}", null, i));
                    }

                    if (!dialog.Commands.Any())
                    {
                        dialog.Commands.Add(new UICommand("OK", null, 0));
                    }

                    var result = await dialog.ShowAsync();
                    return Ok(new { index = Convert.ToInt32(result.Id) });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public Task<string> getAppInfo()
            {
                try
                {
                    var package = Package.Current;
                    var version = package.Id.Version;
                    var versionString = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
                    return Task.FromResult(Ok(new
                    {
                        name = package.DisplayName,
                        id = package.Id.Name,
                        version = versionString,
                        build = version.Revision.ToString()
                    }));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> getAppState()
            {
                return Task.FromResult(Ok(new { isActive = Window.Current.Visible }));
            }

            public Task<string> getLaunchUrl()
            {
                return Task.FromResult(Ok(_mainPage._lastProtocolUri == null ? null : new { url = _mainPage._lastProtocolUri.AbsoluteUri }));
            }

            public Task<string> minimizeApp()
            {
                return Task.FromResult(Fail("UWP/Xbox does not expose a reliable minimize API for this host."));
            }

            public Task<string> getAppLanguage()
            {
                var language = GlobalizationPreferences.Languages.FirstOrDefault() ?? "en-US";
                return Task.FromResult(Ok(new { code = language }));
            }

            public Task<string> toggleBackButtonHandler(string optionsJson)
            {
                var options = ParseOptions(optionsJson);
                var disabled = GetBool(options, "disabled", false);
                _mainPage._backButtonHandlerEnabled = !disabled;
                return Task.FromResult(Ok(new { enabled = _mainPage._backButtonHandlerEnabled }));
            }

            public async Task<string> openBrowser(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var url = GetString(options, "url");
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        return Fail("url is required");
                    }
                    await _mainPage.ShowBrowserOverlayAsync(url);
                    return Ok(new { url = url });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> closeBrowser()
            {
                try
                {
                    await _mainPage.CloseBrowserOverlayAsync();
                    return Ok(true);
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public Task<string> writeClipboard(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var text = GetString(options, "string") ?? GetString(options, "text") ?? GetString(options, "url") ?? "";
                    var package = new DataPackage();
                    package.SetText(text);
                    Clipboard.SetContent(package);
                    return Task.FromResult(Ok(true));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public async Task<string> readClipboard()
            {
                try
                {
                    var content = Clipboard.GetContent();
                    if (content.Contains(StandardDataFormats.Text))
                    {
                        var text = await content.GetTextAsync();
                        return Ok(new { type = "text/plain", value = text });
                    }
                    return Ok(new { type = "text/plain", value = "" });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public Task<string> getDeviceId()
            {
                var settings = ApplicationData.Current.LocalSettings;
                var id = settings.Values["uwp_js_device_id"] as string;
                if (string.IsNullOrEmpty(id))
                {
                    id = Guid.NewGuid().ToString();
                    settings.Values["uwp_js_device_id"] = id;
                }
                return Task.FromResult(Ok(new { identifier = id }));
            }

            public Task<string> getDeviceInfo()
            {
                try
                {
                    var info = new Windows.Security.ExchangeActiveSyncProvisioning.EasClientDeviceInformation();
                    var deviceFamily = AnalyticsInfo.VersionInfo.DeviceFamily;
                    return Task.FromResult(Ok(new
                    {
                        name = info.FriendlyName,
                        model = info.SystemProductName,
                        platform = deviceFamily.Equals("Windows.Xbox", StringComparison.OrdinalIgnoreCase) ? "xbox" : "windows",
                        operatingSystem = "windows",
                        osVersion = Environment.OSVersion.Version.ToString(),
                        manufacturer = info.SystemManufacturer,
                        isVirtual = false,
                        webViewVersion = "WebView2",
                        memUsed = Windows.System.MemoryManager.AppMemoryUsage
                    }));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> getBatteryInfo()
            {
                try
                {
                    var level = PowerManager.RemainingChargePercent;
                    var charging = PowerManager.PowerSupplyStatus == PowerSupplyStatus.Adequate ||
                                   PowerManager.PowerSupplyStatus == PowerSupplyStatus.NotPresent;
                    return Task.FromResult(Ok(new { batteryLevel = level / 100.0, isCharging = charging }));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> getLanguageCode()
            {
                var language = GlobalizationPreferences.Languages.FirstOrDefault() ?? "en-US";
                return Task.FromResult(Ok(new { value = language.Split('-')[0] }));
            }

            public Task<string> getLanguageTag()
            {
                var language = GlobalizationPreferences.Languages.FirstOrDefault() ?? "en-US";
                return Task.FromResult(Ok(new { value = language }));
            }

            public async Task<string> promptDialog(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var textBox = new Windows.UI.Xaml.Controls.TextBox
                    {
                        Text = GetString(options, "inputText", ""),
                        PlaceholderText = GetString(options, "inputPlaceholder", "")
                    };
                    var dialog = new Windows.UI.Xaml.Controls.ContentDialog
                    {
                        Title = GetString(options, "title", "Prompt"),
                        Content = textBox,
                        PrimaryButtonText = GetString(options, "okButtonTitle", "OK"),
                        SecondaryButtonText = GetString(options, "cancelButtonTitle", "Cancel")
                    };
                    var result = await dialog.ShowAsync();
                    return Ok(new { value = textBox.Text, cancelled = result != Windows.UI.Xaml.Controls.ContentDialogResult.Primary });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> showToast(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    await _mainPage.ShowInAppToastAsync(
                        GetString(options, "title", ""),
                        GetString(options, "text", GetString(options, "message", "")),
                        GetInt(options, "duration", 2500)
                    );
                    return Ok(true);
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public Task<string> getNetworkStatus()
            {
                try
                {
                    return Task.FromResult(Ok(BuildNetworkStatus()));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> getScreenOrientation()
            {
                try
                {
                    var display = DisplayInformation.GetForCurrentView();
                    return Task.FromResult(Ok(new { type = MapOrientation(display.CurrentOrientation) }));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> lockScreenOrientation(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var requested = GetString(options, "orientation", GetString(options, "type", "landscape-primary"));
                    DisplayInformation.AutoRotationPreferences =
                        requested != null && requested.StartsWith("portrait", StringComparison.OrdinalIgnoreCase)
                            ? DisplayOrientations.Portrait
                            : DisplayOrientations.Landscape;
                    return Task.FromResult(Ok(true));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> unlockScreenOrientation()
            {
                DisplayInformation.AutoRotationPreferences = DisplayOrientations.None;
                return Task.FromResult(Ok(true));
            }

            public Task<string> isScreenReaderEnabled()
            {
                try
                {
                    var enabled = Windows.UI.Xaml.Automation.Peers.AutomationPeer.ListenerExists(
                        Windows.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
                    return Task.FromResult(Ok(new { value = enabled }));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public async Task<string> speak(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var text = GetString(options, "value", GetString(options, "text", ""));
                    using (var synth = new SpeechSynthesizer())
                    {
                        var stream = await synth.SynthesizeTextToStreamAsync(text);
                        await _mainPage.RunOnUiThreadAsync(() =>
                        {
                            _mainPage.SpeechPlayer.SetSource(stream, stream.ContentType);
                            _mainPage.SpeechPlayer.Play();
                            return Task.CompletedTask;
                        });
                    }
                    return Ok(true);
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public Task<string> canShare(string optionsJson)
            {
                return Task.FromResult(Ok(new { value = _mainPage._dataTransferManager != null }));
            }

            public Task<string> share(string optionsJson)
            {
                try
                {
                    if (_mainPage._dataTransferManager == null)
                    {
                        return Task.FromResult(Fail("UWP share UI is not available for this view."));
                    }
                    _mainPage.ShowShareUi(ToObjectDictionary(ParseOptions(optionsJson)));
                    return Task.FromResult(Ok(new { activityType = "share" }));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> showSplashScreen(string optionsJson)
            {
                return Task.FromResult(Ok(true));
            }

            public Task<string> hideSplashScreen(string optionsJson)
            {
                return Task.FromResult(Ok(true));
            }

            public Task<string> setStatusBarStyle(string optionsJson) => Task.FromResult(Fail("StatusBar is not available on desktop UWP/Xbox."));
            public Task<string> setStatusBarBackgroundColor(string optionsJson) => Task.FromResult(Fail("StatusBar is not available on desktop UWP/Xbox."));
            public Task<string> showStatusBar(string optionsJson) => Task.FromResult(Fail("StatusBar is not available on desktop UWP/Xbox."));
            public Task<string> hideStatusBar(string optionsJson) => Task.FromResult(Fail("StatusBar is not available on desktop UWP/Xbox."));
            public Task<string> getStatusBarInfo() => Task.FromResult(Fail("StatusBar is not available on desktop UWP/Xbox."));
            public Task<string> setStatusBarOverlaysWebView(string optionsJson) => Task.FromResult(Fail("StatusBar is not available on desktop UWP/Xbox."));

            public async Task<string> scheduleLocalNotifications(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var scheduled = new List<object>();
                    if (options.TryGetValue("notifications", out var notifications) && notifications.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var notification in notifications.EnumerateArray())
                        {
                            var id = notification.TryGetProperty("id", out var idProp) ? idProp.ToString() : DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
                            var title = notification.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : "";
                            var body = notification.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : "";
                            await ShowNotification(JsonSerializer.Serialize(new
                            {
                                Id = id,
                                Title = title,
                                Message = body
                            }));
                            scheduled.Add(new { id = id });
                        }
                    }
                    return Ok(new { notifications = scheduled });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public Task<string> getPendingLocalNotifications()
            {
                return Task.FromResult(Ok(new { notifications = Array.Empty<object>() }));
            }

            public Task<string> registerLocalNotificationActionTypes(string optionsJson)
            {
                return Task.FromResult(Ok(true));
            }

            public Task<string> cancelLocalNotifications(string optionsJson)
            {
                return Task.FromResult(Ok(true));
            }

            public Task<string> areLocalNotificationsEnabled()
            {
                return Task.FromResult(Ok(new { value = true }));
            }

            public Task<string> getDeliveredNotifications()
            {
                return Task.FromResult(Ok(new { notifications = Array.Empty<object>() }));
            }

            public Task<string> removeDeliveredNotifications(string optionsJson)
            {
                return Task.FromResult(Ok(true));
            }

            public Task<string> removeAllDeliveredNotifications()
            {
                ToastNotificationManagerCompat.History.Clear();
                return Task.FromResult(Ok(true));
            }

            public Task<string> createNotificationChannel(string channelJson)
            {
                return Task.FromResult(Ok(true));
            }

            public Task<string> deleteNotificationChannel(string optionsJson)
            {
                return Task.FromResult(Ok(true));
            }

            public Task<string> listNotificationChannels()
            {
                return Task.FromResult(Ok(new { channels = Array.Empty<object>() }));
            }

            public Task<string> checkNotificationPermissions()
            {
                return Task.FromResult(Ok(new { display = "granted" }));
            }

            public Task<string> requestNotificationPermissions()
            {
                return Task.FromResult(Ok(new { display = "granted" }));
            }

            public Task<string> checkExactNotificationSetting()
            {
                return Task.FromResult(Ok(new { exact_alarm = "granted" }));
            }

            public Task<string> changeExactNotificationSetting()
            {
                return Task.FromResult(Ok(new { exact_alarm = "granted" }));
            }

            public async Task<string> takePhoto(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var ui = new CameraCaptureUI();
                    ui.PhotoSettings.Format = CameraCaptureUIPhotoFormat.Jpeg;
                    var captured = await ui.CaptureFileAsync(CameraCaptureUIMode.Photo);
                    if (captured == null)
                    {
                        return Fail("No photo captured.");
                    }
                    var local = await CopyToLocalAsync(captured);
                    return Ok(await BuildPhotoResultAsync(local, GetString(options, "resultType", "uri")));
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> recordVideo(string optionsJson)
            {
                try
                {
                    var ui = new CameraCaptureUI();
                    ui.VideoSettings.Format = CameraCaptureUIVideoFormat.Mp4;
                    var captured = await ui.CaptureFileAsync(CameraCaptureUIMode.Video);
                    if (captured == null)
                    {
                        return Fail("No video captured.");
                    }
                    var local = await CopyToLocalAsync(captured);
                    return Ok(await BuildMediaResultAsync(local));
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> playVideo(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var path = GetString(options, "path", GetString(options, "url"));
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return Fail("path or url is required");
                    }
                    if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        await Launcher.LaunchUriAsync(uri);
                    }
                    else
                    {
                        var file = await StorageFile.GetFileFromPathAsync(path);
                        await Launcher.LaunchFileAsync(file);
                    }
                    return Ok(true);
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> chooseMediaFromGallery(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var picker = new FileOpenPicker
                    {
                        SuggestedStartLocation = PickerLocationId.PicturesLibrary
                    };
                    var mediaType = (GetString(options, "mediaType", "all") ?? "all").ToLowerInvariant();
                    if (mediaType == "photos" || mediaType == "all")
                    {
                        picker.FileTypeFilter.Add(".jpg");
                        picker.FileTypeFilter.Add(".jpeg");
                        picker.FileTypeFilter.Add(".png");
                        picker.FileTypeFilter.Add(".gif");
                    }
                    if (mediaType == "videos" || mediaType == "all")
                    {
                        picker.FileTypeFilter.Add(".mp4");
                        picker.FileTypeFilter.Add(".mov");
                        picker.FileTypeFilter.Add(".wmv");
                    }

                    var multiple = GetBool(options, "multiple", false);
                    var files = multiple
                        ? (await picker.PickMultipleFilesAsync()).ToArray()
                        : new[] { await picker.PickSingleFileAsync() }.Where(f => f != null).ToArray();

                    var photos = new List<object>();
                    foreach (var file in files)
                    {
                        var local = await CopyToLocalAsync(file);
                        photos.Add(await BuildPhotoResultAsync(local, GetString(options, "resultType", "uri")));
                    }

                    return Ok(new { photos = photos });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public Task<string> checkCameraPermissions(string optionsJson)
            {
                return Task.FromResult(Ok(new { camera = "granted", photos = "granted" }));
            }

            public Task<string> requestCameraPermissions(string optionsJson)
            {
                return Task.FromResult(Ok(new { camera = "granted", photos = "granted" }));
            }

            public async Task<string> filesystemReadFile(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var path = ResolveFsPath(options);
                    var encoding = (GetString(options, "encoding") ?? "").ToLowerInvariant();
                    if (encoding == "utf8" || encoding == "utf-8")
                    {
                        return Ok(new { data = await File.ReadAllTextAsync(path), encoding = "utf8" });
                    }
                    return Ok(new { data = Convert.ToBase64String(await File.ReadAllBytesAsync(path)), encoding = "base64" });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> filesystemReadFileInChunks(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var path = ResolveFsPath(options);
                    var chunkSize = Math.Max(1024, GetInt(options, "chunkSize", 64 * 1024));
                    var chunks = new List<object>();
                    using (var stream = File.OpenRead(path))
                    {
                        var buffer = new byte[chunkSize];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            var slice = new byte[read];
                            Array.Copy(buffer, slice, read);
                            chunks.Add(new { data = Convert.ToBase64String(slice) });
                        }
                    }
                    return Ok(new { chunks = chunks });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> filesystemWriteFile(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var path = ResolveFsPath(options);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var data = GetString(options, "data", "");
                    var encoding = (GetString(options, "encoding") ?? "").ToLowerInvariant();
                    if (encoding == "utf8" || encoding == "utf-8")
                    {
                        await File.WriteAllTextAsync(path, data);
                    }
                    else
                    {
                        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            data = data.Substring(data.IndexOf(',') + 1);
                        }
                        await File.WriteAllBytesAsync(path, Convert.FromBase64String(data));
                    }
                    return Ok(new { uri = path });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> filesystemAppendFile(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var path = ResolveFsPath(options);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    await File.AppendAllTextAsync(path, GetString(options, "data", ""));
                    return Ok(true);
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public Task<string> filesystemDeleteFile(string optionsJson)
            {
                try
                {
                    File.Delete(ResolveFsPath(ParseOptions(optionsJson)));
                    return Task.FromResult(Ok(true));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> filesystemMkdir(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    Directory.CreateDirectory(ResolveFsPath(options));
                    return Task.FromResult(Ok(true));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> filesystemRmdir(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var path = ResolveFsPath(options);
                    Directory.Delete(path, GetBool(options, "recursive", false));
                    return Task.FromResult(Ok(true));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> filesystemReaddir(string optionsJson)
            {
                try
                {
                    var path = ResolveFsPath(ParseOptions(optionsJson));
                    var files = Directory.EnumerateFileSystemEntries(path)
                        .Select(item =>
                        {
                            var isDir = Directory.Exists(item);
                            var info = isDir ? null : new FileInfo(item);
                            return new
                            {
                                name = Path.GetFileName(item),
                                type = isDir ? "directory" : "file",
                                uri = item,
                                size = isDir ? 0 : info.Length,
                                mtime = isDir ? 0 : ToUnixMs(info.LastWriteTimeUtc)
                            };
                        })
                        .ToArray();
                    return Task.FromResult(Ok(new { files = files }));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> filesystemGetUri(string optionsJson)
            {
                try
                {
                    return Task.FromResult(Ok(new { uri = ResolveFsPath(ParseOptions(optionsJson)) }));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> filesystemStat(string optionsJson)
            {
                try
                {
                    var path = ResolveFsPath(ParseOptions(optionsJson));
                    if (Directory.Exists(path))
                    {
                        var dir = new DirectoryInfo(path);
                        return Task.FromResult(Ok(new
                        {
                            type = "directory",
                            size = 0,
                            ctime = ToUnixMs(dir.CreationTimeUtc),
                            mtime = ToUnixMs(dir.LastWriteTimeUtc),
                            uri = path
                        }));
                    }
                    var file = new FileInfo(path);
                    return Task.FromResult(Ok(new
                    {
                        type = "file",
                        size = file.Length,
                        ctime = ToUnixMs(file.CreationTimeUtc),
                        mtime = ToUnixMs(file.LastWriteTimeUtc),
                        uri = path
                    }));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> filesystemRename(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var from = ResolveFsPath(options, "from");
                    var to = ResolveFsPath(options, "to");
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    if (Directory.Exists(from))
                    {
                        Directory.Move(from, to);
                    }
                    else
                    {
                        File.Move(from, to);
                    }
                    return Task.FromResult(Ok(true));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> filesystemCopy(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var from = ResolveFsPath(options, "from");
                    var to = ResolveFsPath(options, "to");
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    if (Directory.Exists(from))
                    {
                        CopyDirectory(from, to);
                    }
                    else
                    {
                        File.Copy(from, to, true);
                    }
                    return Task.FromResult(Ok(new { uri = to }));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            private static void CopyDirectory(string sourceDir, string destDir)
            {
                Directory.CreateDirectory(destDir);
                foreach (var file in Directory.GetFiles(sourceDir))
                {
                    File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
                }
                foreach (var dir in Directory.GetDirectories(sourceDir))
                {
                    CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
                }
            }

            public async Task<string> fileTransferDownload(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var url = GetString(options, "url", GetString(options, "source"));
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        return Fail("url is required");
                    }
                    var path = ResolveFsPath(options);
                    if (Directory.Exists(path))
                    {
                        path = Path.Combine(path, Path.GetFileName(new Uri(url).LocalPath));
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));

                    using (var response = await _mainPage._httpClient.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        var total = response.Content.Headers.ContentLength ?? -1L;
                        using (var input = await response.Content.ReadAsStreamAsync())
                        using (var output = File.Open(path, FileMode.Create, FileAccess.Write))
                        {
                            var buffer = new byte[81920];
                            long readTotal = 0;
                            int read;
                            while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await output.WriteAsync(buffer, 0, read);
                                readTotal += read;
                                _mainPage.PostEvent("fileTransferProgress", new
                                {
                                    url = url,
                                    bytes = readTotal,
                                    contentLength = total
                                });
                            }
                        }
                    }
                    return Ok(new { path = path });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> fileTransferUpload(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var url = GetString(options, "url");
                    var path = ResolveFsPath(options);
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        return Fail("url is required");
                    }

                    var method = new HttpMethod(GetString(options, "method", "POST"));
                    var request = new HttpRequestMessage(method, new Uri(url));
                    var bytes = await File.ReadAllBytesAsync(path);
                    request.Content = new ByteArrayContent(bytes);
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GuessMime(path));
                    var response = await _mainPage._httpClient.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    return Ok(new { status = (int)response.StatusCode, response = body });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> openDocumentFromLocalPath(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var path = GetString(options, "path", GetString(options, "url"));
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    var success = await Launcher.LaunchFileAsync(file);
                    return success ? Ok(true) : Fail("Launcher could not open the file.");
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> openDocumentFromResources(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var path = GetString(options, "path", GetString(options, "fileName"));
                    var folder = await Package.Current.InstalledLocation.GetFolderAsync("Assets");
                    var wp = await folder.GetFolderAsync("WP");
                    var file = await wp.GetFileAsync(path);
                    var success = await Launcher.LaunchFileAsync(file);
                    return success ? Ok(true) : Fail("Launcher could not open the resource.");
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> openDocumentFromUrl(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var url = GetString(options, "url");
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        return Fail("A valid url is required.");
                    }
                    var success = await Launcher.LaunchUriAsync(uri);
                    return success ? Ok(true) : Fail("Launcher could not open the URL.");
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> getCurrentPosition(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var access = await Geolocator.RequestAccessAsync();
                    if (access == GeolocationAccessStatus.Denied)
                    {
                        return Fail("Location permission denied.");
                    }
                    var geolocator = new Geolocator
                    {
                        DesiredAccuracy = GetBool(options, "enableHighAccuracy", false)
                            ? PositionAccuracy.High
                            : PositionAccuracy.Default
                    };
                    var position = await geolocator.GetGeopositionAsync();
                    return Ok(BuildPosition(position));
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> watchPosition(string optionsJson)
            {
                try
                {
                    var access = await Geolocator.RequestAccessAsync();
                    if (access == GeolocationAccessStatus.Denied)
                    {
                        return Fail("Location permission denied.");
                    }
                    var id = Guid.NewGuid().ToString("N");
                    var geolocator = new Geolocator { ReportInterval = 1000 };
                    TypedEventHandler<Geolocator, PositionChangedEventArgs> handler = (_, args) =>
                    {
                        _mainPage.PostEvent($"geolocationWatch:{id}", BuildPosition(args.Position));
                    };
                    geolocator.PositionChanged += handler;
                    GeoWatches[id] = new GeoWatchRegistration { Locator = geolocator, Handler = handler };
                    return Ok(new { id = id });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public Task<string> clearPositionWatch(string optionsJson)
            {
                try
                {
                    var id = GetString(ParseOptions(optionsJson), "id");
                    if (!string.IsNullOrWhiteSpace(id) && GeoWatches.TryGetValue(id, out var watch))
                    {
                        watch.Locator.PositionChanged -= watch.Handler;
                        GeoWatches.Remove(id);
                    }
                    return Task.FromResult(Ok(true));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public async Task<string> checkGeolocationPermissions(string optionsJson)
            {
                try
                {
                    var access = await Geolocator.RequestAccessAsync();
                    var state = access == GeolocationAccessStatus.Allowed ? "granted" :
                                access == GeolocationAccessStatus.Denied ? "denied" : "prompt";
                    return Ok(new { location = state, coarseLocation = state });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> requestGeolocationPermissions(string optionsJson)
            {
                return await checkGeolocationPermissions(optionsJson);
            }

            public Task<string> keyboardShow()
            {
                try
                {
                    var shown = InputPane.GetForCurrentView().TryShow();
                    return Task.FromResult(Ok(new { value = shown }));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> keyboardHide()
            {
                try
                {
                    var hidden = InputPane.GetForCurrentView().TryHide();
                    return Task.FromResult(Ok(new { value = hidden }));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> keyboardSetResizeMode(string optionsJson)
            {
                _keyboardResizeMode = GetString(ParseOptions(optionsJson), "mode", "native");
                return Task.FromResult(Ok(true));
            }

            public Task<string> keyboardGetResizeMode()
            {
                return Task.FromResult(Ok(new { mode = _keyboardResizeMode }));
            }

            public Task<string> keyboardSetStyle(string optionsJson)
            {
                return Task.FromResult(Ok(true));
            }

            public Task<string> startMotionUpdates(string optionsJson)
            {
                try
                {
                    if (_motionStarted)
                    {
                        return Task.FromResult(Ok(true));
                    }

                    _accelerometer = Accelerometer.GetDefault();
                    if (_accelerometer != null)
                    {
                        _accelerometer.ReportInterval = Math.Max(_accelerometer.MinimumReportInterval, 50);
                        _accelerometer.ReadingChanged += (_, args) =>
                        {
                            var r = args.Reading;
                            _mainPage.PostEvent("motionAccel", new
                            {
                                acceleration = new { x = r.AccelerationX, y = r.AccelerationY, z = r.AccelerationZ },
                                accelerationIncludingGravity = new { x = r.AccelerationX, y = r.AccelerationY, z = r.AccelerationZ },
                                interval = _accelerometer.ReportInterval
                            });
                        };
                    }

                    _inclinometer = Inclinometer.GetDefault();
                    if (_inclinometer != null)
                    {
                        _inclinometer.ReportInterval = Math.Max(_inclinometer.MinimumReportInterval, 50);
                        _inclinometer.ReadingChanged += (_, args) =>
                        {
                            var r = args.Reading;
                            _mainPage.PostEvent("motionOrientation", new
                            {
                                alpha = r.YawDegrees,
                                beta = r.PitchDegrees,
                                gamma = r.RollDegrees
                            });
                        };
                    }

                    _gyrometer = Gyrometer.GetDefault();
                    _compass = Compass.GetDefault();
                    _motionStarted = true;
                    return Task.FromResult(Ok(new
                    {
                        accelerometer = _accelerometer != null,
                        inclinometer = _inclinometer != null,
                        gyrometer = _gyrometer != null,
                        compass = _compass != null
                    }));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> stopMotionUpdates()
            {
                try
                {
                    if (_accelerometer != null) _accelerometer.ReportInterval = 0;
                    if (_inclinometer != null) _inclinometer.ReportInterval = 0;
                    if (_gyrometer != null) _gyrometer.ReportInterval = 0;
                    if (_compass != null) _compass.ReportInterval = 0;
                    _motionStarted = false;
                    return Task.FromResult(Ok(true));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> secureSet(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var key = GetString(options, "key");
                    var value = GetString(options, "value", "");
                    var service = GetString(options, "service", SecureResourcePrefix);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        return Task.FromResult(Fail("key is required"));
                    }

                    var vault = new PasswordVault();
                    try
                    {
                        foreach (var existing in vault.FindAllByResource(service).Where(c => c.UserName == key).ToArray())
                        {
                            vault.Remove(existing);
                        }
                    }
                    catch { }
                    vault.Add(new PasswordCredential(service, key, value));
                    return Task.FromResult(Ok(true));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> secureGet(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var key = GetString(options, "key");
                    var service = GetString(options, "service", SecureResourcePrefix);
                    var credential = new PasswordVault().Retrieve(service, key);
                    credential.RetrievePassword();
                    return Task.FromResult(Ok(new { value = credential.Password }));
                }
                catch
                {
                    return Task.FromResult(Ok(new { value = (string)null }));
                }
            }

            public Task<string> secureRemove(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var key = GetString(options, "key");
                    var service = GetString(options, "service", SecureResourcePrefix);
                    var vault = new PasswordVault();
                    foreach (var existing in vault.FindAllByResource(service).Where(c => c.UserName == key).ToArray())
                    {
                        vault.Remove(existing);
                    }
                    return Task.FromResult(Ok(true));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> secureClear(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var service = GetString(options, "service", SecureResourcePrefix);
                    var vault = new PasswordVault();
                    foreach (var existing in vault.FindAllByResource(service).ToArray())
                    {
                        vault.Remove(existing);
                    }
                    return Task.FromResult(Ok(true));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(Fail(ex.Message));
                }
            }

            public Task<string> secureKeys(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var service = GetString(options, "service", SecureResourcePrefix);
                    var keys = new PasswordVault().FindAllByResource(service).Select(c => c.UserName).Distinct().ToArray();
                    return Task.FromResult(Ok(new { keys = keys }));
                }
                catch
                {
                    return Task.FromResult(Ok(new { keys = Array.Empty<string>() }));
                }
            }

            public async Task<string> checkUserVerificationAvailability()
            {
                try
                {
                    var availability = await UserConsentVerifier.CheckAvailabilityAsync();
                    return Ok(new
                    {
                        available = availability == UserConsentVerifierAvailability.Available,
                        status = availability.ToString()
                    });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> requestUserVerification(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var message = GetString(options, "message", "Verify your identity");
                    var result = await UserConsentVerifier.RequestVerificationAsync(message);
                    return Ok(new { verified = result == UserConsentVerificationResult.Verified, status = result.ToString() });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> registerPushNotifications(string optionsJson)
            {
                try
                {
                    var channel = await PushNotificationChannelManager.CreatePushNotificationChannelForApplicationAsync();
                    channel.PushNotificationReceived += (_, args) =>
                    {
                        args.Cancel = true;
                        _mainPage.PostEvent("pushNotificationReceived", new
                        {
                            title = args.NotificationType.ToString(),
                            body = args.RawNotification?.Content,
                            data = args.NotificationType.ToString()
                        });
                    };
                    var token = new { value = channel.Uri };
                    _mainPage.PostEvent("pushRegistration", token);
                    return Ok(token);
                }
                catch (Exception ex)
                {
                    _mainPage.PostEvent("pushRegistrationError", new { error = ex.Message });
                    return Fail(ex.Message);
                }
            }

            public Task<string> unregisterPushNotifications()
            {
                return Task.FromResult(Ok(true));
            }

            public async Task<string> scanBarcode(string optionsJson)
            {
                try
                {
                    var scanner = await BarcodeScanner.GetDefaultAsync();
                    if (scanner == null)
                    {
                        return Fail("No UWP POS barcode scanner is available. Camera barcode scanning is not implemented in this host.");
                    }

                    var claimed = await scanner.ClaimScannerAsync();
                    if (claimed == null)
                    {
                        return Fail("Barcode scanner could not be claimed.");
                    }

                    var tcs = new TaskCompletionSource<object>();
                    TypedEventHandler<ClaimedBarcodeScanner, BarcodeScannerDataReceivedEventArgs> handler = null;
                    handler = (s, args) =>
                    {
                        var bytes = args.Report.ScanDataLabel?.ToArray() ?? Array.Empty<byte>();
                        var text = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
                        tcs.TrySetResult(new
                        {
                            ScanResult = text,
                            format = args.Report.ScanDataType.ToString()
                        });
                    };
                    claimed.DataReceived += handler;
                    claimed.IsDecodeDataEnabled = true;
                    await claimed.EnableAsync();

                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(30000));
                    claimed.DataReceived -= handler;
                    claimed.Dispose();

                    if (completed != tcs.Task)
                    {
                        return Fail("Barcode scan timed out.");
                    }
                    return Ok(await tcs.Task);
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public Task<string> checkBackgroundScriptPermissions()
            {
                return Task.FromResult(Ok(new
                {
                    geolocation = CurrentGeoPermission(),
                    notifications = "granted"
                }));
            }

            public async Task<string> requestBackgroundScriptPermissions(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var geolocation = CurrentGeoPermission();
                    if (HasApi(options, "geolocation"))
                    {
                        geolocation = MapGeoPermission(await Geolocator.RequestAccessAsync());
                    }

                    var backgroundAccess = await BackgroundExecutionManager.RequestAccessAsync();
                    return Ok(new
                    {
                        geolocation = geolocation,
                        notifications = "granted",
                        background = backgroundAccess.ToString()
                    });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> configureBackgroundScriptRunner(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var label = GetString(options, "label", "default");
                    var src = GetString(options, "src", "background.js");
                    var eventName = GetString(options, "event", "backgroundRunner");
                    var repeat = GetBool(options, "repeat", false);
                    var interval = Math.Max(15, GetInt(options, "interval", 15));
                    var autoStart = GetBool(options, "autoStart", true);

                    SaveScriptRunnerConfig(label, src, eventName, repeat, interval, autoStart);
                    var registered = false;
                    string access = null;

                    if (autoStart)
                    {
                        var status = await BackgroundExecutionManager.RequestAccessAsync();
                        access = status.ToString();
                        if (!Window.Current.Visible)
                        {
                            registered = RegisterScriptRunnerTask(label, interval, repeat);
                        }
                    }

                    return Ok(new
                    {
                        label = label,
                        src = src,
                        eventName = eventName,
                        repeat = repeat,
                        interval = interval,
                        autoStart = autoStart,
                        registered = registered,
                        backgroundAccess = access
                    });
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            public async Task<string> dispatchBackgroundScriptEvent(string optionsJson)
            {
                try
                {
                    var options = ParseOptions(optionsJson);
                    var label = GetString(options, "label", "default");
                    var config = GetSavedScriptRunnerConfig(label);
                    var src = GetString(options, "src", GetString(config, "src", "background.js"));
                    var eventName = GetString(options, "event", GetString(config, "event", null));
                    if (string.IsNullOrWhiteSpace(eventName))
                    {
                        return Fail("event is required");
                    }

                    var detailsJson = GetRawJson(options, "details", "{}");
                    var bootstrap = GetString(options, "bootstrap", null);
                    var result = await RunScriptRunnerEventAsync(label, src, eventName, detailsJson, bootstrap);
                    return Ok(result);
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            private static void SaveScriptRunnerConfig(string label, string src, string eventName, bool repeat, int interval, bool autoStart)
            {
                var settings = ApplicationData.Current.LocalSettings.Values;
                var prefix = ScriptRunnerSettingsPrefix + label + ":";
                settings[prefix + "src"] = src;
                settings[prefix + "event"] = eventName;
                settings[prefix + "repeat"] = repeat;
                settings[prefix + "interval"] = interval;
                settings[prefix + "autoStart"] = autoStart;
                settings[ScriptRunnerSettingsPrefix + "lastLabel"] = label;
            }

            private static Dictionary<string, JsonElement> GetSavedScriptRunnerConfig(string label)
            {
                var settings = ApplicationData.Current.LocalSettings.Values;
                var prefix = ScriptRunnerSettingsPrefix + label + ":";
                var json = JsonSerializer.Serialize(new
                {
                    src = settings.TryGetValue(prefix + "src", out var src) ? src?.ToString() : null,
                    @event = settings.TryGetValue(prefix + "event", out var eventName) ? eventName?.ToString() : null,
                    repeat = settings.TryGetValue(prefix + "repeat", out var repeat) && repeat is bool repeatBool && repeatBool,
                    interval = settings.TryGetValue(prefix + "interval", out var interval) ? Convert.ToInt32(interval) : 15,
                    autoStart = settings.TryGetValue(prefix + "autoStart", out var autoStart) && autoStart is bool autoStartBool && autoStartBool
                });
                return ParseOptions(json);
            }

            private static bool RegisterScriptRunnerTask(string label, int interval, bool repeat)
            {
                var taskName = ScriptRunnerTaskPrefix + label;
                foreach (var task in BackgroundTaskRegistration.AllTasks)
                {
                    if (task.Value.Name == taskName)
                    {
                        task.Value.Unregister(true);
                    }
                }

                var builder = new BackgroundTaskBuilder
                {
                    Name = taskName
                };
                builder.SetTrigger(new TimeTrigger((uint)Math.Max(15, interval), !repeat));
                builder.Register();
                return true;
            }

            public async Task DispatchConfiguredScriptRunnerAsync(string taskName)
            {
                var label = (taskName ?? "").StartsWith(ScriptRunnerTaskPrefix, StringComparison.Ordinal)
                    ? taskName.Substring(ScriptRunnerTaskPrefix.Length)
                    : GetLastScriptRunnerLabel();
                var config = GetSavedScriptRunnerConfig(label);
                var src = GetString(config, "src", "background.js");
                var eventName = GetString(config, "event", "backgroundRunner");
                await RunScriptRunnerEventAsync(label, src, eventName, "{}", null);
            }

            public async Task RegisterSavedScriptRunnerAsync()
            {
                var label = GetLastScriptRunnerLabel();
                var config = GetSavedScriptRunnerConfig(label);
                if (!GetBool(config, "autoStart", false))
                {
                    return;
                }

                await BackgroundExecutionManager.RequestAccessAsync();
                RegisterScriptRunnerTask(
                    label,
                    Math.Max(15, GetInt(config, "interval", 15)),
                    GetBool(config, "repeat", false));
            }

            private static string GetLastScriptRunnerLabel()
            {
                var settings = ApplicationData.Current.LocalSettings.Values;
                return settings.TryGetValue(ScriptRunnerSettingsPrefix + "lastLabel", out var label)
                    ? label?.ToString() ?? "default"
                    : "default";
            }

            private async Task<string> ReadScriptRunnerSourceAsync(string src)
            {
                var folder = await Package.Current.InstalledLocation.GetFolderAsync("Assets");
                var wp = await folder.GetFolderAsync("WP");
                var normalized = (src ?? "background.js").Replace('\\', '/').TrimStart('/');
                var relative = normalized.Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(wp.Path, relative));
                var root = Path.GetFullPath(wp.Path);
                if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("Script runner src must stay inside Assets/WP.");
                }
                return await File.ReadAllTextAsync(fullPath);
            }

            private async Task<object> RunScriptRunnerEventAsync(string label, string src, string eventName, string detailsJson, string bootstrapSource)
            {
                var runnerSource = await ReadScriptRunnerSourceAsync(src);
                object result = null;

                await _mainPage.RunOnUiThreadAsync(async () =>
                {
                    var webView = new Microsoft.UI.Xaml.Controls.WebView2
                    {
                        Width = 1,
                        Height = 1,
                        Visibility = Visibility.Collapsed
                    };
                    _mainPage.RootGrid.Children.Add(webView);

                    var tcs = new TaskCompletionSource<JsonElement>();
                    TypedEventHandler<CoreWebView2, CoreWebView2WebMessageReceivedEventArgs> handler = null;
                    handler = (sender, args) =>
                    {
                        try
                        {
                            var message = args.TryGetWebMessageAsString();
                            using (var document = JsonDocument.Parse(message))
                            {
                                var root = document.RootElement;
                                if (!root.TryGetProperty("source", out var sourceProp) || sourceProp.GetString() != "UwpScriptRunner")
                                {
                                    return;
                                }
                                var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : "";
                                if (type == "notification")
                                {
                                    if (root.TryGetProperty("data", out var notificationProp))
                                    {
                                        var raw = notificationProp.GetRawText();
                                        var payload = notificationProp.ValueKind == JsonValueKind.Array
                                            ? "{\"notifications\":" + raw + "}"
                                            : raw;
                                        var _ = scheduleLocalNotifications(payload);
                                    }
                                    return;
                                }
                                if (type != "resolve" && type != "reject")
                                {
                                    return;
                                }
                                tcs.TrySetResult(root.Clone());
                            }
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                    };

                    try
                    {
                        await webView.EnsureCoreWebView2Async();
                        webView.CoreWebView2.WebMessageReceived += handler;
                        var navigationTcs = new TaskCompletionSource<bool>();
                        TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs> navigationHandler = null;
                        navigationHandler = (sender, args) => navigationTcs.TrySetResult(true);
                        webView.CoreWebView2.NavigationCompleted += navigationHandler;
                        webView.CoreWebView2.NavigateToString("<!doctype html><html><head><meta charset=\"utf-8\"></head><body></body></html>");
                        await Task.WhenAny(navigationTcs.Task, Task.Delay(5000));
                        webView.CoreWebView2.NavigationCompleted -= navigationHandler;

                        await webView.CoreWebView2.ExecuteScriptAsync(BuildScriptRunnerBootstrapScript());
                        if (!string.IsNullOrWhiteSpace(bootstrapSource))
                        {
                            await webView.CoreWebView2.ExecuteScriptAsync(bootstrapSource);
                        }
                        await webView.CoreWebView2.ExecuteScriptAsync(runnerSource);
                        await webView.CoreWebView2.ExecuteScriptAsync(BuildScriptRunnerDispatchScript(label, eventName, detailsJson));

                        var completed = await Task.WhenAny(tcs.Task, Task.Delay(30000));
                        if (completed != tcs.Task)
                        {
                            throw new TimeoutException("Script runner timed out waiting for resolve() or reject().");
                        }

                        var message = await tcs.Task;
                        var type = message.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : "";
                        if (type == "reject")
                        {
                            var error = message.TryGetProperty("error", out var errorProp) ? errorProp.ToString() : "Script runner rejected.";
                            throw new Exception(error);
                        }
                        result = message.TryGetProperty("data", out var dataProp)
                            ? JsonSerializer.Deserialize<object>(dataProp.GetRawText())
                            : null;
                    }
                    finally
                    {
                        if (webView.CoreWebView2 != null && handler != null)
                        {
                            webView.CoreWebView2.WebMessageReceived -= handler;
                        }
                        _mainPage.RootGrid.Children.Remove(webView);
                    }
                });

                return result;
            }

            private static string BuildScriptRunnerBootstrapScript()
            {
                return @"
(() => {
  const nativeAddEventListener = window.addEventListener.bind(window);
  const listeners = new Map();
  const send = (message) => chrome.webview.postMessage(JSON.stringify({ source: 'UwpScriptRunner', ...message }));
  window.addEventListener = (eventName, callback) => {
    if (typeof callback === 'function') {
      const list = listeners.get(eventName) || [];
      list.push(callback);
      listeners.set(eventName, list);
      return;
    }
    return nativeAddEventListener(eventName, callback);
  };
  window.__uwpDispatchScriptRunnerEvent = (eventName, details) => new Promise((resolve, reject) => {
    const handler = (listeners.get(eventName) || [])[0];
    if (!handler) {
      reject(new Error(`No script runner listener registered for '${eventName}'.`));
      return;
    }
    let settled = false;
    const done = (value) => {
      if (settled) return;
      settled = true;
      resolve(value === undefined ? null : value);
    };
    const fail = (error) => {
      if (settled) return;
      settled = true;
      reject(error);
    };
    try {
      handler(done, fail, details || {});
    } catch (error) {
      fail(error);
    }
  });
  window.UwpScriptStorage = {
    set: (key, value) => localStorage.setItem(String(key), String(value)),
    get: (key) => ({ value: localStorage.getItem(String(key)) }),
    remove: (key) => localStorage.removeItem(String(key)),
  };
  window.UwpScriptDevice = {
    getNetworkStatus: () => ({ connected: navigator.onLine, connectionType: navigator.onLine ? 'unknown' : 'none' }),
    getBatteryStatus: async () => {
      if (navigator.getBattery) {
        const battery = await navigator.getBattery();
        return { batteryLevel: battery.level, isCharging: battery.charging };
      }
      return { batteryLevel: null, isCharging: false };
    },
  };
  window.UwpScriptNotifications = {
    schedule: (notifications) => send({ type: 'notification', data: notifications }),
    setBadge: () => {},
    clearBadge: () => {},
  };
})();
";
            }

            private static string BuildScriptRunnerDispatchScript(string label, string eventName, string detailsJson)
            {
                var labelJson = JsonSerializer.Serialize(label ?? "default");
                var eventJson = JsonSerializer.Serialize(eventName);
                if (string.IsNullOrWhiteSpace(detailsJson))
                {
                    detailsJson = "{}";
                }
                return $@"
(async () => {{
  try {{
    const value = await window.__uwpDispatchScriptRunnerEvent({eventJson}, {detailsJson});
    chrome.webview.postMessage(JSON.stringify({{ source: 'UwpScriptRunner', type: 'resolve', label: {labelJson}, data: value === undefined ? null : value }}));
  }} catch (error) {{
    chrome.webview.postMessage(JSON.stringify({{ source: 'UwpScriptRunner', type: 'reject', label: {labelJson}, error: error && (error.message || String(error)) }}));
  }}
}})();
";
            }

            public Task<string> GetPlatform()
            {
                try
                {
                    var deviceFamily = Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily;
                    string platform = deviceFamily.Equals("Windows.Xbox", StringComparison.OrdinalIgnoreCase)
                            ? "xbox"
                            : "windows";

                    return Task.FromResult(JsonSerializer.Serialize(new { completed = true, data = platform }));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(JsonSerializer.Serialize(new { completed = false, error = ex.Message }));
                }
            }

            public async Task<string> VibrateController(string durationMs, string strength)
            {
                try
                {
                    int duration = 300;
                    if (!string.IsNullOrWhiteSpace(durationMs) && int.TryParse(durationMs, out var parsedDuration))
                    {
                        if (parsedDuration >= 0)
                        {
                            duration = parsedDuration;
                        }
                    }

                    double power = 1.0;
                    if (!string.IsNullOrWhiteSpace(strength))
                    {
                        if (double.TryParse(strength, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ||
                            double.TryParse(strength, out p))
                        {
                            power = p;
                        }
                    }
                    if (power < 0.1) power = 0.1;
                    if (power > 1.0) power = 1.0;

                    var listCount = Windows.Gaming.Input.Gamepad.Gamepads.Count;
                    var gamepad = _mainPage.CurrentGamepad ?? Windows.Gaming.Input.Gamepad.Gamepads.LastOrDefault();
                    if (gamepad == null)
                    {
                        return JsonSerializer.Serialize(new { completed = false, error = $"No gamepad connected (Gamepads.Count={listCount})" });
                    }

                    var vibration = new Windows.Gaming.Input.GamepadVibration
                    {
                        LeftMotor = power,
                        RightMotor = power,
                        LeftTrigger = 0,
                        RightTrigger = 0
                    };

                    gamepad.Vibration = vibration;
                    await Task.Delay(duration);
                    gamepad.Vibration = new Windows.Gaming.Input.GamepadVibration();

                    return JsonSerializer.Serialize(new { completed = true, data = new { duration = duration, strength = power } });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }


            public async Task<string> quitApp()
            {
                try
                {
                    CoreApplication.Exit();
                    return JsonSerializer.Serialize(new { completed = true, data = "closing" });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> canOpenUrl(string url)
            {
                try
                {
                    var uri = new Uri(url);
                    
                    var launchQuerySupportType = Windows.System.LaunchQuerySupportType.Uri;
                    var launchQuerySupportStatus = await Windows.System.Launcher.QueryUriSupportAsync(uri, launchQuerySupportType);
                    
                    bool canOpen = launchQuerySupportStatus == Windows.System.LaunchQuerySupportStatus.Available;
                    
                    return JsonSerializer.Serialize(new { completed = true, data = canOpen });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }

            public async Task<string> openUrl(string url)
            {
                try
                {
                    var uri = new Uri(url);
                    bool success = await Windows.System.Launcher.LaunchUriAsync(uri);
                    
                    return JsonSerializer.Serialize(new { completed = true, data = success });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { completed = false, error = ex.Message });
                }
            }
        }
    }
}
