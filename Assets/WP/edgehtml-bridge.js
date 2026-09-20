(function () {
  if (window.chrome && window.chrome.webview) return;
  var listeners = [];
  window.chrome = window.chrome || {};
  window.__capacitorEdgeHTML = true;
  window.chrome.webview = {
    postMessage: function (message) {
      window.external.notify(typeof message === "string" ? message : JSON.stringify(message));
    },
    addEventListener: function (name, listener) {
      if (name === "message" && listeners.indexOf(listener) < 0) listeners.push(listener);
    },
    removeEventListener: function (name, listener) {
      if (name !== "message") return;
      var index = listeners.indexOf(listener);
      if (index >= 0) listeners.splice(index, 1);
    }
  };
  window.__capacitorHostReceive = function (data) {
    data = data.replace(/http:\/\/localdata\//g, "ms-appdata:///local/");
    listeners.slice().forEach(function (listener) { listener({ data: data }); });
  };
})();
