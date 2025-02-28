class UwpBridge {
  constructor() {
    this.eventListeners = {};
    this.plugins = [];

    window.chrome.webview.addEventListener("message", (event) => {
      const response = event.data;
      const result = JSON.parse(response);

      if (result.event) {
        this.emit(result.event, result.data);
      }
    });
  }

  async callNative(methodName, ...args) {
    const message = { method: methodName, args };
    window.chrome.webview.postMessage(JSON.stringify(message));

    return new Promise((resolve, reject) => {
      const handler = (event) => {
        const response = event.data;
        const result = JSON.parse(response);

        if (result.error) {
          reject(new Error(result.error));
        } else {
          resolve(result.result);
        }
        window.chrome.webview.removeEventListener("message", handler);
      };
      window.chrome.webview.addEventListener("message", handler);
    });
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

  async readFile(fileName, codec = null) {
    return await this.callNative("read", fileName, codec);
  }

  async writeFile(fileName, data) {
    return await this.callNative("write", fileName, data);
  }

  async readDir(folderPath) {
    return await this.callNative("readDir", folderPath);
  }

  async readLocalDir() {
    return await this.callNative("readLocalDir");
  }

  async showAlert(title, text) {
    return await this.callNative("showAlert", title, text);
  }

  async showDialog(title, body, yesButtonText = "Yes", noButtonText = "No") {
    return await this.callNative(
      "showDialog",
      title,
      body,
      yesButtonText,
      noButtonText,
    );
  }

  async downloadFile(fileUrlOrData, encoding = "url", name = null) {
    return await this.callNative("downloadFile", fileUrlOrData, encoding, name);
  }

  async selectFile() {
    return await this.callNative("selectFile");
  }

  async setDownloadLocation(path) {
    return await this.callNative("setDownloadLocation", path);
  }

  async createFolder(folderPathOrName) {
    return await this.callNative("createFolder", folderPathOrName);
  }

  async pickFolder() {
    return await this.callNative("pickFolder");
  }

  async redirect(url) {
    return await this.callNative("redirect", url);
  }

  async zipFolder(folderPath, outputPath = null) {
    return await this.callNative("zipFolder", folderPath, outputPath);
  }

  async unzip(zipPath, outputPath = null) {
    return await this.callNative("unzip", zipPath, outputPath);
  }

  async deleteFile(filePath) {
    return await this.callNative("deleteFile", filePath);
  }

  async deleteFolder(folderPath) {
    return await this.callNative("deleteFolder", folderPath);
  }

  async getMachineStatus() {
    return await this.callNative("GetMachineStatus");
  }

  async getPlatform() {
    return await this.callNative("getPlatform");
  }

  async quitApp() {
    return await this.callNative("quitApp");
  }

  async hideCursor() {
    return await this.callNative("HideCursor");
  }

  async showCursor() {
    return await this.callNative("ShowCursor");
  }

  async setHeaders(headersObject) {
    const headersJson = JSON.stringify(headersObject);
    return await this.callNative("setheaders", headersJson);
  }

  async clearHeaders() {
    return await this.callNative("clearheaders");
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
    return await this.callNative(
      "ShowNotification",
      JSON.stringify(notificationData),
    );
  }

  async clearNotification() {
    return await this.callNative("ClearNotifications");
  }
}

export default UwpBridge;
