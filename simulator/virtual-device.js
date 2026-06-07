/**
 * SmartEdgeHMI Virtual Device Simulator (下位机模拟器)
 *
 * Usage:
 *   npm install serialport
 *   node virtual-device.js [COM_PORT] [BAUD_RATE]
 *
 * Default: COM2, 115200
 *
 * Telemetry format (sent every 500ms):
 *   {"dev_id":"Sensor_01","temp":25.5,"count":100,"status":1,"err_code":0}
 *
 * Command format (received, newline-delimited JSON):
 *   {"cmd_id":"...","dev_id":"Sensor_01","action":3,"timestamp":...,"parameters":...}
 */

import { SerialPort } from "serialport";
import { ReadlineParser } from "@serialport/parser-readline";

// ── Configuration ───────────────────────────────────────────────────────────

const CONFIG = {
  portName: process.argv[2] || "COM2",
  baudRate: parseInt(process.argv[3] || "115200", 10),
  deviceId: "Sensor_01",
  telemetryIntervalMs: 500,

  // Temperature simulation
  tempBase: 45.0, // baseline temperature
  tempDriftMax: 0.8, // max random walk step per tick
  tempSpikeChance: 0.05, // 5% chance of a larger spike each tick
  tempSpikeMax: 4.0, // max spike magnitude
  tempMin: 20.0,
  tempMax: 85.0,

  // Default alarm threshold (can be changed via Configure command)
  alarmThreshold: 60.0,
};

// ── Device State ────────────────────────────────────────────────────────────

const DeviceStatus = {
  Offline: 0,
  Online: 1,
  Stopped: 2,
  Maintenance: 3,
  Fault: 4,
};

const ErrorCode = {
  NoError: 0,
  ThresholdExceeded: 302,
};

const DeviceAction = {
  None: 0,
  Start: 1,
  Stop: 2,
  Reset: 3,
  Configure: 4,
  TriggerSample: 5,
};

const state = {
  status: DeviceStatus.Online,
  temperature: CONFIG.tempBase,
  outputCount: 0,
  alarmThreshold: CONFIG.alarmThreshold,
  errorCode: ErrorCode.NoError,
};

// ── Serial Port Setup ───────────────────────────────────────────────────────

const port = new SerialPort({
  path: CONFIG.portName,
  baudRate: CONFIG.baudRate,
  autoOpen: false,
});

const parser = port.pipe(new ReadlineParser({ delimiter: "\n" }));

port.open((err) => {
  if (err) {
    console.error(`[FATAL] Cannot open ${CONFIG.portName}: ${err.message}`);
    console.log(
      "Make sure the virtual COM port exists (VSPD) and is not in use.",
    );
    process.exit(1);
  }
  console.log(
    `[INFO] Virtual device connected on ${CONFIG.portName} @ ${CONFIG.baudRate} baud`,
  );
  console.log(`[INFO] Device ID: ${CONFIG.deviceId}`);
  console.log(`[INFO] Sending telemetry every ${CONFIG.telemetryIntervalMs}ms`);
  console.log(`[INFO] Alarm threshold: ${state.alarmThreshold}°C`);
  console.log("─".repeat(55));
});

port.on("error", (err) => {
  console.error(`[PORT ERROR] ${err.message}`);
});

port.on("close", () => {
  console.warn(`[PORT CLOSED] 串口连接已断开，等待重新连接或排查...`);
});

// ── Outgoing: periodic telemetry ────────────────────────────────────────────

function simulateTemperature() {
  // Random walk
  const drift = (Math.random() - 0.5) * 2 * CONFIG.tempDriftMax;
  state.temperature += drift;

  // Occasional spike to test alarm handling
  if (Math.random() < CONFIG.tempSpikeChance) {
    const spike = (Math.random() - 0.3) * CONFIG.tempSpikeMax;
    state.temperature += spike;
  }

  // Clamp
  state.temperature = Math.max(
    CONFIG.tempMin,
    Math.min(CONFIG.tempMax, state.temperature),
  );
  state.temperature = Math.round(state.temperature * 10) / 10;

  // Update error code based on threshold
  state.errorCode =
    state.temperature > state.alarmThreshold
      ? ErrorCode.ThresholdExceeded
      : ErrorCode.NoError;

  // If in fault due to threshold, update status accordingly
  if (
    state.errorCode === ErrorCode.ThresholdExceeded &&
    state.status === DeviceStatus.Online
  ) {
    state.status = DeviceStatus.Fault;
  } else if (
    state.errorCode === ErrorCode.NoError &&
    state.status === DeviceStatus.Fault
  ) {
    state.status = DeviceStatus.Online;
  }
}

