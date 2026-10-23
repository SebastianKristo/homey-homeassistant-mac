'use strict';

const Homey = require('homey');
const MacBridgeClient = require('./MacBridgeClient');

class MacDriver extends Homey.Driver {
  async onInit() {
    this.cpuAboveTrigger = this.homey.flow.getDeviceTriggerCard('cpu_above');
    this.wentOfflineTrigger = this.homey.flow.getDeviceTriggerCard('went_offline');
    this.cameOnlineTrigger = this.homey.flow.getDeviceTriggerCard('came_online');
  }

  discovered() {
    const results = this.getDiscoveryStrategy().getDiscoveryResults();
    return Object.values(results).map(r => ({
      id: r.txt.id || r.id,
      name: r.name || r.host,
      host: r.address,
      port: r.port || 8765,
    }));
  }

  async validate({ host, port, token }) {
    const client = new MacBridgeClient({ host, port: Number(port) || 8765, token });
    const status = await client.status();
    return {
      name: status.device.name,
      data: { id: status.device.serial },
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
      if (result.data.id !== device.getData().id) throw new Error('This is a different Mac');
      await device.setSettings(result.settings);
      await device.reconnect();
      return null;
    });
  }
}

module.exports = MacDriver;
