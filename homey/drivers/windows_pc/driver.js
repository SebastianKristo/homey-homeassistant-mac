'use strict';

const MacDriver = require('../../lib/MacDriver');

/** Windows PCs run "MacBridge for Windows", which speaks the same signed protocol as the Mac app. */
class WindowsDriver extends MacDriver {
  get platform() {
    return 'windows';
  }
}

module.exports = WindowsDriver;
