'use strict';

const Homey = require('homey');
const MacBridgeClient = require('./MacBridgeClient');

class MacDriver extends Homey.Driver {
  async onInit() {
    this.cpuAboveTrigger = this.homey.flow.getDeviceTriggerCard('cpu_above');
    this.wentOfflineTrigger = this.homey.flow.getDeviceTriggerCard('went_offline');
    this.cameOnlineTrigger = this.homey.flow.getDeviceTriggerCard('came_online');
  }

  /** 'mac' or 'windows'. Macs predate the platform field, so a missing one means a Mac. */
  get platform() {
    return 'mac';
  }

  discovered() {
    const results = this.getDiscoveryStrategy().getDiscoveryResults();
    return Object.values(results)
      .filter(r => ((r.txt && r.txt.platform) || 'mac') === this.platform)
      .map(r => ({
        id: r.txt.id || r.id,
        name: r.name || r.host,
        host: r.address,
        port: r.port || 8765,
      }));
  }

  async validate({ host, port, token }) {
    const client = new MacBridgeClient({ host, port: Number(port) || 8765, token });
    const status = await client.status();
    const device = status.device || {};
    if (!device.id) throw new Error('Update MacBridge on the Mac to version 1.1 or newer');
    if ((device.platform || 'mac') !== this.platform) {
      throw new Error(device.platform === 'windows'
        ? 'This is a Windows PC. Add it as a Windows PC device instead'
        : 'This is a Mac. Add it as a Mac mini or MacBook Pro device instead');
    }
    return {
      name: device.name || 'Mac',
      data: { id: device.id },
      store: { mac_address: device.mac_address || null },
      settings: { host, port: Number(port) || 8765, token, interval: 10 },
    };
  }

  async onPair(session) {
    session.setHandler('discovered', async () => this.discovered());
    session.setHandler('configure', async data => this.validate(data));
  }

  async onRepair(session, device) {
    session.setHandler('discovered', async () => this.discovered());
    session.setHandler('current', async () => device.getSettings());
    session.setHandler('configure', async data => {
      const result = await this.validate(data);
      // Devices paired with version 1.0 used the hardware serial as id; it is no longer shared.
      const legacy = !/^[0-9a-f]{24}$/.test(device.getData().id);
      if (!legacy && result.data.id !== device.getData().id) throw new Error('This is a different computer');
      await device.setSettings(result.settings);
      await device.reconnect();
      return null;
    });
  }
}

module.exports = MacDriver;
