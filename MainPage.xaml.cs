using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
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

        public MainPage()
        {
            this.InitializeComponent();
            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "151330");
            InitializeWebView();
            Window.Current.CoreWindow.SizeChanged += CoreWindow_SizeChanged;
            _httpClient = new HttpClient();

            SystemNavigationManager.GetForCurrentView().BackRequested += App_BackRequested;
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
            e.Handled = true;
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