function sendTelemetry() {
  simulateTemperature();
  state.outputCount++;

  const payload = {
    dev_id: CONFIG.deviceId,
    temp: state.temperature,
    count: state.outputCount,
    status: state.status,
    err_code: state.errorCode,
    quality: 0, // DataQuality.Good
  };

  const line = JSON.stringify(payload) + "\n";

  // 检查串口是否已成功打开
  if (!port.isOpen) return;

  // 写入并带上 drain 控制，虽然虚拟串口很少堵塞，但这是好习惯
  const canWrite = port.write(line, (err) => {
    if (err) {
      console.error(`[ERROR] Write failed: ${err.message}`);
    }
  });

  // Console log (compact)
  const flag =
    state.errorCode === ErrorCode.ThresholdExceeded ? " ⚠ ALARM" : "";
  console.log(
    `[TX] temp=${state.temperature.toFixed(1)}°C  count=${state.outputCount}  status=${state.status}  err=${state.errorCode}${flag}`,
  );
}

// ── Incoming: command handling ──────────────────────────────────────────────

function handleCommand(cmd) {
  console.log(
    `\n[RX] Command received: action=${cmd.action} (${Object.keys(DeviceAction).find((k) => DeviceAction[k] === cmd.action) || "?"})`,
  );

  switch (cmd.action) {
    case DeviceAction.Reset:
      console.log("[CMD] >>> Executing RESET <<<");
      state.temperature = CONFIG.tempBase;
      state.outputCount = 0;
      state.status = DeviceStatus.Online;
      state.errorCode = ErrorCode.NoError;
      break;

    case DeviceAction.Configure:
      if (cmd.parameters != null) {
        let newThreshold;
        // 支持两种格式：直接数值或包含 Threshold 属性的对象
        if (typeof cmd.parameters === 'object' && cmd.parameters.Threshold !== undefined) {
          newThreshold = Number(cmd.parameters.Threshold);
        } else {
          newThreshold = Number(cmd.parameters);
        }

        if (!Number.isNaN(newThreshold) && newThreshold > 0) {
          state.alarmThreshold = newThreshold;
          console.log(`[CMD] Threshold updated to ${newThreshold}°C`);
        } else {
          console.log(`[CMD] Invalid threshold value: ${JSON.stringify(cmd.parameters)}`);
        }
      } else {
        console.log(
          "[CMD] Configure command received but no parameters provided",
        );
      }
      break;

    case DeviceAction.Start:
      console.log("[CMD] >>> START <<<");
      state.status = DeviceStatus.Online;
      state.errorCode = ErrorCode.NoError;
      break;

    case DeviceAction.Stop:
      console.log("[CMD] >>> STOP <<<");
      state.status = DeviceStatus.Stopped;
      break;

    case DeviceAction.TriggerSample:
      console.log("[CMD] Triggering single sample");
      sendTelemetry();
      break;

    default:
      console.log(`[CMD] Unknown action code: ${cmd.action}`);
      break;
  }
}

parser.on("data", (line) => {
  const trimmed = line.trim();
  if (!trimmed) return;

  // 简单的合法性前置判断：确认是 JSON 对象格式
  if (!trimmed.startsWith("{") || !trimmed.endsWith("}")) {
    console.warn(`[WARN] 收到不完整的帧结构: ${trimmed}`);
    return;
  }

  try {
    const cmd = JSON.parse(trimmed);
    if (cmd.action !== undefined) {
      handleCommand(cmd);
    }
  } catch (err) {
    console.warn(
      `[WARN] JSON 解析失败 (${err.message}): ${trimmed.substring(0, 80)}`,
    );
  }
});

// ── Lifespan ────────────────────────────────────────────────────────────────

const telemetryTimer = setInterval(sendTelemetry, CONFIG.telemetryIntervalMs);

function cleanup() {
  console.log("\n[INFO] Shutting down virtual device...");
  clearInterval(telemetryTimer);
  if (port.isOpen) {
    port.close((err) => {
      if (err) console.error(`[ERROR] Close failed: ${err.message}`);
      else console.log("[INFO] Serial port closed.");
      process.exit(0);
    });
  } else {
    process.exit(0);
  }
}

process.on("SIGINT", cleanup);
process.on("SIGTERM", cleanup);

console.log("[INFO] Virtual device simulator started. Press Ctrl+C to stop.");
