class UwpBridge {
  constructor() {
    this.eventListeners = {};
    this.plugins = [];
    this._pending = new Map();
    this._nextId = 1;

    window.chrome.webview.addEventListener("message", (event) => {
      const raw = event.data;
      let payload;
      try {
        payload = JSON.parse(raw);
      } catch (e) {
        console.warn("UwpBridge: received non-JSON message", raw);
        return;
      }

      if (payload && payload.event) {
        this.emit(payload.event, payload.data);
        return;
      }

      if (payload && payload.id) {
        const entry = this._pending.get(payload.id);
        if (!entry) {
          return;
        }

        const { resolve, reject, timeout } = entry;
        clearTimeout(timeout);
        this._pending.delete(payload.id);

        if (payload.error) {
          reject(new Error(payload.error));
        } else {
          resolve(payload.result);
        }
      }
    });
  }

  async callNative(methodName, ...args) {
    const id = String(this._nextId++);
    const message = { id, method: methodName, args };

    const promise = new Promise((resolve, reject) => {
       const timeout = setTimeout(() => {
        this._pending.delete(id);
        reject(new Error(`RPC timeout for ${methodName} (id=${id})`));
      }, 30000);

      this._pending.set(id, { resolve, reject, timeout });
    });

    window.chrome.webview.postMessage(JSON.stringify(message));
    return promise;
  }

  registerPlugin(plugin) {
    this.plugins.push(plugin);
    if (plugin.init) {
      plugin.init(this);
    }
  }

  on(event, callback) {
    if (!this.eventListeners[event]) {
      this.eventListeners[event] = [];
    }
    this.eventListeners[event].push(callback);
  }

  emit(event, data) {
    if (this.eventListeners[event]) {
      this.eventListeners[event].forEach((callback) => callback(data));
    }
  }

  _handleStructuredResponse(result) {
    const response = JSON.parse(result);
    if (!response.completed) {
      throw new Error(response.error || 'Operation failed');
    }
    return response.data;
  }

  async readFile(fileName, codec = null) {
    const result = await this.callNative("read", fileName, codec);
    return this._handleStructuredResponse(result);
  }

  async writeFile(fileName, data) {
    const result = await this.callNative("write", fileName, data);
    return this._handleStructuredResponse(result);
  }

  async readDir(folderPath) {
    const result = await this.callNative("readDir", folderPath);
    return this._handleStructuredResponse(result);
  }

  async readLocalDir() {
    const result = await this.callNative("readLocalDir");
    return this._handleStructuredResponse(result);
  }

  async showAlert(title, text) {
    const result = await this.callNative("showAlert", title, text);
    return this._handleStructuredResponse(result);
  }

  async showDialog(title, body, yesButtonText = "Yes", noButtonText = "No") {
    const result = await this.callNative(
      "showDialog",
      title,
      body,
      yesButtonText,
      noButtonText,
    );
    return this._handleStructuredResponse(result);
  }

  async downloadFile(fileUrlOrData, encoding = "url", name = null) {
    const result = await this.callNative("downloadFile", fileUrlOrData, encoding, name);
    return this._handleStructuredResponse(result);
  }

  async selectFile() {
    const result = await this.callNative("selectFile");
    return this._handleStructuredResponse(result);
  }

  async setDownloadLocation(path) {
    const result = await this.callNative("setDownloadLocation", path);
    return this._handleStructuredResponse(result);
  }

  async createFolder(folderPathOrName) {
    const result = await this.callNative("createFolder", folderPathOrName);
    return this._handleStructuredResponse(result);
  }

  async pickFolder() {
    const result = await this.callNative("pickFolder");
    return this._handleStructuredResponse(result);
  }

  async redirect(url) {
    const result = await this.callNative("redirect", url);
    return this._handleStructuredResponse(result);
  }

  async zipFolder(folderPath, outputPath = null) {
    const result = await this.callNative("zipFolder", folderPath, outputPath);
    return this._handleStructuredResponse(result);
  }

  async unzip(zipPath, outputPath = null) {
    const result = await this.callNative("unzip", zipPath, outputPath);
    return this._handleStructuredResponse(result);
  }

  async deleteFile(filePath) {
    const result = await this.callNative("deleteFile", filePath);
    return this._handleStructuredResponse(result);
  }

  async deleteFolder(folderPath) {
    const result = await this.callNative("deleteFolder", folderPath);
    return this._handleStructuredResponse(result);
  }

