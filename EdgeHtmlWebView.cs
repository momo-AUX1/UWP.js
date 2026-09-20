// ARM32 Windows 10 Mobile has EdgeHTML WebView, but no WebView2 runtime.
// Keep the WebView2-shaped host calls in MainPage while adapting the small
// transport/navigation surface to the OS-provided control.
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;

namespace Microsoft.UI.Xaml.Controls
{
    public sealed class WebView2 : Grid
    {
        private readonly Windows.UI.Xaml.Controls.WebView _view = new Windows.UI.Xaml.Controls.WebView();
        private Uri _source;

        public WebView2()
        {
            Children.Add(_view);
            CoreWebView2 = new Microsoft.Web.WebView2.Core.CoreWebView2(_view);
        }

        public Microsoft.Web.WebView2.Core.CoreWebView2 CoreWebView2 { get; }
        public Task EnsureCoreWebView2Async() => Task.CompletedTask;

        public Uri Source
        {
            get => _source;
            set
            {
                _source = value;
                if (value == null) return;
                // Package pages retain relative asset URLs on EdgeHTML.
                var destination = value.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    ? new Uri("ms-appx-web:///Assets/WP/" + value.AbsolutePath.TrimStart('/') + value.Query)
                    : value;
                _view.Navigate(destination);
            }
        }
    }
}

namespace Microsoft.Web.WebView2.Core
{
    public enum CoreWebView2HostResourceAccessKind { Allow }
    public enum CoreWebView2WebResourceContext { All, Document }
    public enum CoreWebView2PermissionState { Allow }

    public sealed class CoreWebView2WebMessageReceivedEventArgs
    {
        private readonly string _message;
        public CoreWebView2WebMessageReceivedEventArgs(string message) { _message = message; }
        public string TryGetWebMessageAsString() => _message;
    }

    public sealed class CoreWebView2NavigationCompletedEventArgs { }
    public sealed class CoreWebView2NewWindowRequestedEventArgs
    {
        public string Uri { get; set; }
        public bool Handled { get; set; }
    }
    public sealed class CoreWebView2PermissionRequestedEventArgs
    {
        public CoreWebView2PermissionState State { get; set; }
    }
    public sealed class CoreWebView2WebResourceHeaders
    {
        public void SetHeader(string name, string value) { }
        public void AppendHeader(string name, string value) { }
    }
    public sealed class CoreWebView2WebResourceRequest
    {
        public string Uri { get; set; }
        public CoreWebView2WebResourceHeaders Headers { get; } = new CoreWebView2WebResourceHeaders();
    }
    public sealed class CoreWebView2WebResourceResponse
    {
        public CoreWebView2WebResourceHeaders Headers { get; } = new CoreWebView2WebResourceHeaders();
    }
    public sealed class CoreWebView2WebResourceRequestedEventArgs
    {
        public CoreWebView2WebResourceRequest Request { get; } = new CoreWebView2WebResourceRequest();
        public CoreWebView2WebResourceContext ResourceContext { get; set; }
        public CoreWebView2WebResourceResponse Response { get; set; }
    }
    public sealed class CoreWebView2Environment
    {
        public CoreWebView2WebResourceResponse CreateWebResourceResponse(
            IRandomAccessStream stream, int statusCode, string reasonPhrase, string headers)
            => new CoreWebView2WebResourceResponse();
    }

    public sealed class CoreWebView2
    {
        private readonly Windows.UI.Xaml.Controls.WebView _view;
        public CoreWebView2Environment Environment { get; } = new CoreWebView2Environment();
        public bool CanGoBack => _view.CanGoBack;

        public event TypedEventHandler<CoreWebView2, CoreWebView2WebMessageReceivedEventArgs> WebMessageReceived;
        public event TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs> NavigationCompleted;
        public event TypedEventHandler<CoreWebView2, CoreWebView2NewWindowRequestedEventArgs> NewWindowRequested;
        public event TypedEventHandler<CoreWebView2, CoreWebView2PermissionRequestedEventArgs> PermissionRequested;
        public event TypedEventHandler<CoreWebView2, CoreWebView2WebResourceRequestedEventArgs> WebResourceRequested;

        public CoreWebView2(Windows.UI.Xaml.Controls.WebView view)
        {
            _view = view;
            _view.ScriptNotify += (_, args) =>
                WebMessageReceived?.Invoke(this, new CoreWebView2WebMessageReceivedEventArgs(args.Value));
            _view.NavigationCompleted += (_, __) =>
                NavigationCompleted?.Invoke(this, new CoreWebView2NavigationCompletedEventArgs());
            _view.NewWindowRequested += (_, args) =>
            {
                var request = new CoreWebView2NewWindowRequestedEventArgs { Uri = args.Uri?.ToString() };
                NewWindowRequested?.Invoke(this, request);
                args.Handled = request.Handled;
            };
        }

        public void SetVirtualHostNameToFolderMapping(string host, string folder,
            CoreWebView2HostResourceAccessKind access)
        {
            if (host == "localhost" || host == "localdata" ||
                ((host == "selectedfiles" || host == "selectedcontent") &&
                 string.Equals(folder, ApplicationData.Current.LocalFolder.Path,
                     StringComparison.OrdinalIgnoreCase))) return;
            // EdgeHTML cannot expose arbitrary picker folders as an HTTP origin.
            throw new NotSupportedException("EdgeHTML cannot map external folders into the WebView.");
        }

        public void AddWebResourceRequestedFilter(string uri, CoreWebView2WebResourceContext context) { }
        public void NavigateToString(string html)
        {
            // The background runner starts from a string, outside the app's
            // index.html. Install the same bridge before its own scripts run.
            const string bridge = "<script>window.chrome=window.chrome||{};" +
                "window.chrome.webview={postMessage:function(m){window.external.notify(typeof m==='string'?m:JSON.stringify(m));}," +
                "addEventListener:function(){},removeEventListener:function(){}};" +
                "window.__capacitorHostReceive=function(){};</script>";
            _view.NavigateToString(html.Contains("<head>")
                ? html.Replace("<head>", "<head>" + bridge)
                : bridge + html);
        }
        public async Task<string> ExecuteScriptAsync(string script)
            => await _view.InvokeScriptAsync("eval", new[] { script });

        public async void PostWebMessageAsString(string message)
        {
            try
            {
                await ExecuteScriptAsync("window.__capacitorHostReceive(" +
                    JsonSerializer.Serialize(message) + ");");
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine("EdgeHTML host message failed: " + error);
            }
        }
    }
}
