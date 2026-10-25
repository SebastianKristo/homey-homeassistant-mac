'use strict';

const Homey = require('homey');
const MacBridgeClient = require('./MacBridgeClient');

// Buttons on the device tile. Added to devices paired with an older version too.
const BUTTONS = {
  'button.wake': 'wake',
  'button.sleep': 'sleep',
  'button.restart': 'restart',
  'button.shutdown': 'shutdown',
};

// Sensors from the computer's "sensors" section. A capability is added the first time the
// computer reports the value, so older MacBridge versions don't show empty tiles.
const SENSORS = {
  mac_active_app: s => (typeof s.active_app === 'string' ? s.active_app : undefined),
  mac_logged_user: s => (typeof s.logged_user === 'string' ? s.logged_user : undefined),
  mac_idle_minutes: s => (typeof s.idle_seconds === 'number' ? Math.floor(s.idle_seconds / 60) : undefined),
  measure_gpu: s => (typeof s.gpu_usage === 'number' ? s.gpu_usage : undefined),
  mac_locked: s => (typeof s.session_locked === 'boolean' ? s.session_locked : undefined),
  mac_display_on: s => (typeof s.display_on === 'boolean' ? s.display_on : undefined),
  mac_microphone_active: s => (typeof s.microphone_active === 'boolean' ? s.microphone_active : undefined),
  mac_webcam_active: s => (typeof s.webcam_active === 'boolean' ? s.webcam_active : undefined),
};

// Capability -> flow trigger fired when its value changes.
const CHANGE_TRIGGERS = {
  mac_locked: ['locked_changed', 'locked'],
  mac_microphone_active: ['microphone_changed', 'active'],
  mac_webcam_active: ['webcam_changed', 'active'],
};

class MacDevice extends Homey.Device {
  async onInit() {
    this.online = null;
    this.lastEventId = null; // events older than the first status are not replayed
    for (const capability of Object.keys(BUTTONS)) {
      if (!this.hasCapability(capability)) await this.addCapability(capability).catch(this.error);
    }
    for (const [capability, command] of Object.entries(BUTTONS)) {
      this.registerCapabilityListener(capability, () => this.sendCommand(command));
    }
    this.registerCapabilityListener('onoff', on => this.sendCommand(on ? 'wake' : 'sleep'));
    this.registerCapabilityListener('speaker_playing', playing => this.sendCommand(playing ? 'play' : 'pause'));
    this.registerCapabilityListener('speaker_next', () => this.sendCommand('next'));
    this.registerCapabilityListener('speaker_prev', () => this.sendCommand('previous'));
    this.registerCapabilityListener('volume_set', value => this.sendCommand('set_volume', Math.round(value * 100)));
    this.registerCapabilityListener('volume_mute', muted => this.sendCommand(muted ? 'mute' : 'unmute'));
    await this.reconnect();
  }

  // The computer got a new IP (mDNS discovery).
  onDiscoveryAddressChanged(result) {
    this.setSettings({ host: result.address }).then(() => this.reconnect()).catch(this.error);
  }

  async onSettings({ changedKeys }) {
    if (changedKeys.some(k => ['host', 'port', 'token', 'interval'].includes(k))) {
      this.homey.setTimeout(() => this.reconnect(), 100);
    }
  }

  async reconnect() {
    const { host, port, token, interval } = this.getSettings();
    this.client = new MacBridgeClient({ host, port, token });
    this.homey.clearInterval(this.pollTimer);
    this.pollTimer = this.homey.setInterval(() => this.poll(), Math.max(2, interval || 10) * 1000);
    await this.poll();
  }

  async poll() {
    try {
      this.applyStatus(await this.client.status());
    } catch (err) {
      this.setOffline(err);
    }
  }

  async sendCommand(command, value) {
    if (command === 'wake') return this.wake();
    const result = await this.client.command(command, value);
    if (result.status) this.applyStatus(result.status);
    return null;
  }

  /** Wake-on-LAN for a sleeping Mac, plus waking the display if it is already awake. */
  async wake() {
    const mac = this.getStoreValue('mac_address');
    if (mac) await MacBridgeClient.wakeOnLan(mac, this.getSettings().host).catch(this.error);
    try {
      const result = await this.client.command('wake');
      if (result.status) this.applyStatus(result.status);
    } catch (err) {
      if (!mac) throw new Error('The computer is not reachable, and its MAC address for Wake-on-LAN is not known yet');
      this.homey.setTimeout(() => this.poll(), 10000); // still waking up
    }
    return null;
  }

