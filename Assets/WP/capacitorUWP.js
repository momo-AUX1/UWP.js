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
    
    await HapticsPlugin.init(bridge);
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

const HapticsPlugin = {
  bridge: null,
  _selectionActive: false,

  init: async function (bridge) {
    this.bridge = bridge;

    const ImpactStyle = {
      Heavy: 'HEAVY',
      Medium: 'MEDIUM',
      Light: 'LIGHT',
    };

    const NotificationType = {
      Success: 'SUCCESS',
      Warning: 'WARNING',
      Error: 'ERROR',
    };

    const api = {
      impact: (options) => this._impact(options),
      notification: (options) => this._notification(options),
      vibrate: (options) => this._vibrate(options),
      selectionStart: () => this._selectionStart(),
      selectionChanged: () => this._selectionChanged(),
      selectionEnd: () => this._selectionEnd(),

      ImpactStyle,
      NotificationType,
    };

    window.Capacitor.Haptics = api;
    console.log( "HapticsPlugin: Installed Haptics API on window.Capacitor.Haptics",);

    if (!window.Capacitor.Haptics) window.Capacitor.Haptics = api;
    if (!window.Capacitor.Haptics.ImpactStyle) window.Capacitor.Haptics.ImpactStyle = ImpactStyle;
    if (!window.Capacitor.Haptics.NotificationType) window.Capacitor.Haptics.NotificationType = NotificationType;
  },

  _sleep: function (ms) {
    return new Promise((resolve) => setTimeout(resolve, ms));
  },

  _pulse: async function (durationMs, strength) {
    const dur = Math.max(0, Math.round(Number(durationMs || 0)));
    const pow = Math.max(0.1, Math.min(1.0, Number(strength || 1)));
    try {
     await this.bridge.vibrateController(String(dur), String(pow));
    } catch (e) {
      console.debug('Haptics pulse failed:', e);
    }
  },

  _vibrate: async function (options) {
    const duration = (options && typeof options.duration === 'number')
      ? options.duration
      : 300;
    await this._pulse(duration, 1.0);
  },

  _impact: async function (options) {
    const style = (options && options.style) || 'HEAVY';
    switch (String(style).toUpperCase()) {
      case 'LIGHT':
        await this._pulse(300, 0.35);
        break;
      case 'MEDIUM':
        await this._pulse(300, 0.6);
        break;
      case 'HEAVY':
      default:
        await this._pulse(300, 1.0);
        break;
    }
  },

  _notification: async function (options) {
    const type = (options && options.type) || 'SUCCESS';
    const upper = String(type).toUpperCase();
    if (upper === 'SUCCESS') {
      await this._pulse(40, 0.7);
      await this._sleep(30);
      await this._pulse(40, 0.7);
    } else if (upper === 'WARNING') {
      await this._pulse(30, 0.5);
      await this._sleep(30);
      await this._pulse(30, 0.5);
      await this._sleep(30);
      await this._pulse(30, 0.5);
    } else if (upper === 'ERROR') {
      await this._pulse(60, 1.0);
      await this._sleep(40);
      await this._pulse(60, 1.0);
    } else {
      await this._pulse(45, 0.6);
    }
  },

  _selectionStart: async function () {
    this._selectionActive = true;
    await this._pulse(20, 0.3);
  },

  _selectionChanged: async function () {
    if (this._selectionActive) {
      await this._pulse(18, 0.35);
    }
  },

  _selectionEnd: async function () {
    if (this._selectionActive) {
      await this._pulse(22, 0.4);
      this._selectionActive = false;
    }
  },
};

export default CapacitorUWP;