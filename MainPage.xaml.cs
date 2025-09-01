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

        private async void WebView2_WebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            var uri = new Uri(args.Request.Uri);
            var headers = args.Request.Headers;

            headers.SetHeader("Access-Control-Allow-Origin", "*");
            headers.SetHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            headers.SetHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");

            headers.SetHeader("Content-Security-Policy", "script-src 'self' 'unsafe-eval'; object-src 'self';");

            if (uri.AbsolutePath.EndsWith(".wasm"))
            {
                headers.SetHeader("Content-Type", "application/wasm");
            }

            if (customHeaders != null && customHeaders.Count > 0)
            {
                foreach (var header in customHeaders)
                {
                    headers.SetHeader(header.Key, header.Value);
                }
            }
        }

        private async void WebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            var message = args.TryGetWebMessageAsString();
            var request = JsonSerializer.Deserialize<Dictionary<string, object>>(message);

            if (request != null && request.ContainsKey("method") && request.ContainsKey("args"))
            {
                var methodName = request["method"].ToString();
                var argsArray = ((JsonElement)request["args"]).EnumerateArray().Select(a => a.GetString()).ToArray();

                var response = await CallNativeMethodAsync(methodName, argsArray);
                var responseMessage = JsonSerializer.Serialize(new { result = response });

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
                FolderPicker folderPicker = new FolderPicker();
                folderPicker.SuggestedStartLocation = PickerLocationId.Desktop;
                folderPicker.FileTypeFilter.Add("*");
                StorageFolder folder = await folderPicker.PickSingleFolderAsync();

                if (folder == null)
                {
                    return "No folder selected";
                }

                _coreWebView2.SetVirtualHostNameToFolderMapping("selectedcontent", folder.Path, CoreWebView2HostResourceAccessKind.Allow);

                return $"{folder.Path}";
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
                        return base64String;
                    }
                    else
                    {
                        var content = await FileIO.ReadTextAsync(file);
                        return content;
                    }
                }
                catch (Exception ex)
                {
                    return $"Error reading file: {ex.Message}";
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
                    return "File written successfully";
                }
                catch (Exception ex)
                {
                    return $"Error writing file: {ex.Message}";
                }
            }

            public async Task<string> readDir(string folderPath)
            {
                try
                {
                    var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
                    var files = await folder.GetFilesAsync();
                    var fileNames = string.Join(",", files.Select(f => f.Name));
                    return fileNames;
                }
                catch (Exception ex)
                {
                    return $"Error reading directory: {ex.Message}";
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
                                        .Concat(folders.Cast<IStorageItem>());

                    var result = $"{localFolder.Path}|{string.Join(",", allItems.Select(item => item.Path))}";
                    return result;
                }
                catch (Exception ex)
                {
                    return $"Error reading local directory: {ex.Message}";
                }
            }



            public async Task<string> showAlert(string title, string text)
            {
                try
                {
                    var messageDialog = new MessageDialog(text, title);
                    await messageDialog.ShowAsync();
                    return "Alert shown successfully";
                }
                catch (Exception ex)
                {
                    return $"Error showing alert: {ex.Message}";
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

                    return result.Id.ToString();
                }
                catch (Exception ex)
                {
                    return $"Error showing dialog: {ex.Message}";
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

                return _mainPage._cancelDownload ? "Download cancelled" : "Download completed";
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

                    if (dialogResult != "0")
                    {
                        return "Download cancelled";
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

                    return $"File downloaded successfully: {file.Path}";
                }
                catch (Exception ex)
                {
                    return $"Error downloading file: {ex.Message}";
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
                        return "No file selected";
                    }

                    _coreWebView2.SetVirtualHostNameToFolderMapping("selectedfiles", System.IO.Path.GetDirectoryName(file.Path), CoreWebView2HostResourceAccessKind.Allow);

                    return $"{file.Name}|{file.Path}";
                }
                catch (Exception ex)
                {
                    return $"Error selecting file: {ex.Message}";
                }
            }

            public async Task<string> setDownloadLocation(string path)
            {
                if (string.IsNullOrEmpty(path))
                {
                    downloadLocation = null;
                    return "Download location reset to default.";
                }

                try
                {
                    var folder = await StorageFolder.GetFolderFromPathAsync(path);
                    downloadLocation = folder.Path;
                    return $"Download location set to {downloadLocation}.";
                }
                catch (Exception ex)
                {
                    return $"Error setting download location: {ex.Message}";
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

                    return $"Folder created successfully: {folder.Path}";
                }
                catch (Exception ex)
                {
                    return $"Error creating folder: {ex.Message}";
                }
            }

            public async Task<string> redirect(string url)
            {
                if (string.IsNullOrEmpty(url))
                {
                    return "Invalid URL";
                }

                try
                {
                    _mainPage.WebView2.Source = new Uri(url);
                    return "Redirect successful";
                }
                catch (Exception ex)
                {
                    return $"Error during redirection: {ex.Message}";
                }
            }

            public async Task<string> HideCursor()
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
                return "Cursor hidden and pointer locked to center";
            }

            public async Task<string> ShowCursor()
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
                return "Cursor visible and pointer unlocked";
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
                            return $"Folder zipped successfully to {outputPath}";
                        }
                        else
                        {
                            return "Folder not found";
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
                            return $"Folder zipped successfully to {outputPath}";
                        }
                        else
                        {
                            return "Folder not found";
                        }
                    }
                }
                catch (Exception ex)
                {
                    return $"Error zipping folder: {ex.Message}";
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

                    async Task<string> UnzipFile(string filePath)
                    {
                        try
                        {
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
                                    }
                                }
                            }
                            return $"File unzipped successfully to {outputPath}";
                        }
                        catch (Exception ex)
                        {
                            return $"Error unzipping file at {outputPath}: {ex.Message}";
                        }
                    }

                    string resolvedZipPath = zipPath;
                    if (!Path.IsPathRooted(zipPath))
                    {
                        resolvedZipPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, zipPath);
                    }

                    if (File.Exists(resolvedZipPath))
                    {
                        return await UnzipFile(resolvedZipPath);
                    }
                    else
                    {
                        return $"Zip file not found at {resolvedZipPath}";
                    }
                }
                catch (Exception ex)
                {
                    return $"Error processing zip file at {zipPath} with output path {outputPath}: {ex.Message}";
                }
            }
            public async Task<string> deleteFile(string filePath)
            {
                try
                {
                    StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
                    await file.DeleteAsync();
                    return $"File deleted successfully: {filePath}";
                }
                catch (Exception ex)
                {
                    return $"Error deleting file: {filePath} - {ex.Message}";
                }
            }

            public async Task<string> deleteFolder(string folderPath)
            {
                try
                {
                    StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
                    await folder.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    return $"Folder deleted successfully: {folderPath}";
                }
                catch (Exception ex)
                {
                    return $"Error deleting folder: {folderPath} - {ex.Message}";
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
                        .Select(d => $"{d.Name} - {d.DriveType} - Free Space: {d.AvailableFreeSpace / (1024 * 1024)} MB - Total Space: {d.TotalSize / (1024 * 1024)} MB")
                        .ToArray();
                    string drivesInfo = string.Join(", ", drives);

                    return $"OS: {osVersion} | RAM: {usedMemory / (1024 * 1024)} MB / {totalMemory / (1024 * 1024)} MB | CPU Speed: {cpuSpeed} | CPU Cores: {processor_count} | Drives: {drivesInfo}";
                }
                catch (Exception ex)
                {
                    return $"Error retrieving system information: {ex.Message}";
                }
            }

            public async Task<string> setheaders(string headersJson)
            {
                try
                {
                    var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                    _mainPage.SetCustomHeaders(headers);
                    return "Headers set successfully";
                }
                catch (Exception ex)
                {
                    return $"Error setting headers: {ex.Message}";
                }
            }

            public async Task<string> clearheaders()
            {
                _mainPage.ClearCustomHeaders();
                return "Headers cleared";
            }

            public async Task<string> ShowNotification(string notificationJson)
            {
                try
                {
                    var notification = JsonSerializer.Deserialize<NotificationData>(notificationJson);

                    if (notification == null)
                        return "Invalid notification data.";

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

                    return "Notification shown successfully.";
                }
                catch (Exception ex)
                {
                    return $"Error showing notification: {ex.Message}";
                }
            }

            public async Task<string> ClearNotifications()
            {
                try
                {
                    ToastNotificationManagerCompat.History.Clear();
                    return "All notifications cleared successfully.";
                }
                catch (Exception ex)
                {
                    return $"Error clearing notifications: {ex.Message}";
                }
            }

            public Task<string> GetPlatform()
            {
                var deviceFamily = Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily;
                string platform = deviceFamily.Equals("Windows.Xbox", StringComparison.OrdinalIgnoreCase)
                        ? "xbox"
                        : "windows";

                return Task.FromResult(platform);
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
                        return $"Error No gamepad connected (Gamepads.Count={listCount})";
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

                    return $"Vibrated for {duration}ms at strength {power:0.##}";
                }
                catch (Exception ex)
                {
                    return $"Error vibrating controller: {ex.Message}";
                }
            }


            public async Task<string> quitApp()
            {
                try
                {
                    CoreApplication.Exit();
                    return "App is closing.";
                }
                catch (Exception ex)
                {
                    return $"Error quitting app: {ex.Message}";
                }
            }
        }
    }
}