  setOffline(err) {
    if (this.online !== false) {
      this.log('Computer offline:', err.message);
      if (this.online === true) this.driver.wentOfflineTrigger.trigger(this).catch(this.error);
    }
    this.online = false;
    this.setCapability('onoff', false);
    this.setCapability('speaker_playing', false);
    if (err.message === 'Invalid token') this.setUnavailable('Invalid token – run Repair').catch(this.error);
  }

  applyStatus(status) {
    if (this.online === false) this.driver.cameOnlineTrigger.trigger(this).catch(this.error);
    this.online = true;
    this.setAvailable().catch(this.error);

    const stats = status.stats || {};
    const media = status.media || {};
    const device = status.device || {};

    if (typeof device.mac_address === 'string' && device.mac_address !== this.getStoreValue('mac_address')) {
      this.setStoreValue('mac_address', device.mac_address).catch(this.error);
    }

    const previousCpu = this.getCapabilityValue('measure_cpu') || 0;
    if (typeof stats.cpu_usage === 'number') {
      this.driver.cpuAboveTrigger
        .trigger(this, { cpu: stats.cpu_usage }, { cpu: stats.cpu_usage, previous: previousCpu })
        .catch(this.error);
    }

    this.setCapability('onoff', true);
    this.setCapability('speaker_playing', media.state === 'playing');
    this.setCapability('speaker_track', media.title || '');
    this.setCapability('speaker_artist', media.artist || '');
    this.setCapability('speaker_album', media.album || '');
    this.setCapability('volume_set', typeof status.volume === 'number' ? status.volume / 100 : null);
    this.setCapability('volume_mute', !!status.muted);
    this.setCapability('measure_cpu', stats.cpu_usage);
    this.setCapability('measure_memory', stats.memory_usage);
    this.setCapability('measure_disk', stats.disk_usage);
    this.setCapability('measure_disk_free', stats.disk_free_gb);
    this.setCapability('measure_load', stats.load_1m);
    this.setCapability('mac_thermal_state', stats.thermal_state);
    this.applySensors(status.sensors || {});
    this.handleEvents(status.events);

    if (typeof stats.battery_level === 'number') {
      this.setCapability('measure_battery', stats.battery_level);
    } else if (this.hasCapability('measure_battery')) {
      this.removeCapability('measure_battery').catch(this.error); // desktop Mac
    }
  }

  applySensors(sensors) {
    for (const [capability, read] of Object.entries(SENSORS)) {
      const value = read(sensors);
      if (value === undefined) continue;
      if (!this.hasCapability(capability)) {
        this.addCapability(capability).then(() => this.setCapability(capability, value)).catch(this.error);
        continue;
      }
      const previous = this.getCapabilityValue(capability);
      this.setCapability(capability, value);
      const change = CHANGE_TRIGGERS[capability];
      if (change && previous !== null && previous !== value) {
        this.homey.flow.getDeviceTriggerCard(change[0]).trigger(this, { [change[1]]: value }).catch(this.error);
      }
    }
  }

  /** Notification button clicks and system state changes reported by the computer. */
  handleEvents(events) {
    if (!Array.isArray(events)) return;
    const valid = events.filter(e => e && Number.isInteger(e.id)).sort((a, b) => a.id - b.id);
    if (this.lastEventId === null) {
      this.lastEventId = valid.length ? valid[valid.length - 1].id : 0;
      return;
    }
    for (const event of valid) {
      if (event.id <= this.lastEventId) continue;
      this.lastEventId = event.id;
      if (event.type === 'notification_action' && typeof event.action === 'string') {
        this.homey.flow.getDeviceTriggerCard('notification_button')
          .trigger(this, { button: event.action.slice(0, 64), title: String(event.title || '').slice(0, 100) })
          .catch(this.error);
      } else if (event.type === 'state_change' && typeof event.state === 'string') {
        this.homey.flow.getDeviceTriggerCard('state_changed')
          .trigger(this, { state: event.state.slice(0, 32) })
          .catch(this.error);
      }
    }
  }

  /** Notification with up to 4 buttons, from a comma separated list. */
  async notifyWithButtons({ title, message, buttons }) {
    const actions = String(buttons || '').split(',').map(b => b.trim()).filter(Boolean).slice(0, 4)
      .map(b => ({ action: b.slice(0, 40), title: b.slice(0, 40) }));
    return this.sendCommand('notify', {
      title: String(title || 'Homey').slice(0, 100),
      message: String(message || '').slice(0, 500),
      data: { actions },
    });
  }

  setCapability(capability, value) {
    if (value === undefined || !this.hasCapability(capability)) return;
    if (this.getCapabilityValue(capability) === value) return;
    this.setCapabilityValue(capability, value).catch(this.error);
  }

  onDeleted() {
    this.homey.clearInterval(this.pollTimer);
  }
}

module.exports = MacDevice;