  async getMachineStatus() {
    const result = await this.callNative("GetMachineStatus");
    return this._handleStructuredResponse(result);
  }

  async getPlatform() {
    const result = await this.callNative("GetPlatform");
    return this._handleStructuredResponse(result);
  }

  async quitApp() {
    const result = await this.callNative("quitApp");
    return this._handleStructuredResponse(result);
  }

  async hideCursor() {
    const result = await this.callNative("HideCursor");
    return this._handleStructuredResponse(result);
  }

  async showCursor() {
    const result = await this.callNative("ShowCursor");
    return this._handleStructuredResponse(result);
  }

  async setHeaders(headersObject) {
    const headersJson = JSON.stringify(headersObject);
    const result = await this.callNative("setheaders", headersJson);
    return this._handleStructuredResponse(result);
  }

  async clearHeaders() {
    const result = await this.callNative("clearheaders");
    return this._handleStructuredResponse(result);
  }

  /**
   * Shows a toast notification with the specified details.
   *
   * The `notificationData` object should contain the following properties:
   *
   * @param {Object} notificationData - The notification details.
   * @param {string} notificationData.Id - A unique identifier for the notification.
   * @param {string} notificationData.Title - The title text of the notification.
   * @param {string} notificationData.Message - The main message body of the notification.
   * @param {string} [notificationData.Image] -
   *        The image name or relative path (e.g., "cat.jpeg" or "/images/cat.jpg").
   *        If no URI scheme is provided, the image is searched in the `Assets/WP/` directory.
   * @param {string} [notificationData.AppLogoOverride] -
   *        The app logo image name or relative path.
   *        If no URI scheme is provided, the image is searched in the `Assets/WP/` directory.
   * @param {Array<Object>} [notificationData.Buttons] -
   *        An array of button objects to include in the notification.
   *        Each button can have the following properties:
   *        - `Content` (string): The text displayed on the button.
   *        - `Action` (string): The action identifier for the button.
   *        - `ArgName` (string, optional): The name of the argument to pass when the button is clicked.
   *        - `Arg` (string, optional): The value of the argument to pass.
   * @param {string} [notificationData.Tag] -
   *        A tag to uniquely identify the notification for future reference.
   * @param {string} [notificationData.Group] -
   *        A group identifier to categorize notifications.
   * @param {Date} [notificationData.ExpirationTime] -
   *        The time after which the notification expires. Defaults to a maximum of 3 days.
   *
   * @example
   * const notification = {
   *     Id: "12345",
   *     Title: "New Message",
   *     Message: "You have received a new message.",
   *     Image: "cat.jpeg", // Searches in Assets/WP/cat.jpeg
   *     AppLogoOverride: "https://static.pexels.com/photos/45201/kitty-cat-kitten-pet-45201.jpeg",
   *     Buttons: [
   *         {
   *             Content: "Reply",
   *             Action: "reply",
   *             ArgName: "replyText",
   *             Arg: "userReply"
   *         },
   *         {
   *             Content: "Like",
   *             Action: "like"
   *         }
   *     ],
   *     Tag: "message_12345",
   *     Group: "messages",
   *     ExpirationTime: new Date(Date.now() + 2 * 24 * 60 * 60 * 1000)
   * };
   */

  async showNotification(notificationData) {
    const result = await this.callNative(
      "ShowNotification",
      JSON.stringify(notificationData),
    );
    return this._handleStructuredResponse(result);
  }

  async clearNotification() {
    const result = await this.callNative("ClearNotifications");
    return this._handleStructuredResponse(result);
  }

  async vibrateController(durationMs = "300", strength = "0.5") {
    const result = await this.callNative("VibrateController", durationMs, strength);
    return this._handleStructuredResponse(result);
  }

  // App Launcher API methods
  async canOpenUrl(url) {
    const result = await this.callNative("canOpenUrl", url);
    return this._handleStructuredResponse(result);
  }

  async openUrl(url) {
    const result = await this.callNative("openUrl", url);
    return this._handleStructuredResponse(result);
  }

  /**
   * // Listen for deep link events forwarded from C#
    uwp.on('protocolActivated', ({ uri, scheme, host, path, query }) => {
        console.log('Deep link received:', { uri, scheme, host, path, query });
    });
   */
}

export default UwpBridge;
