'use strict';

const Homey = require('homey');

const COMMAND_ACTIONS = [
  'wake', 'sleep', 'display_sleep', 'wake_display', 'lock', 'restart', 'shutdown',
  'play_pause', 'next', 'previous', 'say', 'notify',
];

class MacBridgeApp extends Homey.App {
  async onInit() {
    for (const id of COMMAND_ACTIONS) {
      this.homey.flow.getActionCard(id).registerRunListener(async ({ device, value }) => {
        await device.sendCommand(id, value);
      });
    }

    this.homey.flow.getActionCard('notify_buttons')
      .registerRunListener(async ({ device, title, message, buttons }) => device.notifyWithButtons({ title, message, buttons }));

    const conditions = { is_locked: 'mac_locked', microphone_active: 'mac_microphone_active', webcam_active: 'mac_webcam_active' };
    for (const [id, capability] of Object.entries(conditions)) {
      this.homey.flow.getConditionCard(id)
        .registerRunListener(async ({ device }) => device.hasCapability(capability) && device.getCapabilityValue(capability) === true);
    }

    this.homey.flow.getDeviceTriggerCard('cpu_above')
      .registerRunListener(async ({ threshold }, { cpu, previous }) => cpu > threshold && previous <= threshold);

    this.log('Mac Bridge started');
  }
}

module.exports = MacBridgeApp;
