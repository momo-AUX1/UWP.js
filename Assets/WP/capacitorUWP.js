const CapacitorUWP = {
  init: async function (bridge) {
    window.Capacitor = window.Capacitor || {};

    window.Capacitor.isNativePlatform = function () {
      return true;
    };

    window.alert = function (arg) {
      bridge.showAlert("Alert", arg);
    };

    window.Capacitor.getPlatform = async function () {
      const name = await bridge.getPlatform();
      return name;
    };

    await PreferencesPlugin.init(bridge);
  },
};

const PreferencesPlugin = {
  _store: {},
  _dbFile: "db.json",
  bridge: null,

  init: async function (bridge) {
    this.bridge = bridge;

    try {
      const fileContent = await bridge.readFile(this._dbFile);
      this._store = JSON.parse(fileContent);
    } catch (error) {
      console.log(
        "PreferencesPlugin: Could not load db.json, starting with an empty store.",
        error,
      );
      this._store = {};
      await bridge.writeFile(this._dbFile, JSON.stringify(this._store));
    }

    window.Capacitor.Preferences = {
      configure: this.configure.bind(this),
      get: this.get.bind(this),
      set: this.set.bind(this),
      remove: this.remove.bind(this),
      clear: this.clear.bind(this),
      keys: this.keys.bind(this),
      migrate: this.migrate.bind(this),
      removeOld: this.removeOld.bind(this),
    };

    console.log(
      "PreferencesPlugin: Installed Preferences API on window.Capacitor.Preferences",
    );
  },

  _saveStore: async function () {
    console.log(
      "PreferencesPlugin: Saving store to",
      this._dbFile,
      "with data:",
      this._store,
    );
    await this.bridge.writeFile(this._dbFile, JSON.stringify(this._store));
  },

  configure: async function (options) {
    this._config = options;
  },

  get: async function (options) {
    const value = this._store[options.key] || null;
    return { value };
  },

  set: async function (options) {
    this._store[options.key] = options.value;
    await this._saveStore();
  },

  remove: async function (options) {
    delete this._store[options.key];
    await this._saveStore();
  },

  clear: async function () {
    this._store = {};
    await this._saveStore();
  },

  keys: async function () {
    const keys = Object.keys(this._store);
    return { keys };
  },

  migrate: async function () {
    const result = { migrated: [], existing: Object.keys(this._store) };
    return result;
  },

  removeOld: async function () {
    for (const key of Object.keys(this._store)) {
      if (key.startsWith("_cap_")) {
        delete this._store[key];
      }
    }
    await this._saveStore();
  },
};

export default CapacitorUWP;
