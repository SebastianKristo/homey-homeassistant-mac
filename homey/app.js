'use strict';

const Homey = require('homey');

const COMMAND_ACTIONS = [
  'sleep', 'display_sleep', 'wake_display', 'lock', 'restart', 'shutdown',
  'play_pause', 'say', 'notify', 'open_url',
];

class MacBridgeApp extends Homey.App {
  async onInit() {
    for (const id of COMMAND_ACTIONS) {
      this.homey.flow.getActionCard(id).registerRunListener(async ({ device, value }) => {
        await device.sendCommand(id, value);
      });
    }

    this.homey.flow.getDeviceTriggerCard('cpu_above')
      .registerRunListener(async ({ threshold }, { cpu, previous }) => cpu > threshold && previous <= threshold);

    this.log('Mac Bridge started');
  }
}

module.exports = MacBridgeApp;
